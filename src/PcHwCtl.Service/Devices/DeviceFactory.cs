using System.Threading;
using PcHwCtl.Core.Devices;
using PcHwCtl.Core.Rgb;

namespace PcHwCtl.Service.Devices;

/// <summary>
/// Factory creating physical <see cref="ILedDevice"/> instances according to <see cref="DevicesConfig"/>.
/// </summary>
public static class DeviceFactory
{
    /// <summary>
    /// Creates configured RGB devices.
    /// IT5711 is added when configured and enabled.
    /// ENE DRAM devices are discovered when configured, enabled, and the PawnIO SMBus opens.
    /// All discovered ENE devices share one SMBus handle, which is disposed when the last ENE device is disposed.
    /// </summary>
    public static List<ILedDevice> Create(DevicesConfig cfg, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        log ??= (_ => { });

        var devices = new List<ILedDevice>();

        if (cfg.It5711 != null && cfg.It5711.Enabled)
        {
            var it5711 = new It5711Device(cfg.It5711, () => HidSharpFeatureDevice.OpenIt5711(log), log);
            devices.Add(it5711);
        }

        if (cfg.Ene != null && cfg.Ene.Enabled)
        {
            PawnIoSmbus? smbus = PawnIoSmbus.Open(cfg.Ene, log);
            if (smbus != null)
            {
                IReadOnlyList<EneDramDevice> eneDevices = EneDramDevice.Discover(cfg.Ene, smbus, log);
                if (eneDevices.Count == 0)
                {
                    smbus.Dispose();
                }
                else
                {
                    int refCount = eneDevices.Count;
                    foreach (EneDramDevice eneDevice in eneDevices)
                    {
                        eneDevice.Disposed += () =>
                        {
                            if (Interlocked.Decrement(ref refCount) == 0)
                            {
                                smbus.Dispose();
                            }
                        };
                        devices.Add(eneDevice);
                    }
                }
            }
        }

        return devices;
    }
}
