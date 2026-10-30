using System.IO;
using PcHwCtl.Core.Rgb;

namespace PcHwCtl.Core.Devices;

/// <summary>
/// RGB device controller for the Gigabyte IT5711 USB HID chip.
/// </summary>
public sealed class It5711Device : ILedDevice
{
    private sealed class ArgbZoneState
    {
        public required string Name { get; init; }
        public required int Header { get; init; }
        public required int Leds { get; init; }
        public required RgbColor[] Colors { get; init; }
        public bool Dirty { get; set; } = true;
    }

    private sealed class SingleZoneState
    {
        public required string Name { get; init; }
        public required int LedIndex { get; init; }
        public RgbColor Color { get; set; } = RgbColor.Black;
        public bool Dirty { get; set; } = true;
    }

    private readonly It5711Config _cfg;
    private readonly Func<IHidFeatureDevice?> _open;
    private readonly Action<string> _log;
    private readonly List<LedZone> _zones = [];
    private readonly Dictionary<string, ArgbZoneState> _argbZones = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SingleZoneState> _singleZones = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ByteOrder> _byteOrders = [];

    private IHidFeatureDevice? _device;

    public string Id => "it5711";

    public IReadOnlyList<LedZone> Zones => _zones;

    public It5711Device(It5711Config cfg, Func<IHidFeatureDevice?> open, Action<string> log)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        foreach (var (name, argbCfg) in _cfg.Argb)
        {
            _zones.Add(new LedZone(name, argbCfg.Leds));
            _argbZones[name] = new ArgbZoneState
            {
                Name = name,
                Header = argbCfg.Header,
                Leds = argbCfg.Leds,
                Colors = new RgbColor[argbCfg.Leds]
            };
        }

        foreach (var (name, ledIndex) in _cfg.Single)
        {
            _zones.Add(new LedZone(name, 1));
            _singleZones[name] = new SingleZoneState
            {
                Name = name,
                LedIndex = ledIndex
            };
        }
    }

    public bool Initialize()
    {
        _device?.Dispose();   // again after a resume or a fault: drop the old handle first
        _device = _open();
        if (_device == null)
        {
            _log("IT5711 HID device not found.");
            return false;
        }

        try
        {
            // 1. Request device info
            _device.SetFeature(It5711Protocol.Simple(0x60, 0, 0));
            byte[] infoData = _device.GetFeature(0xCC);
            if (infoData == null || infoData.Length < 64)
            {
                throw new IOException("IT5711 device info response was shorter than 64 bytes.");
            }
            var info = It5711Protocol.ParseInfo(infoData);

            // 2. Request extra calibration
            _device.SetFeature(It5711Protocol.Simple(0x61, 0, 0));
            byte[] extraData = _device.GetFeature(0xCC);
            if (extraData == null || extraData.Length < 64)
            {
                throw new IOException("IT5711 extra calibration response was shorter than 64 bytes.");
            }
            var extra = It5711Protocol.ParseExtraCalibration(extraData);

            ByteOrder fallbackOrder = ByteOrder.Parse(_cfg.FallbackByteOrder);
            _byteOrders[1] = ByteOrder.Decode(info.CalibrationDled1) ?? fallbackOrder;
            _byteOrders[2] = ByteOrder.Decode(info.CalibrationDled2) ?? fallbackOrder;
            _byteOrders[3] = ByteOrder.Decode(extra.CalibrationDled3) ?? fallbackOrder;
            _byteOrders[4] = ByteOrder.Decode(extra.CalibrationDled4) ?? fallbackOrder;

            // 3. Lamp array off if supported
            if (info.SupportCmdFlag >= 2)
            {
                _device.SetFeature(It5711Protocol.Simple(0x48, 0, 0));
            }

            // 4. Clear effect slots 0x20..0x27 and 0x90..0x92, then fast apply
            for (byte cmd = 0x20; cmd <= 0x27; cmd++)
            {
                _device.SetFeature(It5711Protocol.Simple(cmd, 0, 0));
            }
            for (byte cmd = 0x90; cmd <= 0x92; cmd++)
            {
                _device.SetFeature(It5711Protocol.Simple(cmd, 0, 0));
            }
            _device.SetFeature(It5711Protocol.Simple(0x28, 0xFF, 0x07));

            // 5. Audio beat mode off
            _device.SetFeature(It5711Protocol.Simple(0x31, 0, 0));

            // 6. LED count classes from config (unconfigured headers = class 0)
            int GetClass(int h)
            {
                var match = _cfg.Argb.Values.FirstOrDefault(x => x.Header == h);
                return match != null ? It5711Protocol.LedCountClass(match.Leds) : 0;
            }
            int d1 = GetClass(1);
            int d2 = GetClass(2);
            int d3 = GetClass(3);
            int d4 = GetClass(4);
            byte a = (byte)((d2 << 4) | (d1 & 0x0F));
            byte b = (byte)((d4 << 4) | (d3 & 0x0F));
            _device.SetFeature(It5711Protocol.Simple(0x34, a, b));

            // 7. Built-in effect disable mask for ARGB headers in direct mode
            byte mask = 0;
            foreach (var argb in _cfg.Argb.Values)
            {
                mask |= argb.Header switch
                {
                    1 => 0x01,
                    2 => 0x02,
                    3 => 0x08,
                    4 => 0x10,
                    _ => 0
                };
            }
            _device.SetFeature(It5711Protocol.Simple(0x32, mask, 0));
            Thread.Sleep(50);

            Invalidate();
            return true;
        }
        catch (Exception ex)
        {
            _log($"IT5711 initialization failed: {ex.Message}");
            _device?.Dispose();
            _device = null;
            return false;
        }
    }

    public void SetZone(string zone, ReadOnlySpan<RgbColor> colors)
    {
        if (_argbZones.TryGetValue(zone, out var argb))
        {
            for (int i = 0; i < argb.Leds; i++)
            {
                RgbColor c = i < colors.Length ? colors[i] : RgbColor.Black;
                if (argb.Colors[i] != c)
                {
                    argb.Colors[i] = c;
                    argb.Dirty = true;
                }
            }
        }
        else if (_singleZones.TryGetValue(zone, out var single))
        {
            RgbColor c = colors.Length > 0 ? colors[0] : RgbColor.Black;
            if (single.Color != c)
            {
                single.Color = c;
                single.Dirty = true;
            }
        }
    }

    public void Flush()
    {
        if (_device == null)
        {
            throw new InvalidOperationException("IT5711 device is not open.");
        }

        foreach (var argb in _argbZones.Values)
        {
            if (argb.Dirty)
            {
                ByteOrder order = _byteOrders.TryGetValue(argb.Header, out var bo)
                    ? bo
                    : ByteOrder.Parse(_cfg.FallbackByteOrder);
                var packets = It5711Protocol.RgbPackets(argb.Header, argb.Colors, order);
                foreach (var pkt in packets)
                {
                    _device.SetFeature(pkt);
                }
                argb.Dirty = false;
            }
        }

        uint singleMask = 0;
        foreach (var single in _singleZones.Values)
        {
            if (single.Dirty)
            {
                byte[] pkt = It5711Protocol.StaticEffect(single.LedIndex, single.Color);
                _device.SetFeature(pkt);
                singleMask |= (1u << single.LedIndex);
                single.Dirty = false;
            }
        }

        if (singleMask != 0)
        {
            _device.SetFeature(It5711Protocol.Apply(singleMask));
        }
    }

    public void Invalidate()
    {
        foreach (var argb in _argbZones.Values)
        {
            argb.Dirty = true;
        }
        foreach (var single in _singleZones.Values)
        {
            single.Dirty = true;
        }
    }

    public void Dispose()
    {
        _device?.Dispose();
        _device = null;
    }
}
