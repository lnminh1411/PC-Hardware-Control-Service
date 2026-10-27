using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PcHwCtl.Core.Control;
using PcHwCtl.Core.Display;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service;

/// <summary>
/// Writes fan and RGB state as key=value lines for ESP32PcStats ("@source hw file:///C:/ProgramData/PcHwCtl/state.txt 1000").
/// Keys follow its rule: [a-z0-9_.], at most 23 chars.
/// </summary>
public sealed class StateFileWriter
{
    private static readonly Regex KeyChars = new("[^a-z0-9_]+", RegexOptions.Compiled);

    private readonly string _path;
    private readonly string _tmpPath;
    private readonly Action<string> _log;
    private bool _failedLogged;

    public StateFileWriter(string path, Action<string> log)
    {
        _path = path;
        _tmpPath = path + ".tmp";
        _log = log;
    }

    /// <summary>Monitors to list as hw.mon.*; null = none.</summary>
    public IDisplayControl? Displays { get; set; }

    /// <summary>Power requests as hw.disp_req, hw.sys_req, hw.exec_req (+ _by); null = none.</summary>
    public IPowerRequests? Power { get; set; }

    /// <summary>Active power plan as hw.plan; null = none.</summary>
    public IPowerPlans? Plans { get; set; }

    public void Write(IFanControl fans, IRgbControl? rgb, float? mixTemp)
    {
        var displays = Displays;
        var sb = new StringBuilder();
        foreach (var (name, state) in fans.States)
        {
            string key = Key(name);
            Add(sb, $"hw.{key}.pct", state.CurrentPct.ToString("0.#", CultureInfo.InvariantCulture));
            if (state.Rpm.HasValue) Add(sb, $"hw.{key}.rpm", state.Rpm.Value.ToString("0", CultureInfo.InvariantCulture));
            if (state.CurveTemp.HasValue) Add(sb, $"hw.{key}.temp", state.CurveTemp.Value.ToString("0.#", CultureInfo.InvariantCulture));
            if (state.Override != null) Add(sb, $"hw.{key}.ovr", "1");
            if (state.ZeroRpm.HasValue) Add(sb, $"hw.{key}.zr", state.ZeroRpm.Value ? "1" : "0");
            if (state.Parked) Add(sb, $"hw.{key}.park", "1");
        }

        if (mixTemp.HasValue) Add(sb, "hw.mix", mixTemp.Value.ToString("0.#", CultureInfo.InvariantCulture));

        if (rgb != null)
        {
            Add(sb, "hw.rgb.bright", rgb.Brightness.ToString("0", CultureInfo.InvariantCulture));
            foreach (var (group, state) in rgb.Groups)
            {
                Add(sb, $"hw.rgb.{Key(group)}", state.OverrideRule ?? state.Rule);
            }
        }

        try
        {
            if (Plans?.Active is { } plan) Add(sb, "hw.plan", plan.Name);
        }
        catch (InvalidOperationException)
        {
            // Plan not readable this tick.
        }

        if (Power?.State is { } power)
        {
            foreach (var (section, key) in new[] { ("DISPLAY", "disp_req"), ("SYSTEM", "sys_req"), ("EXECUTION", "exec_req") })
            {
                Add(sb, $"hw.{key}", power.Count(section).ToString(CultureInfo.InvariantCulture));
                Add(sb, $"hw.{key}_by", power.First(section));
            }
        }

        if (displays != null)
        {
            var monitors = displays.Displays;
            Add(sb, "hw.mon.count", monitors.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var m in monitors)
            {
                string k = $"hw.mon.{m.Id}";
                Add(sb, $"{k}.name", m.Name);
                Add(sb, $"{k}.main", m.Main ? "1" : "0");
                Add(sb, $"{k}.bright", m.Bright.ToString(CultureInfo.InvariantCulture));
                Add(sb, $"{k}.power", m.Power.ToString(CultureInfo.InvariantCulture));
            }
        }

        try
        {
            File.WriteAllText(_tmpPath, sb.ToString());
            File.Move(_tmpPath, _path, overwrite: true);
            _failedLogged = false;
        }
        catch (Exception ex)
        {
            if (!_failedLogged)
            {
                _log($"State file write failed ({_path}): {ex.Message}");
                _failedLogged = true;
            }
        }
    }

    private static string Key(string name) => KeyChars.Replace(name.ToLowerInvariant(), "_").Trim('_');

    private static void Add(StringBuilder sb, string key, string value)
    {
        // ESP32PcStats drops longer keys silently; shorten the name before the last segment instead.
        if (key.Length > 23)
        {
            int lastDot = key.LastIndexOf('.');
            string suffix = key[lastDot..];
            key = key[..(23 - suffix.Length)] + suffix;
        }
        sb.Append(key).Append('=').Append(value).Append('\n');
    }
}
