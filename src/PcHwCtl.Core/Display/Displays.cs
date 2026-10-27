namespace PcHwCtl.Core.Display;

/// <summary>Monitor control (docs/protocols/ddc-ci.md). Models are matched by the DDC name, e.g. "DELL E1715S".</summary>
public sealed class DisplaysConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>How often the monitor state is read over DDC/CI (each read is a slow I2C round trip).</summary>
    public int PollS { get; set; } = 5;

    public Dictionary<string, DisplayModelConfig> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DisplayModelConfig
{
    /// <summary>
    /// How "display power off/on" works on this model:
    /// "ddc-power" writes VCP 0xD6 = PowerOff / 1 (the monitor stays listed in Windows, e.g. DELL E1715S);
    /// "ddc-input" switches VCP 0x60 to an empty StandbyInput and back, so the monitor sleeps on "no signal" but
    /// keeps its link up (MSI PRO MP275 drops out of Windows on 0xD6 = 4);
    /// "none" = no per-monitor power, only "display sleep" (Windows turns every display off).
    /// </summary>
    public string Power { get; set; } = "none";

    public int PowerOff { get; set; } = 4;

    /// <summary>VCP 0x60 value of an input with nothing plugged in, e.g. "0x01" (VGA).</summary>
    public string? StandbyInput { get; set; }

    /// <summary>VCP 0x60 value to return to when the input before standby is unknown, e.g. "0x11" (HDMI 1).</summary>
    public string? ActiveInput { get; set; }
}

/// <param name="Id">1-based, numbered from the left like the ESP32 board does.</param>
/// <param name="Bright">Brightness %, -1 = no DDC answer.</param>
/// <param name="Power">1 on, 0 off/standby, -1 = no DDC answer.</param>
/// <param name="Input">VCP 0x60, -1 = no DDC answer.</param>
public sealed record DisplayState(int Id, string Name, bool Main, int Bright, int Power, int Input, string PowerMethod);

public sealed record DisplayResult(bool Ok, string? Error = null)
{
    public static readonly DisplayResult Success = new(true);
    public static DisplayResult Fail(string error) => new(false, error);
}

/// <summary>What IPC may do to monitors. In-process (DisplayController) or relayed to the user-session agent.</summary>
public interface IDisplayControl
{
    IReadOnlyList<DisplayState> Displays { get; }

    /// <summary>Null when working; else why not, e.g. "display agent not running".</summary>
    string? Status { get; }

    DisplayResult Brightness(string target, int pct);
    DisplayResult Power(string target, bool on);
    DisplayResult Input(string target, int code);

    /// <summary>Windows' own display off: every monitor at once, any input wakes them.</summary>
    DisplayResult Sleep();

    /// <summary>Wakes Windows' display off with a 1 px mouse nudge.</summary>
    DisplayResult Wake();
}

/// <summary>One physical monitor found by a scan. Valid until the scan is disposed.</summary>
public interface IDisplayHandle
{
    string Name { get; }
    int Left { get; }
    bool Main { get; }
    bool TryRead(byte code, out uint current, out uint maximum);
    bool TryWrite(byte code, uint value);
}

/// <summary>DDC/CI and the user-session Windows calls. Must run in the user's session: session 0 sees no monitors.</summary>
public interface IDisplayBus
{
    DisplayScan Scan();
    bool PostDisplayOff();
    void NudgeMouse();
}

public sealed class DisplayScan(IReadOnlyList<IDisplayHandle> monitors, Action? release = null) : IDisposable
{
    public IReadOnlyList<IDisplayHandle> Monitors { get; } = monitors;

    public void Dispose() => release?.Invoke();
}
