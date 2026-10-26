namespace PcHwCtl.Core.Rgb;

public sealed class RgbConfig
{
    public int Fps { get; set; } = 10;

    /// <summary>
    /// Per-device flush rate cap, keyed by device id or a prefix ending in '*' (e.g. "ene-*").
    /// Frames are still computed at Fps; a capped device gets the newest colors at its own rate.
    /// </summary>
    public Dictionary<string, int> DeviceFps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public float Brightness { get; set; } = 100;
    public Dictionary<string, RgbGroupConfig> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RgbGroupConfig
{
    /// <summary>"&lt;deviceId&gt;/&lt;zone&gt;" or "&lt;deviceId&gt;/*".</summary>
    public List<string> Zones { get; set; } = [];
    public RgbRuleConfig Rule { get; set; } = new();
}

public sealed class RgbRuleConfig
{
    /// <summary>off | static | gradient | breathing | rainbow</summary>
    public string Type { get; set; } = "static";

    /// <summary>"#RRGGBB" for static and breathing.</summary>
    public string? Color { get; set; }

    /// <summary>Gradient input: a sensor reference (same rules as curve sources) or "fan:&lt;name&gt;.rpm" / "fan:&lt;name&gt;.pct".</summary>
    public string? Source { get; set; }

    public List<GradientStop>? Stops { get; set; }

    /// <summary>Gradient: EMA time constant in seconds applied to the source value. 0 = none.</summary>
    public float SmoothingS { get; set; } = 2;

    /// <summary>Breathing and rainbow cycle length in seconds.</summary>
    public float PeriodS { get; set; } = 4;

    /// <summary>Gradient: color while the source is missing. Default: last stop.</summary>
    public string? Fallback { get; set; }
}

public sealed class GradientStop
{
    public float At { get; set; }
    public string Color { get; set; } = "#000000";
}

public sealed class DevicesConfig
{
    public It5711Config? It5711 { get; set; }
    public EneConfig? Ene { get; set; }
}

public sealed class It5711Config
{
    public bool Enabled { get; set; } = true;

    /// <summary>ARGB zones by name. Header 1..4 = D_LED1..D_LED4.</summary>
    public Dictionary<string, ArgbHeaderConfig> Argb { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Single-color zones by name, value = LED index (see docs/protocols/it5711.md).</summary>
    public Dictionary<string, int> Single { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Byte order used when the device reports no calibration, e.g. "GRB".</summary>
    public string FallbackByteOrder { get; set; } = "GRB";
}

public sealed class ArgbHeaderConfig
{
    public int Header { get; set; }
    public int Leds { get; set; }
}

public sealed class EneConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Fixed SMBus addresses, "0x71" style. Only these and 0x77 are ever written.</summary>
    public List<string> Addresses { get; set; } = ["0x71", "0x73"];

    public string ExpectedName { get; set; } = "AUDA0-E6K5-0101";

    /// <summary>Move sticks from 0x77 to Addresses after a cold boot.</summary>
    public bool Remap { get; set; } = true;

    public string PawnIoLibPath { get; set; } = @"C:\Program Files\PawnIO\PawnIOLib.dll";
}

public sealed class IpcConfig
{
    public bool Enabled { get; set; } = true;
    public string PipeName { get; set; } = "PcHwCtl";
    public int OverrideTtlS { get; set; } = 60;
    public int MaxTtlS { get; set; } = 3600;
    public string AllowAccount { get; set; } = @"NT AUTHORITY\LocalService";
}
