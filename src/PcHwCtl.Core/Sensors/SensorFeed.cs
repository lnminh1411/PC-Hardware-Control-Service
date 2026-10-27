using System.Globalization;
using System.Text;

namespace PcHwCtl.Core.Sensors;

/// <summary>
/// The sensor feed for ESP32-Helper: PcHwCtl is the only LibreHardwareMonitor reader on the PC and publishes the
/// hardware keys the board shows (same names as the helper's stats.ini). Refreshing follows the helper's split
/// loop: every IntervalMs only the hardware something reads, in parallel; hardware matched by Every refreshes in
/// the background at most that often (the CPU update costs ~320 ms); a full update and sensor lookup runs at start,
/// on hardware change, and every ResolveS while some key or fan source still matches nothing.
/// </summary>
public sealed class FeedConfig
{
    public bool Enabled { get; set; } = true;
    public int IntervalMs { get; set; } = 500;
    public int ResolveS { get; set; } = 30;

    /// <summary>Hardware type or name pattern -> minimum ms between its updates, refreshed in the background.</summary>
    public Dictionary<string, int> Every { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cpu"] = 1000,
        ["Memory"] = 1000
    };

    /// <summary>Key -> "hardware | sensor type | sensor name [| first|avg|sum|max|min]", as in the helper's stats.ini.</summary>
    public Dictionary<string, string> Keys { get; set; } = new()
    {
        ["cpu_temp"] = "Cpu | Temperature | Core Average",
        ["cpu_power"] = "Cpu | Power | CPU Package",
        ["cpu_fan"] = "ITE* | Fan | Fan #1",
        ["sys_fan"] = "ITE* | Fan | Fan #3",
        ["vrm_temp"] = "ITE* | Temperature | Temperature #5",
        ["ram_temp"] = "Memory | Temperature | DIMM #* | max",
        ["gpu1_temp"] = "GpuAmd | Temperature | GPU Core",
        ["gpu1_fan"] = "GpuAmd | Fan | GPU Fan",
        ["gpu1_clock"] = "GpuAmd | Clock | GPU Core",
        ["gpu1_load"] = "GpuAmd | Load | GPU Core",
        ["gpu1_vram"] = "GpuAmd | SmallData | GPU Memory Used",
        ["gpu1_vramt"] = "GpuAmd | SmallData | GPU Memory Total",
        ["gpu1_power"] = "GpuAmd | Power | GPU Package",
        ["gpu2_temp"] = "GpuNvidia | Temperature | GPU Core",
        ["gpu2_fan"] = "GpuNvidia | Fan | GPU Fan",
        ["gpu2_clock"] = "GpuNvidia | Clock | GPU Core",
        ["gpu2_load"] = "GpuNvidia | Load | GPU Core",
        ["gpu2_vram"] = "GpuNvidia | SmallData | GPU Memory Used",
        ["gpu2_vramt"] = "GpuNvidia | SmallData | GPU Memory Total",
        ["gpu2_power"] = "GpuNvidia | Power | GPU Package",
        ["igpu_load"] = "GpuIntel | Load | D3D* | max"
    };
}

/// <summary>One key's sensor selector. Hardware patterns match a hardware's type or name, or an ancestor's.</summary>
public sealed record SensorRule(string Key, IReadOnlyList<string> Hardware, string SensorType, string SensorName, string Reduce)
{
    private static readonly string[] Reducers = ["first", "avg", "sum", "max", "min"];

    /// <exception cref="FormatException">Not "hardware | type | name [| reduce]".</exception>
    public static SensorRule Parse(string key, string spec)
    {
        string[] p = spec.Split('|', StringSplitOptions.TrimEntries);
        if (p.Length is < 3 or > 4 || p[0].Length == 0 || p[1].Length == 0 || p[2].Length == 0)
        {
            throw new FormatException($"Sensor key '{key}': '{spec}' is not \"hardware | sensor type | sensor name [| reduce]\".");
        }
        string reduce = p.Length == 4 ? p[3].ToLowerInvariant() : "first";
        if (!Reducers.Contains(reduce))
        {
            throw new FormatException($"Sensor key '{key}': reduce '{p[3]}' is not first, avg, sum, max or min.");
        }
        var hardware = p[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new SensorRule(key, hardware, p[1], p[2], reduce);
    }

    /// <param name="hardwareChain">The sensor's hardware (type, name), then its parent, and so on.</param>
    public bool Matches(IReadOnlyList<(string Type, string Name)> hardwareChain, string sensorType, string sensorName) =>
        Wildcard.Match(SensorType, sensorType)
        && Wildcard.Match(SensorName, sensorName)
        && hardwareChain.Any(h => Hardware.Any(p => Wildcard.Match(p, h.Type) || Wildcard.Match(p, h.Name)));

    /// <summary>Values of the matched sensors that have one -> the key's value; null when none has.</summary>
    public double? Apply(IEnumerable<double> values)
    {
        var v = values.Where(x => !double.IsNaN(x)).ToList();
        if (v.Count == 0) return null;
        return Reduce switch
        {
            "avg" => v.Average(),
            "sum" => v.Sum(),
            "max" => v.Max(),
            "min" => v.Min(),
            _ => v[0]
        };
    }
}

public static class Wildcard
{
    /// <summary>Case-insensitive, '*' = any run of characters.</summary>
    public static bool Match(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <param name="Values">Every configured key in order; null = missing (no sensor or no reading).</param>
public sealed record SensorSnapshot(long Seq, long UnixMs, IReadOnlyList<KeyValuePair<string, double?>> Values)
{
    public static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>"seq=", "ts=" (Unix ms), then one "key=value" line per key ("key=" when missing), then an empty line.</summary>
    public string ToText()
    {
        var sb = new StringBuilder(64 + Values.Count * 20);
        sb.Append("seq=").Append(Seq.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("ts=").Append(UnixMs.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var (key, value) in Values)
        {
            sb.Append(key).Append('=');
            if (value is double v) sb.Append(Format(v));
            sb.Append('\n');
        }
        sb.Append('\n');
        return sb.ToString();
    }
}

/// <summary>Latest snapshot plus a wait for the next one, for any number of pipe subscribers.</summary>
public sealed class SensorFeedHub
{
    private readonly object _gate = new();
    private SensorSnapshot? _latest;
    private TaskCompletionSource<SensorSnapshot> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SensorSnapshot? Latest
    {
        get
        {
            lock (_gate) return _latest;
        }
    }

    public void Publish(SensorSnapshot snapshot)
    {
        TaskCompletionSource<SensorSnapshot> waiting;
        lock (_gate)
        {
            _latest = snapshot;
            waiting = _next;
            _next = new TaskCompletionSource<SensorSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        waiting.TrySetResult(snapshot);
    }

    /// <summary>The latest snapshot newer than afterSeq, waiting for one if needed.</summary>
    public async Task<SensorSnapshot> NextAsync(long afterSeq, CancellationToken ct)
    {
        while (true)
        {
            Task<SensorSnapshot> wait;
            lock (_gate)
            {
                if (_latest != null && _latest.Seq > afterSeq) return _latest;
                wait = _next.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
