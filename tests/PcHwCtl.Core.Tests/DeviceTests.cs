using System.Buffers.Binary;
using System.IO;
using System.Text;
using PcHwCtl.Core.Devices;
using PcHwCtl.Core.Rgb;
using Xunit;

namespace PcHwCtl.Core.Tests;

/// <summary>
/// Fake HID feature device recording sent reports and answering configured GetFeature responses.
/// </summary>
public sealed class SliceBFakeHidDevice : IHidFeatureDevice
{
    public List<byte[]> SentReports { get; } = [];
    public Dictionary<byte, byte[]> FeatureResponses { get; } = new();
    public bool Disposed { get; private set; }

    public void SetFeature(byte[] report64)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentNullException.ThrowIfNull(report64);
        SentReports.Add((byte[])report64.Clone());
    }

    public byte[] GetFeature(byte reportId)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (FeatureResponses.TryGetValue(reportId, out var response))
        {
            return (byte[])response.Clone();
        }

        byte[] defaultReport = new byte[64];
        defaultReport[0] = reportId;
        return defaultReport;
    }

    public void Dispose()
    {
        Disposed = true;
    }
}

/// <summary>
/// Simulated ENE DRAM chip in <see cref="SliceBFakeSmbus"/>.
/// </summary>
public sealed class SliceBFakeEneChip
{
    public ushort RegisterPointer { get; set; }
    public byte[] Memory { get; } = new byte[65536];
    public string Name { get; private set; } = "AUDA0-E6K5-0101";
    public string TypeAt1030 { get; private set; } = "ENE";
    public byte LedCount { get; set; } = 8;

    public SliceBFakeEneChip()
    {
        Memory[0x1C02] = LedCount;
        SetName(Name);
        SetTypeAt1030(TypeAt1030);
    }

    /// <summary>DIMM slot of this stick; -1 = answers any slot index.</summary>
    public int Slot { get; set; } = -1;

    public void SetName(string name)
    {
        Name = name;
        Array.Clear(Memory, 0x1000, 16);
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(bytes, 0, Memory, 0x1000, Math.Min(bytes.Length, 16));
    }

    public void SetTypeAt1030(string type)
    {
        TypeAt1030 = type;
        Array.Clear(Memory, 0x1030, 16);
        byte[] bytes = Encoding.ASCII.GetBytes(type);
        Array.Copy(bytes, 0, Memory, 0x1030, Math.Min(bytes.Length, 16));
    }
}

/// <summary>
/// Fake SMBus implementation simulating ENE controllers and tracking touched addresses and writes.
/// </summary>
public sealed class SliceBFakeSmbus : ISmbus
{
    public Dictionary<byte, SliceBFakeEneChip> Chips { get; } = new();
    public Queue<SliceBFakeEneChip> PendingChipsAt77 { get; } = new();
    public int SelectedSlot { get; private set; } = -1;
    public List<byte> TouchedAddresses { get; } = [];
    public List<(byte Address, ushort Register, byte Value)> Writes { get; } = [];
    public List<(byte Address, ushort Register, byte[] Data)> BlockWrites { get; } = [];
    public bool Disposed { get; private set; }

    public bool Quick(byte address)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        TouchedAddresses.Add(address);
        return Chips.ContainsKey(address);
    }

    public int ReadByteData(byte address, byte command)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        TouchedAddresses.Add(address);

        if (!Chips.TryGetValue(address, out var chip))
        {
            return -1;
        }

        // Direct commands 0xA0..0xAF return 0x00..0x0F
        if (command >= 0xA0 && command <= 0xAF)
        {
            return command - 0xA0;
        }

        // Command 0x81: read byte at RegisterPointer
        if (command == 0x81)
        {
            return chip.Memory[chip.RegisterPointer];
        }

        return -1;
    }

    public bool WriteByteData(byte address, byte command, byte value)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        TouchedAddresses.Add(address);

        if (!Chips.TryGetValue(address, out var chip))
        {
            return false;
        }

        if (command == 0x01)
        {
            if (value == 0xAA)
            {
                throw new InvalidOperationException("Flash save value 0xAA must never be written.");
            }

            chip.Memory[chip.RegisterPointer] = value;
            Writes.Add((address, chip.RegisterPointer, value));

            if (address == 0x77 && chip.RegisterPointer == 0x80F8)
            {
                SelectedSlot = value;
            }

            // Address remap: writing 0x80F9 at 0x77 moves the stick in the selected slot to (value >> 1);
            // an empty slot moves nothing.
            if (address == 0x77 && chip.RegisterPointer == 0x80F9)
            {
                var at77 = new List<SliceBFakeEneChip> { chip };
                at77.AddRange(PendingChipsAt77);
                var mover = at77.FirstOrDefault(c => c.Slot == -1 || c.Slot == SelectedSlot);
                if (mover != null)
                {
                    at77.Remove(mover);
                    Chips[(byte)(value >> 1)] = mover;
                    Chips.Remove(0x77);
                    PendingChipsAt77.Clear();
                    if (at77.Count > 0)
                    {
                        Chips[0x77] = at77[0];
                        foreach (var rest in at77.Skip(1)) PendingChipsAt77.Enqueue(rest);
                    }
                }
            }

            return true;
        }

        return false;
    }

    public bool WriteWordData(byte address, byte command, ushort value)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        TouchedAddresses.Add(address);

        if (!Chips.TryGetValue(address, out var chip))
        {
            return false;
        }

        if (command == 0x00)
        {
            // Register select: bytes swapped
            ushort reg = (ushort)(((value << 8) | (value >> 8)) & 0xFFFF);
            chip.RegisterPointer = reg;
            return true;
        }

        return false;
    }

    public bool WriteBlockData(byte address, byte command, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        TouchedAddresses.Add(address);

        if (!Chips.TryGetValue(address, out var chip))
        {
            return false;
        }

        if (command == 0x03)
        {
            byte[] copy = data.ToArray();
            BlockWrites.Add((address, chip.RegisterPointer, copy));

            for (int i = 0; i < copy.Length; i++)
            {
                chip.Memory[(ushort)(chip.RegisterPointer + i)] = copy[i];
            }
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        Disposed = true;
    }
}

public class DeviceTests
{
    [Fact]
    public void Test01_RgbPackets_24Leds_GrbOrder()
    {
        var colors = new RgbColor[24];
        colors[0] = new RgbColor(0xFF, 0x00, 0x00); // pure red
        var order = ByteOrder.Parse("GRB");
        var packets = It5711Protocol.RgbPackets(1, colors, order);

        Assert.Equal(2, packets.Count);

        // Header 1 uses command 0x58
        Assert.Equal(0x58, packets[0][1]);
        Assert.Equal(0x58, packets[1][1]);

        // Packet 0: byte offset 0, byte count 57
        ushort offset0 = BinaryPrimitives.ReadUInt16LittleEndian(packets[0].AsSpan(2, 2));
        Assert.Equal(0, offset0);
        Assert.Equal(57, packets[0][4]);

        // Packet 1: byte offset 57, byte count 15
        ushort offset1 = BinaryPrimitives.ReadUInt16LittleEndian(packets[1].AsSpan(2, 2));
        Assert.Equal(57, offset1);
        Assert.Equal(15, packets[1][4]);

        // Pure red LED 0 puts 0xFF at index 5 + 1 (R position in GRB)
        Assert.Equal(0xFF, packets[0][5 + 1]);
        Assert.Equal(0x00, packets[0][5 + 0]); // G position
        Assert.Equal(0x00, packets[0][5 + 2]); // B position
    }

    [Fact]
    public void Test02_StaticEffect_Led4_Color()
    {
        var color = RgbColor.Parse("#102030");
        byte[] packet = It5711Protocol.StaticEffect(4, color);

        Assert.Equal(0xCC, packet[0]);
        Assert.Equal(0x24, packet[1]); // header 0x24

        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        Assert.Equal(0x10u, mask); // mask 0x10 at [2..5]

        Assert.Equal(1, packet[11]);   // type 1
        Assert.Equal(255, packet[12]); // brightness 255
        Assert.Equal(0x30, packet[14]); // color bytes [14]=0x30 (B)
        Assert.Equal(0x20, packet[15]); // [15]=0x20 (G)
        Assert.Equal(0x10, packet[16]); // [16]=0x10 (R)
    }

    [Fact]
    public void Test03_ByteOrder_DecodeAndParse()
    {
        var decoded = ByteOrder.Decode(0x00010002);
        var parsed = ByteOrder.Parse("GRB");

        Assert.NotNull(decoded);
        Assert.Equal(parsed, decoded.Value);
        Assert.Null(ByteOrder.Decode(0));
    }

    [Fact]
    public void Test04_It5711Device_Initialize_Sequence()
    {
        var fakeHid = new SliceBFakeHidDevice();
        var cfg = new It5711Config
        {
            Argb = new()
            {
                ["DLED1"] = new ArgbHeaderConfig { Header = 1, Leds = 32 },
                ["DLED2"] = new ArgbHeaderConfig { Header = 2, Leds = 32 },
                ["DLED3"] = new ArgbHeaderConfig { Header = 3, Leds = 32 },
            }
        };

        var device = new It5711Device(cfg, () => fakeHid, _ => { });
        bool initialized = device.Initialize();
        Assert.True(initialized);

        // Sequence: 0x60, 0x61, 0x20..0x27, 0x90..0x92, 0x28 FF 07, 0x31 00, 0x34 00 00, 0x32 0x0B
        var cmds = fakeHid.SentReports.Select(r => r[1]).ToList();
        var expectedCmds = new List<byte>
        {
            0x60,
            0x61,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
            0x90, 0x91, 0x92,
            0x28, // 0x28 FF 07
            0x31, // 0x31 00
            0x34, // 0x34 00 00
            0x32  // 0x32 0x0B
        };

        Assert.Equal(expectedCmds, cmds);

        var pkt28 = fakeHid.SentReports.First(r => r[1] == 0x28);
        Assert.Equal(0xFF, pkt28[2]);
        Assert.Equal(0x07, pkt28[3]);

        var pkt31 = fakeHid.SentReports.First(r => r[1] == 0x31);
        Assert.Equal(0x00, pkt31[2]);

        var pkt34 = fakeHid.SentReports.First(r => r[1] == 0x34);
        Assert.Equal(0x00, pkt34[2]);
        Assert.Equal(0x00, pkt34[3]);

        var pkt32 = fakeHid.SentReports.First(r => r[1] == 0x32);
        Assert.Equal(0x0B, pkt32[2]); // D_LED1|2|3 = 0x01 | 0x02 | 0x08 = 0x0B

        // Never 0x47 or 0x5E
        Assert.DoesNotContain(fakeHid.SentReports, r => r[1] == 0x47 || r[1] == 0x5E);
    }

    [Fact]
    public void Test05_It5711Device_Flush_OnlyChangedZonesAndApply()
    {
        var fakeHid = new SliceBFakeHidDevice();
        var cfg = new It5711Config
        {
            Argb = new()
            {
                ["A1"] = new ArgbHeaderConfig { Header = 1, Leds = 10 },
                ["A2"] = new ArgbHeaderConfig { Header = 2, Leds = 10 }
            },
            Single = new()
            {
                ["S1"] = 1, // mask 0x02
                ["S2"] = 3  // mask 0x08
            }
        };

        var device = new It5711Device(cfg, () => fakeHid, _ => { });
        device.Initialize();
        device.Flush(); // clears initial dirty state
        fakeHid.SentReports.Clear();

        // No changes: Flush sends nothing
        device.Flush();
        Assert.Empty(fakeHid.SentReports);

        // Change single zones S1 and S2
        device.SetZone("S1", [new RgbColor(10, 20, 30)]);
        device.SetZone("S2", [new RgbColor(40, 50, 60)]);
        device.Flush();

        // Expect StaticEffect for S1 (0x21), StaticEffect for S2 (0x23), and one Apply (0x28) with mask 0x0A
        Assert.Equal(3, fakeHid.SentReports.Count);
        Assert.Equal(0x21, fakeHid.SentReports[0][1]);
        Assert.Equal(0x23, fakeHid.SentReports[1][1]);
        Assert.Equal(0x28, fakeHid.SentReports[2][1]);

        uint applyMask = BinaryPrimitives.ReadUInt32LittleEndian(fakeHid.SentReports[2].AsSpan(2, 4));
        Assert.Equal(0x0Au, applyMask);

        fakeHid.SentReports.Clear();

        // Change only ARGB zone A1
        device.SetZone("A1", [new RgbColor(255, 0, 0)]);
        device.Flush();

        // Expect only RGB packets for A1 (header 1 -> 0x58), no Apply packet
        Assert.Single(fakeHid.SentReports);
        Assert.Equal(0x58, fakeHid.SentReports[0][1]);
    }

    [Fact]
    public void Test06_Ene_Discover_ExistingChips()
    {
        var smbus = new SliceBFakeSmbus();
        smbus.Chips[0x71] = new SliceBFakeEneChip();
        smbus.Chips[0x73] = new SliceBFakeEneChip();

        var cfg = new EneConfig
        {
            Addresses = ["0x71", "0x73"],
            Remap = true
        };

        var devices = EneDramDevice.Discover(cfg, smbus, _ => { });

        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Id == "ene-0x71");
        Assert.Contains(devices, d => d.Id == "ene-0x73");

        // No write to 0x77
        Assert.DoesNotContain(smbus.Writes, w => w.Address == 0x77);
    }

    [Fact]
    public void Test07b_Ene_Discover_RemapSkipsEmptySlots()
    {
        // Dual channel: sticks in slots 1 and 3, slots 0 and 2 empty.
        var smbus = new SliceBFakeSmbus();
        smbus.Chips[0x77] = new SliceBFakeEneChip { Slot = 1 };
        smbus.PendingChipsAt77.Enqueue(new SliceBFakeEneChip { Slot = 3 });

        var devices = EneDramDevice.Discover(new EneConfig { Addresses = ["0x71", "0x73"], Remap = true }, smbus, _ => { });

        Assert.Equal(2, devices.Count);
        Assert.Equal(1, smbus.Chips[0x71].Slot);
        Assert.Equal(3, smbus.Chips[0x73].Slot);
        Assert.False(smbus.Chips.ContainsKey(0x77));
    }

    [Fact]
    public void Test07_Ene_Discover_RemapChipsAt77()
    {
        var smbus = new SliceBFakeSmbus();
        smbus.Chips[0x77] = new SliceBFakeEneChip(); // First chip at 0x77
        smbus.PendingChipsAt77.Enqueue(new SliceBFakeEneChip()); // Second chip waiting at 0x77

        var cfg = new EneConfig
        {
            Addresses = ["0x71", "0x73"],
            Remap = true
        };

        var devices = EneDramDevice.Discover(cfg, smbus, _ => { });

        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Id == "ene-0x71");
        Assert.Contains(devices, d => d.Id == "ene-0x73");

        // Chips moved to 0x71 and 0x73
        Assert.True(smbus.Chips.ContainsKey(0x71));
        Assert.True(smbus.Chips.ContainsKey(0x73));
    }

    [Fact]
    public void Test08_Ene_AddressGuard_AndCheckAddressThrows()
    {
        var allowed = new byte[] { 0x71, 0x73, 0x77 };
        Assert.Throws<InvalidOperationException>(() => EneDramDevice.CheckAddress(0x50, allowed));

        var smbus = new SliceBFakeSmbus();
        smbus.Chips[0x71] = new SliceBFakeEneChip();
        smbus.Chips[0x73] = new SliceBFakeEneChip();

        var cfg = new EneConfig
        {
            Addresses = ["0x71", "0x73"],
            Remap = false
        };

        var devices = EneDramDevice.Discover(cfg, smbus, _ => { });
        foreach (var dev in devices)
        {
            dev.Initialize();
            dev.SetZone("DRAM", [new RgbColor(255, 255, 255)]);
            dev.Flush();
        }

        Assert.NotEmpty(smbus.TouchedAddresses);
        Assert.All(smbus.TouchedAddresses, a => Assert.Contains(a, new byte[] { 0x71, 0x73, 0x77 }));
    }

    [Fact]
    public void Test09_Ene_Flush_WritesRbgForChangedLedsOnly()
    {
        var smbus = new SliceBFakeSmbus();
        var chip = new SliceBFakeEneChip();
        smbus.Chips[0x71] = chip;

        var dev = new EneDramDevice(0x71, smbus, [0x71, 0x77], _ => { });
        dev.Initialize();
        dev.Flush();
        smbus.BlockWrites.Clear();

        // Change only LED 1 (color R=0x11, G=0x22, B=0x33)
        var colors = new RgbColor[8];
        colors[1] = new RgbColor(0x11, 0x22, 0x33);
        dev.SetZone("DRAM", colors);
        dev.Flush();

        // Expect exactly 1 block write for LED 1 at 0x8100 + 3*1 = 0x8103
        Assert.Single(smbus.BlockWrites);
        var write = smbus.BlockWrites[0];
        Assert.Equal(0x71, write.Address);
        Assert.Equal(0x8103, write.Register);
        Assert.Equal(3, write.Data.Length);

        // Byte order is R, B, G
        Assert.Equal(0x11, write.Data[0]); // R
        Assert.Equal(0x33, write.Data[1]); // B
        Assert.Equal(0x22, write.Data[2]); // G
    }

    [Fact]
    public void Test10_Ene_RejectMicronAt1030()
    {
        var smbus = new SliceBFakeSmbus();
        var chip = new SliceBFakeEneChip();
        chip.SetTypeAt1030("Micron");
        smbus.Chips[0x71] = chip;

        var cfg = new EneConfig
        {
            Addresses = ["0x71"],
            Remap = false
        };

        var devices = EneDramDevice.Discover(cfg, smbus, _ => { });
        Assert.Empty(devices);
    }
}
