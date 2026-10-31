using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>
/// VPN check on adapter events only, never on a timer. Windows' address and availability events come in bursts while
/// a VPN connects, so the check waits until they have been quiet for settleS. The check asks Windows which adapter
/// carries traffic to 8.8.8.8 (GetBestInterface, so split 0/1 + 128/1 routes count) and classifies it. When the
/// result changes, or the last trace failed, it asks Cloudflare's trace where traffic exits.
/// </summary>
public sealed class VpnWatcher : IVpnStatus, IFeedSource, IDisposable
{
    // By name, not 1.1.1.1: VPN clients often route DNS servers like 1.1.1.1 outside the tunnel (SoftEther does), and
    // the trace would then report the real IP. www.cloudflare.com follows the default route like ordinary traffic.
    private const string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";
    private const uint ProbeAddress = 0x08080808;   // 8.8.8.8, same in either byte order

    [DllImport("iphlpapi.dll")] private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    private readonly Func<VpnConfig> _config;
    private readonly Action<string> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private VpnStatus _status = VpnStatus.Unknown;
    private DateTime _dueUtc = DateTime.MinValue;   // MinValue = nothing pending
    private bool _traceFailed;
    private bool _first = true;

    public VpnWatcher(Func<VpnConfig> config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public VpnStatus Status
    {
        get
        {
            lock (_lock) return _status;
        }
    }

    public void Start()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        lock (_lock) _dueUtc = DateTime.UtcNow;   // first check at start, no settle
        new Thread(Run) { IsBackground = true, Name = "PcHwCtl-Vpn", Priority = ThreadPriority.BelowNormal }.Start();
        _wake.Set();
    }

    public void AddTo(List<KeyValuePair<string, double?>> values, List<KeyValuePair<string, string>> texts) =>
        VpnClassifier.AddTo(Status, values, texts);

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _cts.Cancel();
        _wake.Set();
        _http.Dispose();
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        lock (_lock) _dueUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(_config().SettleS, 1, 60));   // each event restarts the wait
        _wake.Set();
    }

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            TimeSpan wait;
            lock (_lock) wait = _dueUtc == DateTime.MinValue ? Timeout.InfiniteTimeSpan : _dueUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero || wait == Timeout.InfiniteTimeSpan)
            {
                _wake.WaitOne(wait);
                continue;   // an event may have moved the due time
            }
            lock (_lock) _dueUtc = DateTime.MinValue;
            try
            {
                CheckOnce();
            }
            catch (Exception ex)
            {
                _log($"VPN: check failed: {ex.Message}");
            }
        }
    }

    private void CheckOnce()
    {
        var check = Check();
        VpnStatus old;
        lock (_lock) old = _status;
        bool changed = _first || check != old.Check;
        if (changed) _log($"VPN: {check.Kind} ({check.By}).");

        ExitInfo? exit = old.Exit;
        DateTime? traced = old.TracedUtc;
        if ((changed || _traceFailed) && _config().Trace)
        {
            exit = Trace();
            _traceFailed = exit == null;
            traced = DateTime.UtcNow;
            _log(exit == null ? "VPN: Cloudflare trace failed." : $"VPN: exits at {exit.Ip} ({exit.Loc}), warp={exit.Warp}.");
        }
        _first = false;
        lock (_lock) _status = new VpnStatus(check, exit, DateTime.UtcNow, traced);
    }

    private static VpnCheck Check()
    {
        if (GetBestInterface(ProbeAddress, out uint index) != 0) return new VpnCheck("none", "no route");
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPv4InterfaceProperties? v4;
            try
            {
                v4 = ni.GetIPProperties().GetIPv4Properties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            if (v4 == null || v4.Index != index) continue;
            string kind = VpnClassifier.Kind(ni.Description, ni.Name, ni.NetworkInterfaceType == NetworkInterfaceType.Ppp);
            return new VpnCheck(kind, ni.Description);
        }
        return new VpnCheck("none", $"interface {index}");
    }

    private ExitInfo? Trace()
    {
        try
        {
            return VpnClassifier.ParseTrace(_http.GetStringAsync(TraceUrl, _cts.Token).GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }
}
