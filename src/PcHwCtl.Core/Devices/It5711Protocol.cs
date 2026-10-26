using System.Buffers.Binary;
using PcHwCtl.Core.Rgb;

namespace PcHwCtl.Core.Devices;

/// <summary>
/// Device information decoded from IT5711 command 0x60 response.
/// </summary>
public sealed record It5711Info(
    string FirmwareVersion,
    byte SupportCmdFlag,
    uint CalibrationDled1,
    uint CalibrationDled2,
    uint CalibrationMainboard);

/// <summary>
/// Extra calibration values decoded from IT5711 command 0x61 response.
/// </summary>
public sealed record It5711ExtraCalibration(
    uint CalibrationDled3,
    uint CalibrationDled4);

/// <summary>
/// Relative positions of Red, Green, and Blue inside a 3-byte LED triple.
/// </summary>
public readonly record struct ByteOrder(int PosR, int PosG, int PosB)
{
    /// <summary>
    /// Decodes a 32-bit calibration value from the controller.
    /// Byte 0 is blue pos, byte 1 is green pos, byte 2 is red pos. Returns null if 0 or invalid.
    /// </summary>
    public static ByteOrder? Decode(uint value)
    {
        if (value == 0) return null;

        int b = (int)(value & 0xFF);
        int g = (int)((value >> 8) & 0xFF);
        int r = (int)((value >> 16) & 0xFF);

        if (r > 2 || g > 2 || b > 2) return null;
        if (r == g || r == b || g == b) return null;

        return new ByteOrder(r, g, b);
    }

    /// <summary>
    /// Parses a 3-character string such as "RGB", "GRB", "BRG" into a <see cref="ByteOrder"/>.
    /// </summary>
    public static ByteOrder Parse(string order)
    {
        if (string.IsNullOrWhiteSpace(order) || order.Length != 3)
        {
            throw new FormatException($"Invalid byte order '{order}'. Expected 3 characters (e.g. GRB).");
        }

        int posR = order.IndexOf('R', StringComparison.OrdinalIgnoreCase);
        int posG = order.IndexOf('G', StringComparison.OrdinalIgnoreCase);
        int posB = order.IndexOf('B', StringComparison.OrdinalIgnoreCase);

        if (posR < 0 || posG < 0 || posB < 0 || posR > 2 || posG > 2 || posB > 2 ||
            posR == posG || posR == posB || posG == posB)
        {
            throw new FormatException($"Invalid byte order '{order}'. Must contain R, G, and B.");
        }

        return new ByteOrder(posR, posG, posB);
    }
}

/// <summary>
/// Pure packet builders and parsers for the Gigabyte IT5711 RGB controller.
/// </summary>
public static class It5711Protocol
{
    /// <summary>
    /// Builds a 64-byte simple command packet: [0]=0xCC, [1]=cmd, [2]=a, [3]=b, rest 0.
    /// </summary>
    public static byte[] Simple(byte cmd, byte a, byte b)
    {
        byte[] packet = new byte[64];
        packet[0] = 0xCC;
        packet[1] = cmd;
        packet[2] = a;
        packet[3] = b;
        return packet;
    }

    /// <summary>
    /// Maps LED count to the IT5711 class: 0 (&lt;=32), 1 (&lt;=64), 2 (&lt;=256), 3 (&lt;=512), 4 (1024).
    /// </summary>
    public static int LedCountClass(int leds)
    {
        if (leds <= 32) return 0;
        if (leds <= 64) return 1;
        if (leds <= 256) return 2;
        if (leds <= 512) return 3;
        return 4;
    }

    /// <summary>
    /// Builds per-LED color packets for an ARGB header in direct mode (up to 19 LEDs per 64-byte packet).
    /// </summary>
    public static List<byte[]> RgbPackets(int header, ReadOnlySpan<RgbColor> colors, ByteOrder order)
    {
        byte cmd = header switch
        {
            1 => 0x58,
            2 => 0x59,
            3 => 0x62,
            4 => 0x63,
            _ => throw new ArgumentOutOfRangeException(nameof(header), header, "Header must be 1..4.")
        };

        var packets = new List<byte[]>();
        if (colors.Length == 0) return packets;

        for (int i = 0; i < colors.Length; i += 19)
        {
            int count = Math.Min(19, colors.Length - i);
            byte[] pkt = new byte[64];
            pkt[0] = 0xCC;
            pkt[1] = cmd;
            ushort byteOffset = (ushort)(i * 3);
            BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(2, 2), byteOffset);
            pkt[4] = (byte)(count * 3);

            for (int j = 0; j < count; j++)
            {
                RgbColor c = colors[i + j];
                int baseIdx = 5 + j * 3;
                pkt[baseIdx + order.PosR] = c.R;
                pkt[baseIdx + order.PosG] = c.G;
                pkt[baseIdx + order.PosB] = c.B;
            }

            packets.Add(pkt);
        }

        return packets;
    }

    /// <summary>
    /// Builds a static effect packet for a single-color zone LED slot (0..10).
    /// </summary>
    public static byte[] StaticEffect(int ledIndex, RgbColor color)
    {
        if (ledIndex < 0 || ledIndex > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(ledIndex), ledIndex, "LED index must be 0..10.");
        }

        byte[] pkt = new byte[64];
        pkt[0] = 0xCC;
        pkt[1] = (byte)(ledIndex <= 7 ? 0x20 + ledIndex : 0x90 + (ledIndex - 8));
        uint mask = 1u << ledIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(2, 4), mask);
        pkt[11] = 1;   // static effect
        pkt[12] = 255; // max brightness
        pkt[13] = 0;   // min brightness
        pkt[14] = color.B;
        pkt[15] = color.G;
        pkt[16] = color.R;
        pkt[17] = 0;
        return pkt;
    }

    /// <summary>
    /// Builds an apply command packet for the specified zone mask.
    /// </summary>
    public static byte[] Apply(uint mask)
    {
        byte[] pkt = new byte[64];
        pkt[0] = 0xCC;
        pkt[1] = 0x28;
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(2, 4), mask);
        return pkt;
    }

    /// <summary>
    /// Decodes the 64-byte device info response after command 0x60.
    /// </summary>
    public static It5711Info ParseInfo(byte[] data)
    {
        if (data.Length < 64)
        {
            throw new ArgumentException("Data must be at least 64 bytes.", nameof(data));
        }

        string fwVersion = $"{data[4]}.{data[5]}.{data[6]}.{data[7]}";
        byte supportCmdFlag = data[11];
        uint calibDled1 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44, 4));
        uint calibDled2 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48, 4));
        uint calibMainboard = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(52, 4));

        return new It5711Info(fwVersion, supportCmdFlag, calibDled1, calibDled2, calibMainboard);
    }

    /// <summary>
    /// Decodes the extra calibration response after command 0x61.
    /// </summary>
    public static It5711ExtraCalibration ParseExtraCalibration(byte[] data)
    {
        if (data.Length < 12)
        {
            throw new ArgumentException("Data must be at least 12 bytes.", nameof(data));
        }

        uint calibDled3 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        uint calibDled4 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));

        return new It5711ExtraCalibration(calibDled3, calibDled4);
    }
}
