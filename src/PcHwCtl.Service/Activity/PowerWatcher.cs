using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>
/// Why the PC goes down, went down, and woke up, from the System event log:
/// - 1074 (a process asked for shutdown/restart: who, reason title and code, comment, user) -> going;
/// - Kernel-Power 42 (entering sleep, with Windows' sleep reason) and 109 (kernel shutdown, e.g. power button) -> going;
/// - Kernel-Power 41 / EventLog 6008 at boot, or the last 1074 before it -> last_down;
/// - Power-Troubleshooter 1 (back from sleep, with the wake source) -> wake, and clears a sleep going.
/// Updates: RebootRequired / RebootPending registry keys every 30 s, servicing workers with nobody signed in every 5 s.
/// A going clears after 120 s (a shutdown that was cancelled, or a sleep we came back from).
/// </summary>
public sealed class PowerWatcher : IPowerStatus, IFeedSource, IDisposable
{
    private const int GoingClearS = 120;
    private static readonly Regex Reason = new(@"Reason:\s*(.+?)\s*$", RegexOptions.Multiline);
    private static readonly Regex WakeSource = new(@"Wake Source:\s*(.+?)\s*$", RegexOptions.Multiline);
    private static readonly Regex Action = new(@"Action:\s*(.+?)\s*Event Code", RegexOptions.Singleline);

    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();

    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<EventLogWatcher> _watchers = [];
    private GoingState _going = GoingState.None;
    private LastDown _lastDown = LastDown.Unknown;
    private WakeInfo? _wake;
    private UpdateState _update = UpdateState.None;
    private PowerDoResult? _lastDo;

    public PowerWatcher(Action<string> log)
    {
        _log = log;
    }

    public PowerStatus Status
    {
        get
        {
            lock (_lock) return new PowerStatus(_going, _lastDown, _wake, _update, _lastDo);
        }
    }

    public void Start()
    {
        DateTime boot = DateTime.UtcNow.AddMilliseconds(-(double)GetTickCount64());
        _lastDown = ReadLastDown(boot);
        _wake = ReadLastWake(boot);
        _log($"Power: last down {_lastDown.Kind} at {PowerStatusKeys.Local(_lastDown.At)} by {_lastDown.By} ({_lastDown.Why}).");
        Watch("*[System[(EventID=1074)]]", On1074);
        Watch("*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=109)]]", OnKernelPower);
        Watch("*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]", OnWake);
        var thread = new Thread(Poll) { IsBackground = true, Name = "PcHwCtl-PowerWatch", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>`power do` outcome; an accepted sleep or hibernate is going right away (its event comes as Windows suspends).</summary>
    public void Done(PowerAction action, string outcome, string why)
    {
        string name = action.ToString().ToLowerInvariant();
        lock (_lock)
        {
            _lastDo = new PowerDoResult(name, outcome, PowerReasons.Clean(why), DateTime.UtcNow);
            if (outcome == "accepted" && action is PowerAction.Sleep or PowerAction.Hibernate)
                _going = new GoingState(name, "board", "power do", "-", "-", 0, DateTime.UtcNow);
        }
        _log($"Power: {name} {outcome}{(why == "-" || why == "" ? "" : ": " + why)}.");
    }

    public void AddTo(List<KeyValuePair<string, double?>> values, List<KeyValuePair<string, string>> texts) =>
        PowerStatusKeys.AddTo(Status, values, texts);

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var w in _watchers)
        {
            try
            {
                w.Enabled = false;
                w.Dispose();
            }
            catch (Exception)
            {
                // Shutting down anyway.
            }
        }
    }

    private void Watch(string query, Action<EventRecord> handler)
    {
        try
        {
            var w = new EventLogWatcher(new EventLogQuery("System", PathType.LogName, query));
            w.EventRecordWritten += (_, e) =>
            {
                if (e.EventException != null)
                {
                    _log($"Power: event watcher: {e.EventException.Message}");
                    return;
                }
                if (e.EventRecord == null) return;
                using var rec = e.EventRecord;
                try
                {
                    handler(rec);
                }
                catch (Exception ex)
                {
                    _log($"Power: event {rec.Id}: {ex.Message}");
                }
            };
            w.Enabled = true;
            _watchers.Add(w);
        }
        catch (Exception ex)
        {
            _log($"Power: can't watch the event log ({query}): {ex.Message}");
        }
    }

    private void On1074(EventRecord rec)
    {
        var d = Data(rec);
        bool pending;
        lock (_lock) pending = _update.Pending;
        string kind = PowerReasons.ClassifyShutdown(d.Get("param1"), d.Get("param5"), d.Get("param4"), pending);
        string by = PowerReasons.ProcessName(d.Get("param1"));
        string code = PowerReasons.TryParseCode(d.Get("param4"), out uint c) ? PowerReasons.DescribeCode(c) : PowerReasons.Clean(d.Get("param4"));
        var going = new GoingState(kind, by == "" ? "-" : by, PowerReasons.Why(d.Get("param3"), d.Get("param6")), code,
            PowerReasons.Clean(d.Get("param7")), rec.RecordId ?? 0, DateTime.UtcNow);
        lock (_lock) _going = going;
        _log($"Power: going {going.Kind} by {going.By} ({going.Why}, {going.Code}, {going.User}) id={going.Id}.");
    }

    private void OnKernelPower(EventRecord rec)
    {
        string message = Describe(rec);
        string why = Match(Reason, message);
        GoingState going;
        if (rec.Id == 42)
        {
            int target = int.TryParse(Data(rec).Get("TargetState"), out int t) ? t : 4;
            going = new GoingState(PowerReasons.SleepState(target), "-", why, "-", "-", rec.RecordId ?? 0, DateTime.UtcNow);
        }
        else
        {
            // 109 follows a 1074 for ordinary shutdowns; alone it is the kernel's own (power button, thermal).
            lock (_lock)
                if (_going.Kind != "none" && (DateTime.UtcNow - _going.AtUtc).TotalSeconds < 30) return;
            string action = Match(Action, message);
            string kind = action.Contains("Reset", StringComparison.OrdinalIgnoreCase) ? "restart" : "shutdown";
            going = new GoingState(kind, "-", PowerReasons.Why(why, action), "-", "-", rec.RecordId ?? 0, DateTime.UtcNow);
        }
        lock (_lock) _going = going;
        _log($"Power: going {going.Kind} ({going.Why}) id={going.Id}.");
    }

    private void OnWake(EventRecord rec)
    {
        var wake = Wake(rec);
        lock (_lock)
        {
            _wake = wake;
            if (_going.Kind is "sleep" or "hibernate") _going = GoingState.None;
        }
        _log($"Power: woke from {wake.From} by {wake.By}.");
    }

    private void Poll()
    {
        DateTime nextPending = DateTime.MinValue;
        while (!_cts.Token.WaitHandle.WaitOne(nextPending == DateTime.MinValue ? 0 : 5000))
        {
            try
            {
                UpdateState u;
                lock (_lock) u = _update;
                string by = u.PendingBy;
                if (DateTime.UtcNow >= nextPending)
                {
                    by = PendingBy();
                    nextPending = DateTime.UtcNow.AddSeconds(30);
                }
                string lastKind;
                lock (_lock) lastKind = _lastDown.Kind;
                bool running = UpdateRunning(lastKind);
                if (running != u.Running) _log($"Power: updates {(running ? "installing" : "no longer installing")}.");
                if (by != u.PendingBy) _log($"Power: update restart pending: {by}.");
                lock (_lock)
                {
                    _update = new UpdateState(by != "-", by, running);
                    if (_going.Kind != "none" && (DateTime.UtcNow - _going.AtUtc).TotalSeconds >= GoingClearS) _going = GoingState.None;
                }
            }
            catch (Exception ex)
            {
                _log($"Power: update check: {ex.Message}");
            }
        }
    }

    private static string PendingBy()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        bool wu = Exists(hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        bool cbs = Exists(hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        return wu && cbs ? "wu+cbs" : wu ? "wu" : cbs ? "cbs" : "-";
    }

    private static bool Exists(RegistryKey root, string path)
    {
        using var k = root.OpenSubKey(path);
        return k != null;
    }

    // "Updates are underway" after an update restart: servicing workers with nobody signed in. Windows also services
    // in the background while someone works, and at the sign-in screen after an ordinary boot, so it only counts
    // after an update restart or shutdown.
    private static bool UpdateRunning(string lastDownKind)
    {
        if (!lastDownKind.StartsWith("update", StringComparison.Ordinal) || Running("explorer")) return false;
        return Running("TiWorker") || Running("poqexec") || Running("SetupHost");
    }

    private static bool Running(string name)
    {
        var ps = Process.GetProcessesByName(name);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>
    /// Newest of: Kernel-Power 41 / 6008 written at boot (unclean), or the last 1074 before boot (clean, with who and why).
    /// </summary>
    private LastDown ReadLastDown(DateTime bootUtc)
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, "*[System[(EventID=1074 or EventID=41 or EventID=6008)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (var rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    if (rec.TimeCreated is not { } created) continue;
                    DateTime t = created.ToUniversalTime();
                    bool unclean = rec.Id is 41 or 6008;
                    if (!(t < bootUtc || (unclean && t <= bootUtc.AddMinutes(5)))) continue;
                    var d = Data(rec);
                    if (rec.Id == 41)
                    {
                        var (kind, why) = PowerReasons.Unexpected(
                            ParseUInt(d.Get("BugcheckCode")), (int)ParseUInt(d.Get("SleepInProgress")),
                            ParseULong(d.Get("PowerButtonTimestamp")), d.Get("LongPowerButtonPressDetected") == "true");
                        return new LastDown(kind, t, "-", why);
                    }
                    if (rec.Id == 6008) return new LastDown("unexpected", t, "-", "unclean shutdown");
                    string code = PowerReasons.TryParseCode(d.Get("param4"), out uint c) ? PowerReasons.DescribeCode(c) : "";
                    string by = PowerReasons.ProcessName(d.Get("param1"));
                    return new LastDown(PowerReasons.ClassifyShutdown(d.Get("param1"), d.Get("param5"), d.Get("param4"), false), t,
                        by == "" ? "-" : by, PowerReasons.Why(d.Get("param3"), d.Get("param6")) + (code == "" ? "" : $" ({code})"));
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Power: can't read the last shutdown: {ex.Message}");
        }
        return LastDown.Unknown;
    }

    private WakeInfo? ReadLastWake(DateTime bootUtc)
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            using var rec = reader.ReadEvent();
            if (rec?.TimeCreated is { } t && t.ToUniversalTime() > bootUtc) return Wake(rec);
        }
        catch (Exception ex)
        {
            _log($"Power: can't read the last wake: {ex.Message}");
        }
        return null;
    }

    private static WakeInfo Wake(EventRecord rec)
    {
        var d = Data(rec);
        int target = int.TryParse(d.Get("TargetState"), out int s) ? s : 4;
        string by = Match(WakeSource, Describe(rec));
        if (by == "-") by = PowerReasons.Clean(d.Get("WakeSourceText"));
        return new WakeInfo(PowerReasons.SleepState(target), Time(d.Get("WakeTime")) ?? DateTime.UtcNow, Time(d.Get("SleepTime")) ?? DateTime.MinValue, by);
    }

    private static Dictionary<string, string> Data(EventRecord rec)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        foreach (var e in XDocument.Parse(rec.ToXml()).Descendants(ns + "Data"))
        {
            string? name = (string?)e.Attribute("Name");
            if (name != null) d[name] = e.Value;
        }
        return d;
    }

    private static string Describe(EventRecord rec)
    {
        try
        {
            return rec.FormatDescription() ?? "";
        }
        catch (EventLogException)
        {
            return "";
        }
    }

    private static string Match(Regex re, string text)
    {
        var m = re.Match(text);
        return m.Success ? PowerReasons.Clean(m.Groups[1].Value.Replace("‎", "")) : "-";
    }

    private static DateTime? Time(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static uint ParseUInt(string? s) =>
        s != null && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h) ? h : 0
            : uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : 0;

    private static ulong ParseULong(string? s) => ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v) ? v : 0;
}

internal static class DictionaryExtensions
{
    public static string? Get(this Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v : null;
}
