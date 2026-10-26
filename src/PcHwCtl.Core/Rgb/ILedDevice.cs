namespace PcHwCtl.Core.Rgb;

public sealed record LedZone(string Name, int LedCount);

/// <summary>
/// One physical RGB controller. Zones are addressed in config as "&lt;Id&gt;/&lt;zone&gt;" or "&lt;Id&gt;/*".
/// Not thread-safe: only the RGB engine tick calls it.
/// </summary>
public interface ILedDevice : IDisposable
{
    /// <summary>Stable id, e.g. "it5711", "ene-0x71".</summary>
    string Id { get; }

    IReadOnlyList<LedZone> Zones { get; }

    /// <summary>Opens and configures the hardware. Returns false when unavailable (the device logs why).</summary>
    bool Initialize();

    /// <summary>Buffers colors for a zone. Extra colors are ignored, missing ones are black.</summary>
    void SetZone(string zone, ReadOnlySpan<RgbColor> colors);

    /// <summary>Sends buffered changes to the hardware. Throws on I/O failure.</summary>
    void Flush();

    /// <summary>Next Flush resends every zone (after resume or re-init).</summary>
    void Invalidate();
}
