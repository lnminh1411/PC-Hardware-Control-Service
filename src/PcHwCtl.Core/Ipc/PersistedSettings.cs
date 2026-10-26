using System.Text.Json;
using PcHwCtl.Core.Control;

namespace PcHwCtl.Core.Ipc;

/// <summary>
/// IPC settings that survive service restarts and config reloads: Zero RPM per fan and RGB brightness.
/// Everything else set over IPC (fan % overrides, curve switches, RGB rule overrides) stays in memory only.
/// Thread-safe; saved atomically on every change.
/// </summary>
public sealed class PersistedSettings
{
    private sealed record Data(Dictionary<string, bool>? ZeroRpm, float? RgbBrightness);

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly bool _save;
    private readonly Action<string> _log;
    private readonly Dictionary<string, bool> _zeroRpm = new(StringComparer.OrdinalIgnoreCase);
    private float? _rgbBrightness;

    /// <param name="path">File to load and save; null keeps the settings in memory (tests).</param>
    /// <param name="save">False reads the file but never writes it (dry run).</param>
    public PersistedSettings(string? path, Action<string> log, bool save = true)
    {
        _path = path;
        _save = save;
        _log = log;
        if (path == null || !File.Exists(path)) return;

        try
        {
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), ConfigLoader.JsonOptions);
            foreach (var (fan, on) in data?.ZeroRpm ?? []) _zeroRpm[fan] = on;
            if (data?.RgbBrightness is float b && b >= 0f && b <= 100f) _rgbBrightness = b;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log($"Saved IPC settings unreadable ({path}), starting from config defaults: {ex.Message}");
        }
    }

    public IReadOnlyDictionary<string, bool> ZeroRpm
    {
        get { lock (_gate) return new Dictionary<string, bool>(_zeroRpm, StringComparer.OrdinalIgnoreCase); }
    }

    public float? RgbBrightness
    {
        get { lock (_gate) return _rgbBrightness; }
    }

    /// <summary>Remembers a Zero RPM toggle; null forgets it so the config value applies.</summary>
    public void SetZeroRpm(string fan, bool? enabled)
    {
        lock (_gate)
        {
            if (enabled is bool on) _zeroRpm[fan] = on;
            else _zeroRpm.Remove(fan);
            Save();
        }
    }

    public void SetRgbBrightness(float brightness)
    {
        lock (_gate)
        {
            _rgbBrightness = brightness;
            Save();
        }
    }

    /// <summary>Pushes the saved values into the engines. Call at startup and after every config reload.</summary>
    public void Apply(IFanControl fans, IRgbControl? rgb)
    {
        foreach (var (fan, on) in ZeroRpm)
        {
            // A fan renamed or stripped of its zeroRpm block keeps its entry, in case the config comes back.
            if (fans.SupportsZeroRpm(fan)) fans.SetZeroRpm(fan, on);
        }

        if (rgb != null && RgbBrightness is float b) rgb.Brightness = b;
    }

    private void Save()
    {
        if (_path == null || !_save) return;
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Data(_zeroRpm, _rgbBrightness), ConfigLoader.JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Saving IPC settings failed ({_path}): {ex.Message}");
        }
    }
}
