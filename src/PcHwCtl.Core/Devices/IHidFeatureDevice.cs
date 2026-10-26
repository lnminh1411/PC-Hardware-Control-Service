namespace PcHwCtl.Core.Devices;

/// <summary>
/// Abstraction for a USB HID device exposing 64-byte feature reports.
/// </summary>
public interface IHidFeatureDevice : IDisposable
{
    /// <summary>Sends a 64-byte feature report. Throws IOException on failure.</summary>
    void SetFeature(byte[] report64);

    /// <summary>Reads a 64-byte feature report beginning with the specified report id.</summary>
    byte[] GetFeature(byte reportId);
}
