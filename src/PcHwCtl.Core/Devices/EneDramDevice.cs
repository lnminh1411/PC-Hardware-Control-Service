using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using PcHwCtl.Core.Rgb;

[assembly: InternalsVisibleTo("PcHwCtl.Core.Tests")]

namespace PcHwCtl.Core.Devices;

/// <summary>
/// RGB device controller for ENE DRAM controllers (AUDA0-E6K5-0101) over SMBus.
/// </summary>
public sealed class EneDramDevice : ILedDevice
{
    private readonly byte _address;
    private readonly ISmbus _bus;
    private readonly HashSet<byte> _allowedAddresses;
    private readonly Action<string> _log;

    private int _ledCount = 8;
    private List<LedZone> _zones;
    private RgbColor[] _colors;
    private bool[] _dirty;
    private bool _disposed;

    /// <summary>
    /// Event raised once when the device is disposed.
    /// </summary>
    public event Action? Disposed;

    /// <inheritdoc />
    public string Id => $"ene-0x{_address:x2}";

    /// <inheritdoc />
    public IReadOnlyList<LedZone> Zones => _zones;

    /// <summary>
    /// Gets the SMBus address for this device.
    /// </summary>
    public byte Address => _address;

    public EneDramDevice(
        byte address,
        ISmbus bus,
        IReadOnlyCollection<byte>? allowedAddresses = null,
        Action<string>? log = null)
    {
        _address = address;
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _allowedAddresses = allowedAddresses != null
            ? new HashSet<byte>(allowedAddresses)
            : [address, 0x77];
        _log = log ?? (_ => { });

        _colors = new RgbColor[_ledCount];
        _dirty = new bool[_ledCount];
        Array.Fill(_dirty, true);
        _zones = [new LedZone("DRAM", _ledCount)];
    }

    /// <summary>
    /// Validates that an SMBus address is in the allowed collection; throws if not.
    /// </summary>
    internal static void CheckAddress(byte address, IReadOnlyCollection<byte> allowed)
    {
        if (allowed == null || !allowed.Contains(address))
        {
            throw new InvalidOperationException(
                $"SMBus address 0x{address:X2} is not allowed. Allowed: {(allowed == null ? "none" : string.Join(", ", allowed.Select(a => $"0x{a:X2}")))}");
        }
    }

    private static ushort SwapBytes(ushort r) => (ushort)(((r & 0xFF) << 8) | ((r >> 8) & 0xFF));

    private static bool Quick(ISmbus bus, byte address, IReadOnlyCollection<byte> allowed)
    {
        CheckAddress(address, allowed);
        return bus.Quick(address);
    }

    private static int ReadByteData(ISmbus bus, byte address, byte command, IReadOnlyCollection<byte> allowed)
    {
        CheckAddress(address, allowed);
        return bus.ReadByteData(address, command);
    }

    private static bool WriteByteData(ISmbus bus, byte address, byte command, byte value, IReadOnlyCollection<byte> allowed)
    {
        CheckAddress(address, allowed);
        return bus.WriteByteData(address, command, value);
    }

    private static bool WriteWordData(ISmbus bus, byte address, byte command, ushort value, IReadOnlyCollection<byte> allowed)
    {
        CheckAddress(address, allowed);
        return bus.WriteWordData(address, command, value);
    }

    private static bool WriteBlockData(ISmbus bus, byte address, byte command, ReadOnlySpan<byte> data, IReadOnlyCollection<byte> allowed)
    {
        CheckAddress(address, allowed);
        return bus.WriteBlockData(address, command, data);
    }

    private static void SelectRegister(ISmbus bus, byte address, ushort reg, IReadOnlyCollection<byte> allowed)
    {
        ushort swapped = SwapBytes(reg);
        if (!WriteWordData(bus, address, 0x00, swapped, allowed))
        {
            throw new IOException($"Failed to select register 0x{reg:X4} on ENE device 0x{address:X2}.");
        }
    }

    private static int ReadRegisterByte(ISmbus bus, byte address, ushort reg, IReadOnlyCollection<byte> allowed)
    {
        SelectRegister(bus, address, reg, allowed);
        return ReadByteData(bus, address, 0x81, allowed);
    }

    private static void WriteRegisterByte(ISmbus bus, byte address, ushort reg, byte value, IReadOnlyCollection<byte> allowed)
    {
        if (value == 0xAA)
        {
            throw new InvalidOperationException("Writing value 0xAA to ENE registers is forbidden (saves to flash).");
        }
        SelectRegister(bus, address, reg, allowed);
        if (!WriteByteData(bus, address, 0x01, value, allowed))
        {
            throw new IOException($"Failed to write byte 0x{value:X2} to register 0x{reg:X4} on ENE device 0x{address:X2}.");
        }
    }

    private static void WriteRegisterBlock(ISmbus bus, byte address, ushort reg, ReadOnlySpan<byte> data, IReadOnlyCollection<byte> allowed)
    {
        if (data.Length is < 1 or > 3)
        {
            throw new ArgumentException("Block write length must be 1..3 bytes.", nameof(data));
        }
        SelectRegister(bus, address, reg, allowed);
        if (!WriteBlockData(bus, address, 0x03, data, allowed))
        {
            throw new IOException($"Failed to write block of {data.Length} bytes to register 0x{reg:X4} on ENE device 0x{address:X2}.");
        }
    }

    private static bool PassesIdentity(ISmbus bus, byte address, string expectedName, IReadOnlyCollection<byte> allowed)
    {
        // 1. Commands 0xA0..0xAF return 0x00..0x0F in order
        for (int i = 0; i < 16; i++)
        {
            int val = ReadByteData(bus, address, (byte)(0xA0 + i), allowed);
            if (val != i)
            {
                return false;
            }
        }

        // 2. Register 0x1030 does not read "Micron"
        Span<byte> nameBuf = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            int b = ReadRegisterByte(bus, address, (ushort)(0x1030 + i), allowed);
            if (b < 0) return false;
            nameBuf[i] = (byte)b;
        }
        string str1030 = Encoding.ASCII.GetString(nameBuf).TrimEnd('\0');
        if (str1030.Contains("Micron", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 3. Register 0x1000 reads expected name
        for (int i = 0; i < 16; i++)
        {
            int b = ReadRegisterByte(bus, address, (ushort)(0x1000 + i), allowed);
            if (b < 0) return false;
            nameBuf[i] = (byte)b;
        }
        string str1000 = Encoding.ASCII.GetString(nameBuf).TrimEnd('\0').Trim();
        return string.Equals(str1000, expectedName, StringComparison.Ordinal);
    }

    private static byte ParseAddress(string addr)
    {
        string trimmed = addr.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return byte.Parse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return byte.Parse(trimmed, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Discovers ENE DRAM controllers matching the configuration, performing remap if enabled.
    /// </summary>
    public static IReadOnlyList<EneDramDevice> Discover(EneConfig cfg, ISmbus bus, Action<string> log)
    {
        if (cfg == null) throw new ArgumentNullException(nameof(cfg));
        if (bus == null) throw new ArgumentNullException(nameof(bus));
        log ??= (_ => { });

        var targets = new List<byte>();
        foreach (string addrStr in cfg.Addresses)
        {
            targets.Add(ParseAddress(addrStr));
        }

        var allowed = new HashSet<byte>(targets) { 0x77 };
        var readyTargets = new HashSet<byte>();

        // Step 1: Check which targets are already ready
        foreach (byte target in targets)
        {
            if (PassesIdentity(bus, target, cfg.ExpectedName, allowed))
            {
                readyTargets.Add(target);
            }
        }

        // Step 2: Remap if enabled. Writing slot index s + new address to 0x77 moves only the stick
        // in DIMM slot s; empty slots move nothing, so walk every slot and keep the same target
        // until a stick actually lands on it.
        if (cfg.Remap)
        {
            var pending = targets.Where(t => !readyTargets.Contains(t)).ToList();
            int next = 0;
            for (int slot = 0; slot < 8 && next < pending.Count; slot++)
            {
                byte target = pending[next];

                if (!PassesIdentity(bus, 0x77, cfg.ExpectedName, allowed))
                {
                    if (slot == 0) log("ENE DRAM remap: nothing at 0x77, no remap needed.");
                    break;
                }

                if (Quick(bus, target, allowed))
                {
                    log($"ENE DRAM remap: target 0x{target:X2} answered quick write (busy), stopping remap.");
                    break;
                }

                try
                {
                    WriteRegisterByte(bus, 0x77, 0x80F8, (byte)slot, allowed);
                    WriteRegisterByte(bus, 0x77, 0x80F9, (byte)(target << 1), allowed);
                }
                catch (Exception ex)
                {
                    log($"ENE DRAM remap: failed writing to 0x77: {ex.Message}");
                    break;
                }

                Thread.Sleep(20);

                if (PassesIdentity(bus, target, cfg.ExpectedName, allowed))
                {
                    log($"ENE DRAM remap: slot {slot} moved to 0x{target:X2}.");
                    readyTargets.Add(target);
                    next++;
                }
            }
        }

        var devices = new List<EneDramDevice>();
        foreach (byte target in targets)
        {
            if (readyTargets.Contains(target))
            {
                devices.Add(new EneDramDevice(target, bus, allowed, log));
            }
        }

        return devices;
    }

    /// <inheritdoc />
    public bool Initialize()
    {
        try
        {
            int rawCount = ReadRegisterByte(_bus, _address, 0x1C02, _allowedAddresses);
            if (rawCount < 0)
            {
                _log($"ENE DRAM 0x{_address:X2}: failed to read LED count from config table.");
                return false;
            }

            _ledCount = Math.Clamp(rawCount, 1, 8);
            _zones = [new LedZone("DRAM", _ledCount)];
            _colors = new RgbColor[_ledCount];
            _dirty = new bool[_ledCount];
            Array.Fill(_dirty, true);

            WriteRegisterByte(_bus, _address, 0x8020, 1, _allowedAddresses);
            WriteRegisterByte(_bus, _address, 0x80A0, 1, _allowedAddresses);
            return true;
        }
        catch (Exception ex)
        {
            _log($"ENE DRAM 0x{_address:X2} initialization failed: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public void SetZone(string zone, ReadOnlySpan<RgbColor> colors)
    {
        if (!string.Equals(zone, "DRAM", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (int i = 0; i < _ledCount; i++)
        {
            RgbColor c = i < colors.Length ? colors[i] : RgbColor.Black;
            if (_colors[i] != c)
            {
                _colors[i] = c;
                _dirty[i] = true;
            }
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        Span<byte> rgb = stackalloc byte[3];
        for (int i = 0; i < _ledCount; i++)
        {
            if (_dirty[i])
            {
                rgb[0] = _colors[i].R;
                rgb[1] = _colors[i].B;
                rgb[2] = _colors[i].G;
                ushort reg = (ushort)(0x8100 + 3 * i);
                WriteRegisterBlock(_bus, _address, reg, rgb, _allowedAddresses);
                _dirty[i] = false;
            }
        }
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        Array.Fill(_dirty, true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disposed?.Invoke();
    }
}
