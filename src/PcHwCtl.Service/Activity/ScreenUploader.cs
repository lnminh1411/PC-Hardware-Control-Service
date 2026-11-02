using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>
/// The service half of a screenshot (IPC `screenshot`): checks the webhook against screenshot.webhookIds, asks the
/// agent for a capture (the agent shows the border), waits until the border has been up for BorderS, posts the PNG
/// and deletes the post DeleteMin later. The webhook never enters the user's session. Pending deletes outlive
/// restarts in a file under the admin-only data directory, the webhook sealed with DPAPI, and are retried until
/// Discord answers.
/// </summary>
public sealed class ScreenUploader : IScreenshot, IDisposable
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };

    private readonly Func<ScreenshotConfig> _config;
    private readonly Func<string, ShotCapture> _capture;
    private readonly Action<string> _log;
    private readonly string _pendingPath;
    private readonly object _pendingLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _idle = new(true);
    private int _busy;
    private long _nextShotMs;

    /// <param name="capture">nonce -> the PNG (border already shown) or why not; the agent over its pipe, or ScreenShooter in a console.</param>
    public ScreenUploader(Func<ScreenshotConfig> config, Func<string, ShotCapture> capture, string pendingPath, Action<string> log)
    {
        _config = config;
        _capture = capture;
        _pendingPath = pendingPath;
        _log = log;
    }

    /// <summary>Starts the thread that deletes posts when they are due.</summary>
    public void Start()
    {
        new Thread(() =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    ProcessPendingDeletes();
                }
                catch (Exception ex)
                {
                    _log($"Screenshot: pending deletes: {ex.Message}");
                }
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
            }
        }) { IsBackground = true, Name = "PcHwCtl-ShotDeletes" }.Start();
    }

    public ShotResult Take(string nonce, string webhook)
    {
        var cfg = _config();
        if (!cfg.Enabled) return Declined(nonce, "off");
        if (!ScreenshotLogic.IsAllowed(webhook, cfg)) return Declined(nonce, "webhook");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return Declined(nonce, "busy");
        bool handedOff = false;
        try
        {
            if (Environment.TickCount64 < _nextShotMs) return Declined(nonce, "busy");
            var shot = _capture(nonce);
            if (shot.Png == null) return Declined(nonce, shot.Why);
            _nextShotMs = Environment.TickCount64 + cfg.MinIntervalS * 1000L;
            _log($"Screenshot {nonce}: captured, posting after the {cfg.BorderS} s border.");

            byte[] png = shot.Png;
            _idle.Reset();
            handedOff = true;
            new Thread(() =>
            {
                try
                {
                    // The border went up when the capture returned: give the person at the PC its full time first.
                    Thread.Sleep(TimeSpan.FromSeconds(cfg.BorderS));
                    Upload(nonce, webhook, png, cfg.DeleteMin);
                }
                catch (Exception ex)
                {
                    _log($"Screenshot {nonce}: post failed: {ex.Message}");
                }
                finally
                {
                    Array.Clear(png);
                    Interlocked.Exchange(ref _busy, 0);
                    _idle.Set();
                }
            }) { IsBackground = true, Name = "PcHwCtl-Shot" }.Start();
            return ShotResult.Ok;
        }
        finally
        {
            if (!handedOff) Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Lets a running post finish (so its delete gets recorded) before the deletes thread stops.</summary>
    public void Dispose()
    {
        _idle.Wait(Http.Timeout + TimeSpan.FromSeconds(10));
        _cts.Cancel();
    }

    private ShotResult Declined(string nonce, string why)
    {
        _log($"Screenshot {nonce}: declined ({why}).");
        return ShotResult.Declined(why);
    }

    /// <summary>multipart POST with ?wait=true so Discord returns the message, whose id the delete needs.</summary>
    private void Upload(string nonce, string webhook, byte[] png, double deleteMin)
    {
        string text = deleteMin > 0 ? $"Screenshot (auto-deletes in {deleteMin:0.#} min)" : "Screenshot";
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(new { content = text }), Encoding.UTF8, "application/json"), "payload_json");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "files[0]", "screenshot.png");

        // Not cancelled by shutdown: a post that lands after a cancel would have no delete recorded.
        using var resp = Http.PostAsync(ScreenshotLogic.PostUrl(webhook), form, CancellationToken.None).GetAwaiter().GetResult();
        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        string? id;
        try
        {
            using var doc = JsonDocument.Parse(body);
            id = doc.RootElement.TryGetProperty("id", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            id = null;
        }
        if (id == null || id.Length == 0 || !id.All(char.IsAsciiDigit))
        {
            _log($"Screenshot {nonce}: posted, but Discord sent no message id: it stays on Discord, remove it by hand.");
            return;
        }

        if (deleteMin <= 0)
        {
            _log($"Screenshot {nonce}: posted (message {id}).");
            return;
        }
        try
        {
            var job = PendingDelete.New(DateTime.UtcNow.AddMinutes(deleteMin), Seal(webhook), id);
            lock (_pendingLock) File.AppendAllText(_pendingPath, job.Format() + "\n");
            _log($"Screenshot {nonce}: posted (message {id}), deleted in {deleteMin:0.#} min.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _log($"Screenshot {nonce}: posted (message {id}) but its delete could not be recorded ({ex.Message}): it stays on Discord, remove it by hand.");
        }
    }

    /// <summary>The webhook's token can post to the channel, so it goes to disk sealed for this account (SYSTEM).</summary>
    private static string Seal(string webhook) =>
        SealPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(webhook), null, DataProtectionScope.CurrentUser));

    private static string Unseal(string stored) => stored.StartsWith(SealPrefix, StringComparison.Ordinal)
        ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[SealPrefix.Length..]), null, DataProtectionScope.CurrentUser))
        : stored;

    private void ProcessPendingDeletes()
    {
        lock (_pendingLock)
        {
            if (!File.Exists(_pendingPath)) return;
            var keep = new List<string>();
            bool changed = false;
            foreach (string line in File.ReadAllLines(_pendingPath))
            {
                if (line.Trim().Length == 0) continue;
                if (!PendingDelete.TryParse(line, out var job))
                {
                    changed = true;   // malformed: drop
                    continue;
                }
                long now = DateTime.UtcNow.Ticks;
                if (now < job.DueTicks)
                {
                    keep.Add(line);
                    continue;
                }
                changed = true;
                if (Delete(job)) continue;
                if (job.Expired(now)) _log($"Screenshot: gave up deleting message {job.MessageId}: it is still on Discord, remove it by hand.");
                else keep.Add(job.Retry(now).Format());
            }
            if (!changed) return;
            if (keep.Count == 0)
            {
                File.Delete(_pendingPath);
                return;
            }
            // Through a temp file: a crash mid-write must not lose the deletes still due.
            string tmp = _pendingPath + ".tmp";
            File.WriteAllLines(tmp, keep);
            File.Move(tmp, _pendingPath, overwrite: true);
        }
    }

    /// <summary>
    /// Sends one due delete; false = try again. A desktop left on Discord is the worse failure, so only a refusal
    /// that can't change (or an entry that can't be read) ends it, and that says so in the log.
    /// </summary>
    private bool Delete(PendingDelete job)
    {
        string webhook;
        try
        {
            webhook = Unseal(job.Webhook);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            _log($"Screenshot: delete of {job.MessageId} unreadable ({ex.Message}): it may still be on Discord, remove it by hand.");
            return true;
        }
        if (!ScreenshotLogic.IsWebhook(webhook))
        {
            _log($"Screenshot: delete of {job.MessageId} names no Discord webhook, dropped: remove it by hand.");
            return true;
        }

        int? status = null;
        string why;
        try
        {
            using var resp = Http.DeleteAsync(ScreenshotLogic.MessageUrl(webhook, job.MessageId), _cts.Token).GetAwaiter().GetResult();
            status = (int)resp.StatusCode;
            why = $"HTTP {status}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            why = ex.Message;
        }

        if (status is >= 200 and < 300 or 404)   // 404: already gone
        {
            _log($"Screenshot: deleted message {job.MessageId}.");
            return true;
        }
        if (ScreenshotLogic.RetryDelete(status))
        {
            _log($"Screenshot: delete of {job.MessageId} failed ({why}), will retry.");
            return false;
        }
        _log($"Screenshot: delete of {job.MessageId} refused ({why}): it is still on Discord, remove it by hand.");
        return true;
    }

    private const string SealPrefix = "dpapi:";
}
