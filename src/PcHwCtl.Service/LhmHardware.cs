using LibreHardwareMonitor.Hardware;
using PcHwCtl.Core;

namespace PcHwCtl.Service;

public sealed class LhmHardware : ISensorSource, IFanOutput, IDisposable
{
    private readonly Computer _computer;
    // Refresh runs on the fan thread while the RGB thread reads sensors: Refresh builds new collections and swaps
    // them in under the gate, so readers never see a half-filled index and never wait for hardware.Update.
    private readonly object _gate = new();
    private Dictionary<string, ISensor> _sensorsById = new(StringComparer.OrdinalIgnoreCase);
    private List<(IHardware Hardware, ISensor Sensor)> _allSensors = [];
    private readonly Dictionary<string, ISensor> _resolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _hardwareChanged;

    public LhmHardware()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = true, // DIMM SPD temperatures for the sensor feed (ram_temp), ~1.3 ms per stick
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false
        };
        _computer.Open();
        _computer.HardwareAdded += _ => _hardwareChanged = true;
        _computer.HardwareRemoved += _ => _hardwareChanged = true;
        Refresh();
    }

    public IComputer Computer => _computer;

    /// <summary>True once after hardware was added or removed since the last call.</summary>
    public bool TakeHardwareChanged()
    {
        if (!_hardwareChanged) return false;
        _hardwareChanged = false;
        return true;
    }

    /// <summary>Some sensor reference (a fan source, a tach) matched nothing at its last lookup.</summary>
    public bool HasUnresolved
    {
        get
        {
            lock (_gate) return _unresolved.Count > 0;
        }
    }

    /// <summary>Hardware holding a sensor something has looked up: what a partial refresh must update.</summary>
    public IReadOnlyCollection<IHardware> ResolvedHardware()
    {
        lock (_gate) return _resolutionCache.Values.Select(s => s.Hardware).Distinct().ToList();
    }

    public void Refresh()
    {
        var byId = new Dictionary<string, ISensor>(StringComparer.OrdinalIgnoreCase);
        var all = new List<(IHardware Hardware, ISensor Sensor)>();

        foreach (var hw in _computer.Hardware)
        {
            UpdateHardware(hw);
            CollectSensors(hw, byId, all);
        }

        lock (_gate)
        {
            _sensorsById = byId;
            _allSensors = all;
        }
    }

    private void UpdateHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
        {
            UpdateHardware(sub);
        }
    }

    private static void CollectSensors(IHardware hardware, Dictionary<string, ISensor> byId, List<(IHardware, ISensor)> all)
    {
        foreach (var sensor in hardware.Sensors)
        {
            string id = sensor.Identifier.ToString();
            byId[id] = sensor;
            all.Add((hardware, sensor));
        }

        foreach (var sub in hardware.SubHardware)
        {
            CollectSensors(sub, byId, all);
        }
    }

    public IEnumerable<(IHardware Hardware, ISensor Sensor)> GetAllSensorsWithHardware()
    {
        lock (_gate) return _allSensors;
    }

    public ISensor? ResolveSensor(string reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        lock (_gate) return ResolveSensorLocked(reference);
    }

    private ISensor? ResolveSensorLocked(string reference)
    {

        if (_resolutionCache.TryGetValue(reference, out var cached))
        {
            return cached;
        }

        ISensor? resolved = null;

        if (reference.StartsWith('/'))
        {
            string? matchedId = IdentifierMatcher.Match(reference, _sensorsById.Keys);
            if (matchedId != null && _sensorsById.TryGetValue(matchedId, out var sensor))
            {
                resolved = sensor;
            }
        }
        else if (reference.StartsWith("sel:", StringComparison.OrdinalIgnoreCase))
        {
            // sel:<HardwareType>|<SensorType>|<SensorName>
            string selector = reference["sel:".Length..];
            string[] parts = selector.Split('|');
            if (parts.Length >= 3)
            {
                string hwType = parts[0];
                string sType = parts[1];
                string sName = parts[2];

                foreach (var (hardware, sensor) in _allSensors)
                {
                    if (string.Equals(hardware.HardwareType.ToString(), hwType, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(sensor.SensorType.ToString(), sType, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(sensor.Name, sName, StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = sensor;
                        break;
                    }
                }
            }
        }

        if (resolved != null)
        {
            _resolutionCache[reference] = resolved;
            _unresolved.Remove(reference);
        }
        else
        {
            _unresolved.Add(reference);
        }

        return resolved;
    }

    public bool TryRead(string reference, out float value)
    {
        var sensor = ResolveSensor(reference);
        if (sensor != null && sensor.Value.HasValue && !float.IsNaN(sensor.Value.Value))
        {
            value = sensor.Value.Value;
            return true;
        }

        value = 0f;
        return false;
    }

    public void Set(string controlId, float percent)
    {
        var sensor = ResolveSensor(controlId);
        if (sensor?.Control != null)
        {
            sensor.Control.SetSoftware(percent);
        }
    }

    public void SetDefault(string controlId)
    {
        var sensor = ResolveSensor(controlId);
        if (sensor?.Control != null)
        {
            sensor.Control.SetDefault();
        }
    }

    public bool ValidateFanControls(IEnumerable<FanConfig> enabledFans, Action<string> log)
    {
        var availableControls = GetAllSensorsWithHardware()
            .Where(x => x.Sensor.SensorType == SensorType.Control && x.Sensor.Control != null)
            .Select(x => x.Sensor.Identifier.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool allValid = true;
        foreach (var fan in enabledFans)
        {
            var sensor = ResolveSensor(fan.Control);
            if (sensor == null || sensor.SensorType != SensorType.Control || sensor.Control == null)
            {
                log($"Control identifier '{fan.Control}' could not be resolved to a Control sensor.");
                allValid = false;
            }
        }

        if (!allValid)
        {
            log("Available Control sensor identifiers:");
            foreach (var ctrl in availableControls)
            {
                log($"  {ctrl}");
            }
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        _computer.Close();
    }
}
