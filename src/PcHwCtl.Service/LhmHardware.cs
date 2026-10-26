using LibreHardwareMonitor.Hardware;
using PcHwCtl.Core;

namespace PcHwCtl.Service;

public sealed class LhmHardware : ISensorSource, IFanOutput, IDisposable
{
    private readonly Computer _computer;
    private readonly Dictionary<string, ISensor> _sensorsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(IHardware Hardware, ISensor Sensor)> _allSensors = [];
    private readonly Dictionary<string, ISensor> _resolutionCache = new(StringComparer.OrdinalIgnoreCase);

    public LhmHardware()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false
        };
        _computer.Open();
        Refresh();
    }

    public void Refresh()
    {
        _sensorsById.Clear();
        _allSensors.Clear();

        foreach (var hw in _computer.Hardware)
        {
            UpdateHardware(hw);
            CollectSensors(hw);
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

    private void CollectSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            string id = sensor.Identifier.ToString();
            _sensorsById[id] = sensor;
            _allSensors.Add((hardware, sensor));
        }

        foreach (var sub in hardware.SubHardware)
        {
            CollectSensors(sub);
        }
    }

    public IEnumerable<(IHardware Hardware, ISensor Sensor)> GetAllSensorsWithHardware()
    {
        return _allSensors;
    }

    public ISensor? ResolveSensor(string reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;

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
        var availableControls = _allSensors
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
