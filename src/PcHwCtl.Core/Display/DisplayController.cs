using System.Globalization;

namespace PcHwCtl.Core.Display;

/// <summary>
/// Monitor control over DDC/CI, carried over from ESP32-Helper's Monitors.cs: one DDC conversation at a time,
/// one retry on a lost reply, monitors numbered from the left, and values just set are reported for 15 s
/// because monitors answer reads with the old value for a while.
/// </summary>
public sealed class DisplayController : IDisplayControl
{
    public const byte VcpBrightness = 0x10;
    public const byte VcpInput = 0x60;
    public const byte VcpPower = 0xD6;

    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(15);

    private readonly IDisplayBus _bus;
    private readonly Func<DisplaysConfig> _config;
    private readonly IClock _clock;
    private readonly Action<int> _sleep;

    private readonly object _ddcGate = new();
    private readonly object _stateGate = new();
    private IReadOnlyList<DisplayState> _displays = [];

    // (monitor index, VCP code) -> value just written and until when to report it
    private readonly Dictionary<(int, byte), (int Value, DateTime Until)> _held = new();

    // Input each "ddc-input" monitor was on before standby, by name and index
    private readonly Dictionary<(int, string), uint> _inputBeforeStandby = new();

    // Last value each monitor answered, by index, name and VCP code. A monitor sleeping on "no signal" stops
    // answering DDC altogether, so its state is what it last said (plus anything set since).
    private readonly Dictionary<(int, string, byte), int> _lastKnown = new();

    public DisplayController(IDisplayBus bus, Func<DisplaysConfig> config, IClock clock, Action<int>? sleep = null)
    {
        _bus = bus;
        _config = config;
        _clock = clock;
        _sleep = sleep ?? Thread.Sleep;
    }

    public IReadOnlyList<DisplayState> Displays
    {
        get { lock (_stateGate) return _displays; }
    }

    public string? Status => null;

    /// <summary>Reads every monitor's brightness, power and input. Call every PollS and after each change.</summary>
    public void Refresh()
    {
        lock (_ddcGate)
        {
            using var scan = _bus.Scan();
            var ordered = Ordered(scan);
            var states = new List<DisplayState>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i];
                var model = ModelFor(m.Name);
                int bright = Known(i, m.Name, VcpBrightness, Read(m, VcpBrightness, out uint cur, out uint max) && max > 0 ? (int)Math.Round(cur * 100.0 / max) : -1);
                int input = Known(i, m.Name, VcpInput, Read(m, VcpInput, out cur, out _) ? (int)cur : -1);
                int power = Known(i, m.Name, VcpPower, Read(m, VcpPower, out cur, out _) ? (cur == 1 ? 1 : 0) : -1);

                // The input trick leaves 0xD6 at "on": the power state is which input is selected.
                if (IsMethod(model, "ddc-input") && input >= 0 && TryParseCode(model!.StandbyInput, out uint standby))
                {
                    power = input == standby ? 0 : 1;
                }

                states.Add(new DisplayState(
                    i + 1, m.Name, m.Main,
                    Held(i, VcpBrightness, bright),
                    // A monitor asleep on purpose stops answering DDC: the value just set still stands.
                    Held(i, VcpPower, power),
                    Held(i, VcpInput, input),
                    model?.Power ?? "none"));
            }

            lock (_stateGate) _displays = states;
        }
    }

    public DisplayResult Brightness(string target, int pct)
    {
        if (pct < 0 || pct > 100) return DisplayResult.Fail("brightness is 0-100");
        return ForTargets(target, includeAll: _ => true, (i, m, model) =>
        {
            uint max = Read(m, VcpBrightness, out _, out uint mx) && mx > 0 ? mx : 100;
            if (!Write(m, VcpBrightness, (uint)Math.Round(pct * max / 100.0))) return false;
            Hold(i, VcpBrightness, pct);
            return true;
        });
    }

    public DisplayResult Power(string target, bool on)
    {
        return ForTargets(target, includeAll: model => !IsMethod(model, "none"), (i, m, model) =>
        {
            if (IsMethod(model, "ddc-power"))
            {
                if (!Write(m, VcpPower, on ? 1u : (uint)model!.PowerOff)) return false;
                Hold(i, VcpPower, on ? 1 : 0);
                return true;
            }

            if (IsMethod(model, "ddc-input"))
            {
                if (!TryParseCode(model!.StandbyInput, out uint standby))
                {
                    throw new InvalidOperationException($"{m.Name} has no valid standbyInput");
                }

                var key = (i, m.Name);
                uint next;
                if (on)
                {
                    if (!_inputBeforeStandby.Remove(key, out next) && !TryParseCode(model.ActiveInput, out next))
                    {
                        throw new InvalidOperationException($"{m.Name}: input before standby unknown and no activeInput");
                    }
                }
                else
                {
                    if (Read(m, VcpInput, out uint current, out _) && current != standby) _inputBeforeStandby[key] = current;
                    next = standby;
                }

                if (!Write(m, VcpInput, next)) return false;
                Hold(i, VcpInput, (int)next);
                Hold(i, VcpPower, on ? 1 : 0);
                return true;
            }

            throw new InvalidOperationException($"{m.Name} has no DDC power method; use display sleep");
        });
    }

    public DisplayResult Input(string target, int code)
    {
        if (code < 1 || code > 0xFF) return DisplayResult.Fail("input code is 0x01-0xFF");
        return ForTargets(target, includeAll: _ => true, (i, m, model) =>
        {
            if (!Write(m, VcpInput, (uint)code)) return false;
            Hold(i, VcpInput, code);
            return true;
        });
    }

    public DisplayResult Sleep() =>
        _bus.PostDisplayOff() ? DisplayResult.Success : DisplayResult.Fail("display off not sent");

    public DisplayResult Wake()
    {
        _bus.NudgeMouse();
        return DisplayResult.Success;
    }

    /// <summary>Runs action on "all" (monitors includeAll accepts) or monitor n. Action returns false = no DDC answer.</summary>
    private DisplayResult ForTargets(string target, Func<DisplayModelConfig?, bool> includeAll, Func<int, IDisplayHandle, DisplayModelConfig?, bool> action)
    {
        DisplayResult result;
        lock (_ddcGate)
        {
            using var scan = _bus.Scan();
            var ordered = Ordered(scan);
            if (ordered.Count == 0) return DisplayResult.Fail("no monitors");

            List<int> indexes;
            if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                indexes = Enumerable.Range(0, ordered.Count).Where(i => includeAll(ModelFor(ordered[i].Name))).ToList();
                if (indexes.Count == 0) return DisplayResult.Fail("no monitor supports this; use display sleep");
            }
            else if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= ordered.Count)
            {
                indexes = [n - 1];
            }
            else
            {
                return DisplayResult.Fail($"no monitor {target} ({ordered.Count} found)");
            }

            var failed = new List<string>();
            foreach (int i in indexes)
            {
                try
                {
                    if (!action(i, ordered[i], ModelFor(ordered[i].Name))) failed.Add($"no DDC answer from monitor {i + 1}");
                }
                catch (InvalidOperationException ex)
                {
                    failed.Add($"monitor {i + 1}: {ex.Message}");
                }
            }

            result = failed.Count == 0 ? DisplayResult.Success : DisplayResult.Fail(string.Join("; ", failed));
        }

        Refresh();
        return result;
    }

    private static List<IDisplayHandle> Ordered(DisplayScan scan) => scan.Monitors.OrderBy(m => m.Left).ToList();

    private DisplayModelConfig? ModelFor(string name) =>
        _config().Models.TryGetValue(name, out var model) ? model : null;

    private static bool IsMethod(DisplayModelConfig? model, string method) =>
        string.Equals(model?.Power ?? "none", method, StringComparison.OrdinalIgnoreCase);

    public static bool TryParseCode(string? text, out uint code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
            : uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
    }

    // DDC/CI replies get lost now and then: one retry
    private bool Read(IDisplayHandle m, byte code, out uint current, out uint maximum)
    {
        if (m.TryRead(code, out current, out maximum)) return true;
        _sleep(50);
        return m.TryRead(code, out current, out maximum);
    }

    private bool Write(IDisplayHandle m, byte code, uint value)
    {
        if (m.TryWrite(code, value)) return true;
        _sleep(50);
        return m.TryWrite(code, value);
    }

    private int Known(int index, string name, byte code, int read)
    {
        if (read >= 0)
        {
            _lastKnown[(index, name, code)] = read;
            return read;
        }
        return _lastKnown.TryGetValue((index, name, code), out int last) ? last : -1;
    }

    private void Hold(int index, byte code, int value) => _held[(index, code)] = (value, _clock.UtcNow + HoldFor);

    private int Held(int index, byte code, int read)
    {
        if (!_held.TryGetValue((index, code), out var held)) return read;
        if (_clock.UtcNow < held.Until && read != held.Value) return held.Value;
        _held.Remove((index, code)); // expired, or the monitor caught up
        return read;
    }
}
