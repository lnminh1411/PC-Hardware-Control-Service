using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PcHwCtl.Core.Control;

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

    public void Write(IFanControl fans, IRgbControl? rgb, float? mixTemp)
    {
        var sb = new StringBuilder();
        foreach (var (name, state) in fans.States)
        {
            string key = Key(name);
            Add(sb, $"hw.{key}.pct", state.CurrentPct.ToString("0.#", CultureInfo.InvariantCulture));
            if (state.Rpm.HasValue) Add(sb, $"hw.{key}.rpm", state.Rpm.Value.ToString("0", CultureInfo.InvariantCulture));
            if (state.CurveTemp.HasValue) Add(sb, $"hw.{key}.temp", state.CurveTemp.Value.ToString("0.#", CultureInfo.InvariantCulture));
            if (state.Override != null) Add(sb, $"hw.{key}.ovr", "1");
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
