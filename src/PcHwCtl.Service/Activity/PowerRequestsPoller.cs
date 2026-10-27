using System.Diagnostics;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>
/// Reads `powercfg /requests` every pollS (needs admin; the service is SYSTEM). DISPLAY = something keeps the screen
/// on (video); SYSTEM = something keeps the PC awake (audio, downloads, Windows Update); EXECUTION = an app asks to
/// keep running. The board's sleep and night timers hold off on these.
/// </summary>
public sealed class PowerRequestsPoller : IPowerRequests, IFeedSource, IDisposable
{
    private static readonly (string Section, string Key)[] FeedSections = [("DISPLAY", "disp_req"), ("SYSTEM", "sys_req"), ("EXECUTION", "exec_req")];

    private readonly Func<int> _pollS;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private volatile PowerRequestState _state = PowerRequestState.Unknown;

    public PowerRequestsPoller(Func<int> pollS, Action<string> log)
    {
        _pollS = pollS;
        _log = log;
    }

    public PowerRequestState State => _state;

    public void Start()
    {
        var thread = new Thread(() =>
        {
            string? lastError = null;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _state = new PowerRequestState(PowerRequestParser.Parse(RunPowercfg()));
                    lastError = null;
                }
                catch (Exception ex)
                {
                    _state = PowerRequestState.Unknown;
                    if (lastError != ex.Message) _log($"Power requests: {ex.Message}");
                    lastError = ex.Message;
                }
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(1, _pollS())));
            }
        })
        {
            IsBackground = true,
            Name = "PcHwCtl-PowerRequests"
        };
        thread.Start();
    }

    private static string RunPowercfg()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/requests")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("powercfg did not start");
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(5000)) throw new TimeoutException("powercfg /requests took over 5 s");
        if (p.ExitCode != 0) throw new InvalidOperationException($"powercfg /requests exit code {p.ExitCode}");
        return output;
    }

    /// <summary>disp_req, sys_req, exec_req (count, -1 unknown) and the first requester of each as *_by.</summary>
    public void AddTo(List<KeyValuePair<string, double?>> values, List<KeyValuePair<string, string>> texts)
    {
        var s = _state;
        foreach (var (section, key) in FeedSections)
        {
            values.Add(new(key, s.Count(section)));
            texts.Add(new(key + "_by", s.First(section)));
        }
    }

    public void Dispose() => _cts.Cancel();
}
