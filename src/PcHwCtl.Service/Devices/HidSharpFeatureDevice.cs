using System.IO;
using HidSharp;
using PcHwCtl.Core.Devices;

namespace PcHwCtl.Service.Devices;

/// <summary>
/// Wraps a HidSharp <see cref="HidStream"/> for 64-byte HID feature report I/O.
/// </summary>
public sealed class HidSharpFeatureDevice : IHidFeatureDevice
{
    private readonly HidStream _stream;
    private bool _disposed;

    public HidSharpFeatureDevice(HidStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Searches for IT5711 devices (VID 0x048D, PID 0x5711), logs candidate paths,
    /// and opens the first one with MaxFeatureReportLength &gt;= 64.
    /// </summary>
    public static HidSharpFeatureDevice? OpenIt5711(Action<string> log)
    {
        log ??= (_ => { });

        try
        {
            var candidates = DeviceList.Local.GetHidDevices(0x048D, 0x5711);
            foreach (var device in candidates)
            {
                string path = device.DevicePath;
                int maxFeature = device.GetMaxFeatureReportLength();
                log($"IT5711 candidate: path='{path}', maxFeature={maxFeature}");

                if (maxFeature >= 64)
                {
                    if (device.TryOpen(out HidStream stream))
                    {
                        log($"IT5711 opened: '{path}'");
                        return new HidSharpFeatureDevice(stream);
                    }
                    log($"IT5711 candidate: failed to open '{path}'");
                }
            }
        }
        catch (Exception ex)
        {
            log($"IT5711 search failed: {ex.Message}");
        }

        return null;
    }

    /// <inheritdoc />
    public void SetFeature(byte[] report64)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (report64 == null) throw new ArgumentNullException(nameof(report64));

        byte[] buffer;
        if (report64.Length == 64)
        {
            buffer = report64;
        }
        else
        {
            buffer = new byte[64];
            Array.Copy(report64, buffer, Math.Min(report64.Length, 64));
        }

        try
        {
            _stream.SetFeature(buffer);
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException($"SetFeature failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public byte[] GetFeature(byte reportId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] buffer = new byte[64];
        buffer[0] = reportId;

        try
        {
            _stream.GetFeature(buffer);
            return buffer;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException($"GetFeature failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
