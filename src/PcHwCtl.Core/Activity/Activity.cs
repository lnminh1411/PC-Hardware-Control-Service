namespace PcHwCtl.Core.Activity;

/// <summary>
/// What the board needs to decide night mode, sleep and hibernate: how busy each program is, and which power
/// requests are held. PcHwCtl only reports; the board owns the thresholds and timers.
/// </summary>
/// <param name="CreateTime">Process creation time; with Pid it tells a reused pid from the old process.</param>
/// <param name="CpuTime">User + kernel time, 100 ns units.</param>
public sealed record ProcessSample(int Pid, long CreateTime, string Exe, long CpuTime);

/// <param name="Cores">CPU used per second of wall time, summed over the exe's instances: 1.0 = one core busy.</param>
public sealed record ProcessLoad(string Exe, double Cores, int Instances);

public interface IProcessActivity
{
    /// <summary>Busiest exes over the last sample, most first.</summary>
    IReadOnlyList<ProcessLoad> Top(int n);
}

/// <summary>Extra feed keys published with every sensor snapshot.</summary>
public interface IFeedSource
{
    void AddTo(List<KeyValuePair<string, double?>> values, List<KeyValuePair<string, string>> texts);
}

public static class ProcessCpu
{
    /// <summary>
    /// Per-exe cores between two samples. A process missing from the earlier sample started in between, so all its
    /// CPU time falls inside the interval. Pid 0 (Idle) is not a program and is left out.
    /// </summary>
    public static List<ProcessLoad> Loads(IReadOnlyList<ProcessSample> before, IReadOnlyList<ProcessSample> now, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return [];
        var earlier = new Dictionary<(int, long), long>();
        foreach (var s in before) earlier[(s.Pid, s.CreateTime)] = s.CpuTime;

        var byExe = new Dictionary<string, (long Cpu, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in now)
        {
            if (s.Pid == 0) continue;
            long delta = earlier.TryGetValue((s.Pid, s.CreateTime), out long was) ? s.CpuTime - was : s.CpuTime;
            if (delta < 0) delta = 0;
            byExe.TryGetValue(s.Exe, out var acc);
            byExe[s.Exe] = (acc.Cpu + delta, acc.Count + 1);
        }

        return byExe
            .Select(kv => new ProcessLoad(kv.Key, kv.Value.Cpu / 1e7 / elapsedSeconds, kv.Value.Count))
            .OrderByDescending(l => l.Cores)
            .ThenBy(l => l.Exe, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <param name="Sections">"DISPLAY", "SYSTEM", "EXECUTION", ... -> requesters in powercfg's order; null = unknown.</param>
public sealed record PowerRequestState(IReadOnlyDictionary<string, IReadOnlyList<string>>? Sections)
{
    public static readonly PowerRequestState Unknown = new((IReadOnlyDictionary<string, IReadOnlyList<string>>?)null);

    /// <summary>Requests held in the section; -1 = unknown.</summary>
    public int Count(string section) =>
        Sections == null ? -1 : Sections.TryGetValue(section, out var list) ? list.Count : 0;

    public string First(string section) =>
        Sections != null && Sections.TryGetValue(section, out var list) && list.Count > 0 ? list[0] : "";
}

public interface IPowerRequests
{
    PowerRequestState State { get; }
}

public static class PowerRequestParser
{
    /// <summary>
    /// `powercfg /requests`: "DISPLAY:" then "None." or "[PROCESS] \Device\...\brave.exe" + reason lines, until the
    /// next "XYZ:" header. Requesters come back as the file name ("svchost.exe (wuauserv)"), drivers as their text.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<string>> Parse(string output)
    {
        var sections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (string raw in output.Split('\n'))
        {
            string l = raw.Trim();
            if (l.Length > 1 && l.EndsWith(':') && l[..^1].All(c => c is >= 'A' and <= 'Z'))
            {
                current = [];
                sections[l[..^1]] = current;
                continue;
            }
            if (current == null || !l.StartsWith('[')) continue;
            int close = l.IndexOf(']');
            string kind = close > 1 ? l[1..close] : "";
            string what = (close >= 0 ? l[(close + 1)..] : l).Trim();
            bool path = kind is "PROCESS" or "SERVICE";
            string name = path && what.Contains('\\') ? what[(what.LastIndexOf('\\') + 1)..] : what;
            current.Add(name.Length > 0 ? name : what);
        }
        return sections;
    }
}
