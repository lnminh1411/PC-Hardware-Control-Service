using System.Globalization;

namespace PcHwCtl.Core.Rgb;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor Black = new(0, 0, 0);

    /// <summary>Parses "#RRGGBB" or "RRGGBB".</summary>
    public static RgbColor Parse(string text)
    {
        if (!TryParse(text, out var color))
        {
            throw new FormatException($"Invalid color '{text}', expected #RRGGBB.");
        }
        return color;
    }

    public static bool TryParse(string? text, out RgbColor color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        color = new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public RgbColor Scale(float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);
        return new RgbColor((byte)MathF.Round(R * factor), (byte)MathF.Round(G * factor), (byte)MathF.Round(B * factor));
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
