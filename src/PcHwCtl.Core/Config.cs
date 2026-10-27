using PcHwCtl.Core.Display;
using PcHwCtl.Core.Lasso;
using PcHwCtl.Core.Rgb;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcHwCtl.Core;

public sealed class Config
{
    public int TickMs { get; set; } = 1000;
    public int StaleSensorS { get; set; } = 5;
    public Dictionary<string, SensorConfig> Sensors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CurveConfig> Curves { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FanConfig> Fans { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public RgbConfig Rgb { get; set; } = new();
    public DevicesConfig Devices { get; set; } = new();
    public IpcConfig Ipc { get; set; } = new();
    public DisplaysConfig Displays { get; set; } = new();
    public LassoConfig Lasso { get; set; } = new();
}

public sealed class SensorConfig
{
    public string Type { get; set; } = "mix";
    public string Fn { get; set; } = "max";
    public List<string> Sources { get; set; } = [];
    public bool AllowMissing { get; set; }
}

public sealed class CurveConfig
{
    public string Type { get; set; } = "";
    public string? Source { get; set; }

    // Graph
    public List<List<float>>? Points { get; set; }
    public float HysteresisUp { get; set; }
    public float HysteresisDown { get; set; }
    public float ResponseUpS { get; set; }
    public float ResponseDownS { get; set; }
    public bool IgnoreHysteresisAtLimits { get; set; }

    // Auto
    public float IdleTemp { get; set; }
    public float LoadTemp { get; set; }
    public float MinPct { get; set; }
    public float MaxPct { get; set; }
    public float Step { get; set; } = 1;
    public float Deadband { get; set; }
    public float ResponseS { get; set; }

    // Flat
    public float Pct { get; set; }
}

public sealed class FanConfig
{
    public string Control { get; set; } = "";
    public string? Tach { get; set; }
    public string Curve { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public float StepUp { get; set; } = 8;
    public float StepDown { get; set; } = 8;
    public float StartPct { get; set; } = 0;
    public float MinPct { get; set; } = 0;
    public float FloorPct { get; set; } = 0;
    public float Offset { get; set; } = 0;

    /// <summary>
    /// Null = software curve written as a % each tick. "amd-od8" = the 5-point graph curve and Zero RPM go into the
    /// GPU firmware (docs/protocols/amd-od8.md); the firmware then stops the fan below its own 52 °C, so
    /// zeroRpm stopBelowC, startAtC and park do not apply.
    /// </summary>
    public string? Driver { get; set; }

    /// <summary>Optional: stop the fan below a temperature (GPU style). Null = fan never parks.</summary>
    public ZeroRpmConfig? ZeroRpm { get; set; }
}

public sealed class ZeroRpmConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Park the fan when the curve temperature drops below this.</summary>
    public float StopBelowC { get; set; } = 50;

    /// <summary>Spin up again once the curve temperature reaches this. Must be above StopBelowC.</summary>
    public float StartAtC { get; set; } = 60;

    /// <summary>"zero" writes 0 %; "release" hands the fan back to the driver, whose own auto mode stops it at idle
    /// (needed where manual 0 % is clamped, e.g. NVIDIA).</summary>
    public string Park { get; set; } = "zero";
}

public sealed class ConfigException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public ConfigException(string message) : base(message)
    {
        Errors = [message];
    }

    public ConfigException(string message, IReadOnlyList<string> errors) : base(message)
    {
        Errors = errors;
    }
}

public static class ConfigLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static Config Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found: {path}", path);
        }

        string json = File.ReadAllText(path);
        return LoadText(json);
    }

    public static Config LoadText(string json)
    {
        Config? config;
        try
        {
            config = JsonSerializer.Deserialize<Config>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Invalid JSON in config: {ex.Message}");
        }

        if (config == null)
        {
            throw new ConfigException("Config content deserialized to null.");
        }

        Validate(config);
        return config;
    }

    public static void Validate(Config config)
    {
        var errors = new List<string>();

        // 1. Validate sensors
        foreach (var (name, sensor) in config.Sensors)
        {
            if (!string.Equals(sensor.Type, "mix", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Sensor '{name}' has unknown type '{sensor.Type}'.");
            }

            if (!string.Equals(sensor.Fn, "max", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sensor.Fn, "min", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sensor.Fn, "avg", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Sensor '{name}' has unknown function '{sensor.Fn}'.");
            }

            foreach (var src in sensor.Sources)
            {
                if (!src.StartsWith('/') && !src.StartsWith("sel:", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Sensor '{name}' source '{src}' cannot reference virtual sensors.");
                }
            }
        }

        // 2. Validate curves
        foreach (var (name, curve) in config.Curves)
        {
            bool isGraph = string.Equals(curve.Type, "graph", StringComparison.OrdinalIgnoreCase);
            bool isAuto = string.Equals(curve.Type, "auto", StringComparison.OrdinalIgnoreCase);
            bool isFlat = string.Equals(curve.Type, "flat", StringComparison.OrdinalIgnoreCase);

            if (!isGraph && !isAuto && !isFlat)
            {
                errors.Add($"Curve '{name}' has unknown type '{curve.Type}'.");
                continue;
            }

            if (isGraph || isAuto)
            {
                if (string.IsNullOrWhiteSpace(curve.Source))
                {
                    errors.Add($"Curve '{name}' source is missing.");
                }
                else if (!curve.Source.StartsWith('/') && !curve.Source.StartsWith("sel:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!config.Sensors.ContainsKey(curve.Source))
                    {
                        errors.Add($"Curve '{name}' references unknown sensor key '{curve.Source}'.");
                    }
                }
            }

            if (isGraph)
            {
                if (curve.Points == null || curve.Points.Count == 0)
                {
                    errors.Add($"Curve '{name}' has empty points.");
                }
                else
                {
                    bool sorted = true;
                    for (int i = 0; i < curve.Points.Count; i++)
                    {
                        var pt = curve.Points[i];
                        if (pt.Count != 2)
                        {
                            errors.Add($"Curve '{name}' point {i} does not have 2 values.");
                            continue;
                        }

                        float temp = pt[0];
                        float pct = pt[1];

                        if (pct < 0f || pct > 100f)
                        {
                            errors.Add($"Curve '{name}' point {i} pct {pct} outside 0-100.");
                        }

                        if (i > 0 && temp < curve.Points[i - 1][0])
                        {
                            sorted = false;
                        }
                    }

                    if (!sorted)
                    {
                        errors.Add($"Curve '{name}' points not sorted by temp.");
                    }
                }
            }
            else if (isAuto)
            {
                if (curve.MinPct < 0f || curve.MinPct > 100f)
                {
                    errors.Add($"Curve '{name}' minPct {curve.MinPct} outside 0-100.");
                }
                if (curve.MaxPct < 0f || curve.MaxPct > 100f)
                {
                    errors.Add($"Curve '{name}' maxPct {curve.MaxPct} outside 0-100.");
                }
            }
            else if (isFlat)
            {
                if (curve.Pct < 0f || curve.Pct > 100f)
                {
                    errors.Add($"Curve '{name}' pct {curve.Pct} outside 0-100.");
                }
            }
        }

        // 3. Validate fans
        var controlGroups = config.Fans
            .GroupBy(f => f.Value.Control, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1);

        foreach (var group in controlGroups)
        {
            errors.Add($"Duplicate control id '{group.Key}'.");
        }

        foreach (var (name, fan) in config.Fans)
        {
            if (string.IsNullOrWhiteSpace(fan.Curve))
            {
                errors.Add($"Fan '{name}' curve is missing.");
            }
            else if (!config.Curves.ContainsKey(fan.Curve))
            {
                errors.Add($"Fan '{name}' references unknown curve key '{fan.Curve}'.");
            }

            if (fan.StartPct < 0f || fan.StartPct > 100f)
            {
                errors.Add($"Fan '{name}' startPct {fan.StartPct} outside 0-100.");
            }
            if (fan.MinPct < 0f || fan.MinPct > 100f)
            {
                errors.Add($"Fan '{name}' minPct {fan.MinPct} outside 0-100.");
            }
            if (fan.FloorPct < 0f || fan.FloorPct > 100f)
            {
                errors.Add($"Fan '{name}' floorPct {fan.FloorPct} outside 0-100.");
            }

            if (fan.Driver != null)
            {
                if (!string.Equals(fan.Driver, "amd-od8", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Fan '{name}' driver '{fan.Driver}' unknown; only 'amd-od8' exists.");
                }
                else if (!config.Curves.TryGetValue(fan.Curve, out var nativeCurve) ||
                    !string.Equals(nativeCurve.Type, "graph", StringComparison.OrdinalIgnoreCase) ||
                    nativeCurve.Points is not { Count: 5 } pts ||
                    pts.Any(pt => pt.Count != 2 || pt[0] < 25f || pt[0] > 100f || pt[1] < 30f || pt[1] > 100f) ||
                    pts.Zip(pts.Skip(1)).Any(pair => pair.Second[0] <= pair.First[0]))
                {
                    errors.Add($"Fan '{name}' driver amd-od8 needs a graph curve of exactly 5 points, temperatures 25-100 rising, speeds 30-100.");
                }
            }

            if (fan.ZeroRpm is { } zr)
            {
                if (zr.StartAtC <= zr.StopBelowC)
                {
                    errors.Add($"Fan '{name}' zeroRpm startAtC {zr.StartAtC} must be above stopBelowC {zr.StopBelowC}.");
                }
                if (!string.Equals(zr.Park, "zero", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(zr.Park, "release", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Fan '{name}' zeroRpm park '{zr.Park}' must be 'zero' or 'release'.");
                }
                if (fan.FloorPct > 0f)
                {
                    errors.Add($"Fan '{name}' has floorPct {fan.FloorPct}; zeroRpm would park below the floor.");
                }
            }
        }

        // 4. RGB groups and rules
        errors.AddRange(RgbConfigValidator.Validate(config.Rgb, config));

        // 5. Process Lasso
        if (config.Lasso.Enabled && string.IsNullOrWhiteSpace(config.Lasso.Ini))
        {
            errors.Add("Lasso ini path is empty.");
        }

        // 6. Displays
        if (config.Displays.PollS < 1)
        {
            errors.Add($"Displays pollS {config.Displays.PollS} must be at least 1.");
        }
        foreach (var (model, d) in config.Displays.Models)
        {
            bool known = d.Power.ToLowerInvariant() is "none" or "ddc-power" or "ddc-input";
            if (!known)
            {
                errors.Add($"Display '{model}' power '{d.Power}' must be none, ddc-power or ddc-input.");
            }
            if (string.Equals(d.Power, "ddc-power", StringComparison.OrdinalIgnoreCase) && (d.PowerOff < 2 || d.PowerOff > 5))
            {
                errors.Add($"Display '{model}' powerOff {d.PowerOff} must be a VCP 0xD6 off value, 2-5.");
            }
            if (string.Equals(d.Power, "ddc-input", StringComparison.OrdinalIgnoreCase) && !DisplayController.TryParseCode(d.StandbyInput, out _))
            {
                errors.Add($"Display '{model}' power ddc-input needs standbyInput, e.g. \"0x01\".");
            }
            if (d.ActiveInput != null && !DisplayController.TryParseCode(d.ActiveInput, out _))
            {
                errors.Add($"Display '{model}' activeInput '{d.ActiveInput}' is not a number.");
            }
        }

        if (errors.Count > 0)
        {
            throw new ConfigException(string.Join(Environment.NewLine, errors), errors);
        }
    }
}
