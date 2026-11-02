using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>
/// Screenshot for the ESP32 helper (moved here from its pc_stats host): every monitor in one PNG, kept in memory
/// only; then a red border with "Screenshot Captured" on every monitor so it is never secret (no border, no post);
/// then a post to a webhook in WebhookIds, deleted DeleteMin later. Pending deletes outlive restarts in
/// %LOCALAPPDATA%\PcHwCtl\shot-deletes.txt, the webhook sealed with DPAPI, and are retried until Discord answers.
/// Runs in the user-session agent (session 0 sees no desktop), or in the console service.
/// </summary>
public sealed class ScreenShooter : IScreenshot, IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Func<ScreenshotConfig> _config;
    private readonly Action<string> _log;
    private readonly string _pendingPath;
    private readonly object _pendingLock = new();
    private readonly CancellationTokenSource _cts = new();
    private int _busy;
    private long _nextShotMs;

    public ScreenShooter(Func<ScreenshotConfig> config, Action<string> log)
    {
        _config = config;
        _log = log;
        _pendingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcHwCtl", "shot-deletes.txt");
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
        if (!cfg.Enabled) return ShotResult.Declined("off");
        if (!ScreenshotLogic.IsAllowed(webhook, cfg))
        {
            _log($"Screenshot {nonce}: webhook not a plain Discord one listed in screenshot.webhookIds, declined.");
            return ShotResult.Declined("webhook");
        }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            _log($"Screenshot {nonce}: one is already in progress, declined.");
            return ShotResult.Declined("busy");
        }
        if (Environment.TickCount64 < _nextShotMs)
        {
            _log($"Screenshot {nonce}: within minIntervalS of the last one, declined.");
            Interlocked.Exchange(ref _busy, 0);
            return ShotResult.Declined("busy");
        }

        byte[] png;
        try
        {
            png = Capture();   // before the border, so the border is never in the image
            _nextShotMs = Environment.TickCount64 + cfg.MinIntervalS * 1000L;
        }
        catch (Exception ex)
        {
            _log($"Screenshot {nonce}: capture failed: {ex.Message}");
            Interlocked.Exchange(ref _busy, 0);
            return ShotResult.Declined("capture");
        }

        new Thread(() =>
        {
            try
            {
                if (ShowBorder(cfg.BorderS)) Upload(nonce, webhook, png, cfg.DeleteMin);
                else _log($"Screenshot {nonce}: border did not show, not posted.");
            }
            catch (Exception ex)
            {
                _log($"Screenshot {nonce}: upload failed: {ex.Message}");
            }
            finally
            {
                Array.Clear(png);
                Interlocked.Exchange(ref _busy, 0);
            }
        }) { IsBackground = true, Name = "PcHwCtl-Shot" }.Start();
        return ShotResult.Ok;
    }

    public void Dispose() => _cts.Cancel();

    /// <summary>The whole virtual desktop (every monitor) as one PNG, in physical pixels. Its pixels are cleared after.</summary>
    private static byte[] Capture()
    {
        IntPtr old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w <= 0 || h <= 0) throw new InvalidOperationException("no screen metrics (locked or no desktop?)");
            using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            // Sized for the raw pixels, so the stream doesn't grow and leave copies behind in old buffers.
            using var ms = new MemoryStream((int)Math.Min(w * 3L * h + 65536, Array.MaxLength));
            try
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            finally
            {
                Array.Clear(ms.GetBuffer());
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.Black);
            }
        }
        finally
        {
            if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
        }
    }

    /// <summary>
    /// A click-through, topmost, borderless window per monitor: thick red frame and a centred red "Screenshot
    /// Captured", for seconds. Own STA thread with a short message loop; returns once every window is painted, or
    /// false when that failed.
    /// </summary>
    private bool ShowBorder(int seconds)
    {
        var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Thread(() =>
        {
            try
            {
                SetThreadDpiAwarenessContext(PerMonitorAwareV2);
                var forms = Screen.AllScreens.Select(s => new BorderForm(s.Bounds)).ToList();
                if (forms.Count == 0)
                {
                    shown.TrySetResult(false);
                    return;
                }
                foreach (var f in forms)
                {
                    f.Show();
                    f.Refresh();
                }
                shown.TrySetResult(true);
                using var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    foreach (var f in forms) f.Close();
                    Application.ExitThread();
                };
                timer.Start();
                Application.Run();
                foreach (var f in forms) f.Dispose();
            }
            catch (Exception ex)
            {
                _log($"Screenshot: border failed: {ex.Message}");
                shown.TrySetResult(false);
            }
        }) { IsBackground = true, Name = "PcHwCtl-ShotBorder" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        return shown.Task.Wait(TimeSpan.FromSeconds(5)) && shown.Task.Result;
    }

    /// <summary>multipart POST with ?wait=true so Discord returns the message, whose id the delete needs.</summary>
    private void Upload(string nonce, string webhook, byte[] png, double deleteMin)
    {
        string text = deleteMin > 0 ? $"Screenshot (auto-deletes in {deleteMin:0.#} min)" : "Screenshot";
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(new { content = text }), System.Text.Encoding.UTF8, "application/json"), "payload_json");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "files[0]", "screenshot.png");

        using var resp = Http.PostAsync(ScreenshotLogic.PostUrl(webhook), form, _cts.Token).GetAwaiter().GetResult();
        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(body);
        string? id = doc.RootElement.TryGetProperty("id", out var e) ? e.GetString() : null;
        if (id == null) throw new InvalidOperationException("Discord sent no message id");

        if (deleteMin > 0)
        {
            var job = PendingDelete.New(DateTime.UtcNow.AddMinutes(deleteMin), Seal(webhook), id);
            lock (_pendingLock) File.AppendAllText(_pendingPath, job.Format() + "\n");
            _log($"Screenshot {nonce}: posted (message {id}), deleted in {deleteMin:0.#} min.");
        }
        else _log($"Screenshot {nonce}: posted (message {id}).");
    }

    /// <summary>The webhook's token can post to the channel, so it goes to disk readable only by this Windows user.</summary>
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
            if (keep.Count > 0) File.WriteAllLines(_pendingPath, keep);
            else File.Delete(_pendingPath);
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

    private sealed class BorderForm : Form
    {
        public BorderForm(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            TransparencyKey = Color.Black;   // only the painted pixels show
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int t = Math.Max(8, Width / 180);
            using (var pen = new Pen(Color.Red, t) { Alignment = PenAlignment.Inset })
                e.Graphics.DrawRectangle(pen, t / 2, t / 2, Width - t, Height - t);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new Font("Segoe UI", Math.Max(24, Height / 16), FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.Red);
            const string msg = "Screenshot Captured";
            SizeF sz = e.Graphics.MeasureString(msg, font);
            e.Graphics.DrawString(msg, font, brush, (Width - sz.Width) / 2, (Height - sz.Height) / 2);
        }
    }

    private const string SealPrefix = "dpapi:";
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private static readonly IntPtr PerMonitorAwareV2 = -4;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
