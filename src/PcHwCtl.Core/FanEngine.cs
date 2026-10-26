using PcHwCtl.Core.Control;
using System.Text.Json;

namespace PcHwCtl.Core;

public sealed record OverrideInfo(float Pct, DateTime ExpiresAtUtc);

public sealed record FanState(
    float CurrentPct,
    float TargetPct,
    float? CurveTemp,
    bool SourceMissing,
    OverrideInfo? Override,
    float? Rpm = null,
    string Curve = "");

public sealed class FanEngine : IFanControl
{
    private sealed class FanRuntime
    {
        public string Name { get; }
        public FanConfig Config { get; set; }
        public IFanCurve Curve { get; set; }
        public float PreviousOutput { get; set; }
        public bool HasTicked { get; set; }
        public bool WasMissing { get; set; }

        // IPC curve switch; its own state instance so the configured curve keeps its history.
        public string? OverrideCurveName { get; set; }
        public IFanCurve? OverrideCurve { get; set; }

        public FanRuntime(string name, FanConfig config, IFanCurve curve)
        {
            Name = name;
            Config = config;
            Curve = curve;
        }
    }

    private Config _config;
    private readonly ISensorSource _sensorSource;
    private readonly IFanOutput _fanOutput;
    private readonly IClock _clock;
    private readonly Action<string> _log;
    private readonly bool _dryRun;

    private readonly Dictionary<string, FanRuntime> _fans = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastGoodTime = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _lastGoodValue = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OverrideInfo> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writtenControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FanState> _states = new(StringComparer.OrdinalIgnoreCase);

    // Serializes Tick against overrides, reloads and shutdown, which arrive from other threads.
    private readonly object _gate = new();

    public IReadOnlyDictionary<string, FanState> States
    {
        get { lock (_gate) return new Dictionary<string, FanState>(_states, StringComparer.OrdinalIgnoreCase); }
    }

    public FanEngine(
        Config cfg,
        ISensorSource src,
        IFanOutput @out,
        IClock clock,
        Action<string> log,
        bool dryRun)
    {
        _config = cfg;
        _sensorSource = src;
        _fanOutput = @out;
        _clock = clock;
        _log = log;
        _dryRun = dryRun;

        InitializeFans();
    }

    private void InitializeFans()
    {
        foreach (var (name, fanCfg) in _config.Fans)
        {
            if (fanCfg.Enabled && _config.Curves.TryGetValue(fanCfg.Curve, out var curveCfg))
            {
                var curve = CreateCurve(curveCfg);
                _fans[name] = new FanRuntime(name, fanCfg, curve);
            }
        }
    }

    private static IFanCurve CreateCurve(CurveConfig cfg)
    {
        if (string.Equals(cfg.Type, "graph", StringComparison.OrdinalIgnoreCase))
        {
            var points = (cfg.Points ?? []).Select(p => (p[0], p[1])).ToArray();
            return new GraphCurve(
                points,
                cfg.HysteresisUp,
                cfg.HysteresisDown,
                cfg.ResponseUpS,
                cfg.ResponseDownS,
                cfg.IgnoreHysteresisAtLimits);
        }
        else if (string.Equals(cfg.Type, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return new AutoCurve(
                cfg.IdleTemp,
                cfg.LoadTemp,
                cfg.MinPct,
                cfg.MaxPct,
                cfg.Step,
                cfg.Deadband,
                cfg.ResponseS);
        }
        else if (string.Equals(cfg.Type, "flat", StringComparison.OrdinalIgnoreCase))
        {
            return new FlatCurve(cfg.Pct);
        }

        throw new InvalidOperationException($"Unknown curve type '{cfg.Type}'.");
    }

    public IReadOnlyCollection<string> FanNames
    {
        get { lock (_gate) return _fans.Keys.ToList(); }
    }

    public bool HasCurve(string curve)
    {
        lock (_gate) return _config.Curves.ContainsKey(curve);
    }

    public void SetCurveOverride(string fan, string curve)
    {
        lock (_gate)
        {
            if (!_fans.TryGetValue(fan, out var runtime)) throw new ArgumentException($"Unknown fan '{fan}'.");
            if (!_config.Curves.TryGetValue(curve, out var curveCfg)) throw new ArgumentException($"Unknown curve '{curve}'.");
            runtime.OverrideCurveName = curve;
            runtime.OverrideCurve = CreateCurve(curveCfg);
        }
    }

    public void ClearCurveOverride(string fan)
    {
        lock (_gate)
        {
            if (_fans.TryGetValue(fan, out var runtime))
            {
                runtime.OverrideCurveName = null;
                runtime.OverrideCurve = null;
            }
        }
    }

    /// <summary>Reads a sensor reference or virtual sensor the same way curves do. Used by RGB gradients.</summary>
    public bool TryReadSource(string reference, out float value)
    {
        lock (_gate)
        {
            float? v = ReadSource(reference, _clock.UtcNow, out bool missing);
            value = v ?? 0f;
            return !missing && v.HasValue;
        }
    }

    /// <summary>After resume: next tick rewrites every fan even if the value did not change.</summary>
    public void ForceRewrite()
    {
        lock (_gate)
        {
            foreach (var fan in _fans.Values) fan.HasTicked = false;
        }
    }

    public void SetOverride(string fan, float pct, TimeSpan ttl)
    {
        lock (_gate) _overrides[fan] = new OverrideInfo(pct, _clock.UtcNow + ttl);
    }

    public void ClearOverride(string fan)
    {
        lock (_gate) _overrides.Remove(fan);
    }

    public void ClearAllOverrides()
    {
        lock (_gate) _overrides.Clear();
    }

    public void Shutdown()
    {
        // A hung tick holds the gate; after 2 s release the fans anyway.
        bool locked = Monitor.TryEnter(_gate, TimeSpan.FromSeconds(2));
        try
        {
            foreach (var controlId in _writtenControls.ToList())
            {
                _fanOutput.SetDefault(controlId);
            }
            _writtenControls.Clear();
        }
        finally
        {
            if (locked) Monitor.Exit(_gate);
        }
    }

    public void ReplaceConfig(Config newConfig)
    {
        lock (_gate) ReplaceConfigLocked(newConfig);
    }

    private static bool SameCurve(CurveConfig a, CurveConfig b) =>
        JsonSerializer.Serialize(a, ConfigLoader.JsonOptions) == JsonSerializer.Serialize(b, ConfigLoader.JsonOptions);

    private void ReleaseControl(string controlId)
    {
        if (_writtenControls.Remove(controlId))
        {
            _fanOutput.SetDefault(controlId);
        }
    }

    private void ReplaceConfigLocked(Config newConfig)
    {
        var existingNames = _fans.Keys.ToList();

        foreach (var name in existingNames)
        {
            var fanRuntime = _fans[name];
            if (newConfig.Fans.TryGetValue(name, out var newFanCfg) && newFanCfg.Enabled)
            {
                // Kept fan: rebuild the curve when its name or its settings changed
                bool curveChanged = !string.Equals(fanRuntime.Config.Curve, newFanCfg.Curve, StringComparison.OrdinalIgnoreCase) ||
                    !_config.Curves.TryGetValue(fanRuntime.Config.Curve, out var oldCurveCfg) ||
                    !newConfig.Curves.TryGetValue(newFanCfg.Curve, out var newCurveCfg) ||
                    !SameCurve(oldCurveCfg, newCurveCfg);
                if (curveChanged && newConfig.Curves.TryGetValue(newFanCfg.Curve, out var rebuilt))
                {
                    fanRuntime.Curve = CreateCurve(rebuilt);
                }

                if (!string.Equals(fanRuntime.Config.Control, newFanCfg.Control, StringComparison.OrdinalIgnoreCase))
                {
                    ReleaseControl(fanRuntime.Config.Control);
                    fanRuntime.HasTicked = false;
                }
                fanRuntime.Config = newFanCfg;
                fanRuntime.OverrideCurveName = null;
                fanRuntime.OverrideCurve = null;
            }
            else
            {
                // Removed or disabled
                ReleaseControl(fanRuntime.Config.Control);
                _fans.Remove(name);
                _states.Remove(name);
            }
        }

        // Add any newly enabled fans
        foreach (var (name, newFanCfg) in newConfig.Fans)
        {
            if (newFanCfg.Enabled && !_fans.ContainsKey(name))
            {
                if (newConfig.Curves.TryGetValue(newFanCfg.Curve, out var newCurveCfg))
                {
                    _fans[name] = new FanRuntime(name, newFanCfg, CreateCurve(newCurveCfg));
                }
            }
        }

        _config = newConfig;
    }

    public void Tick()
    {
        lock (_gate) TickLocked();
    }

    private void TickLocked()
    {
        DateTime now = _clock.UtcNow;

        foreach (var (fanName, fan) in _fans)
        {
            if (!fan.Config.Enabled) continue;

            string curveName = fan.OverrideCurveName ?? fan.Config.Curve;
            IFanCurve curve = fan.OverrideCurve ?? fan.Curve;
            if (!_config.Curves.TryGetValue(curveName, out var curveCfg))
            {
                continue;
            }

            string? sourceRef = curveCfg.Source;
            float? temp = ReadSource(sourceRef, now, out bool isMissing);

            float target;
            float output;

            // 2. If missing: target = 100, apply immediately (no step limit)
            if (isMissing)
            {
                if (!fan.WasMissing)
                {
                    fan.WasMissing = true;
                    _log($"Fan '{fanName}': sensor '{sourceRef}' missing, failsafe 100%");
                }

                target = 100f;
                output = 100f;
            }
            else
            {
                if (fan.WasMissing)
                {
                    fan.WasMissing = false;
                    _log($"Fan '{fanName}': sensor '{sourceRef}' recovered");
                }

                // 3. Else target = curve pct + offset, clamp to [minPct, 100]
                float? curvePct = curve.Evaluate(temp, now);
                float baseTarget = (curvePct ?? 100f) + fan.Config.Offset;
                target = Math.Clamp(baseTarget, fan.Config.MinPct, 100f);

                // 4. Override replaces step 3's target when active
                if (_overrides.TryGetValue(fanName, out var ov))
                {
                    if (now >= ov.ExpiresAtUtc)
                    {
                        _overrides.Remove(fanName);
                        ov = null;
                    }
                    else
                    {
                        target = ov.Pct;
                    }
                }

                // 5. target = max(target, floorPct)
                target = Math.Max(target, fan.Config.FloorPct);

                // 6. Start: if previous output == 0 and 0 < target < startPct, target = startPct
                if (fan.PreviousOutput == 0f && target > 0f && target < fan.Config.StartPct)
                {
                    target = fan.Config.StartPct;
                }

                // 7. Step limit: change from previous output at most stepUp up / stepDown down per tick. First tick: no limit.
                if (!fan.HasTicked)
                {
                    output = target;
                }
                else
                {
                    float prev = fan.PreviousOutput;
                    if (target > prev)
                    {
                        output = Math.Min(target, prev + fan.Config.StepUp);
                    }
                    else if (target < prev)
                    {
                        output = Math.Max(target, prev - fan.Config.StepDown);
                    }
                    else
                    {
                        output = target;
                    }
                }
            }

            // 8. If output changed (or first tick): out.Set(control, value) unless dryRun
            if (!fan.HasTicked || output != fan.PreviousOutput)
            {
                if (!_dryRun)
                {
                    _fanOutput.Set(fan.Config.Control, output);
                    _writtenControls.Add(fan.Config.Control);
                }
            }

            fan.PreviousOutput = output;
            fan.HasTicked = true;

            OverrideInfo? currentOverride = null;
            if (_overrides.TryGetValue(fanName, out var activeOv) && now < activeOv.ExpiresAtUtc)
            {
                currentOverride = activeOv;
            }

            float? rpm = null;
            if (!string.IsNullOrEmpty(fan.Config.Tach) && _sensorSource.TryRead(fan.Config.Tach, out float rpmValue))
            {
                rpm = rpmValue;
            }

            _states[fanName] = new FanState(output, target, temp, isMissing, currentOverride, rpm, curveName);
        }
    }

    private float? ReadSource(string? sourceRef, DateTime now, out bool isMissing)
    {
        isMissing = false;
        float? temp = null;

        if (!string.IsNullOrEmpty(sourceRef))
        {
            if (_config.Sensors.TryGetValue(sourceRef, out var sensorCfg) &&
                string.Equals(sensorCfg.Type, "mix", StringComparison.OrdinalIgnoreCase))
            {
                // Virtual mix sensor
                var validValues = new List<float>();
                bool anyMissing = false;

                foreach (var src in sensorCfg.Sources)
                {
                    if (TryReadWithStale(src, now, out float val))
                    {
                        validValues.Add(val);
                    }
                    else
                    {
                        anyMissing = true;
                        if (!sensorCfg.AllowMissing)
                        {
                            break;
                        }
                    }
                }

                if (!sensorCfg.AllowMissing && anyMissing)
                {
                    isMissing = true;
                }
                else if (validValues.Count == 0)
                {
                    isMissing = true;
                }
                else
                {
                    if (string.Equals(sensorCfg.Fn, "min", StringComparison.OrdinalIgnoreCase))
                    {
                        temp = validValues.Min();
                    }
                    else if (string.Equals(sensorCfg.Fn, "avg", StringComparison.OrdinalIgnoreCase))
                    {
                        temp = validValues.Average();
                    }
                    else
                    {
                        temp = validValues.Max();
                    }
                }
            }
            else
            {
                // Direct sensor reference
                if (TryReadWithStale(sourceRef, now, out float val))
                {
                    temp = val;
                }
                else
                {
                    isMissing = true;
                }
            }
        }


        return temp;
    }

    private bool TryReadWithStale(string reference, DateTime now, out float value)
    {
        if (_sensorSource.TryRead(reference, out float readVal) &&
            !float.IsNaN(readVal) &&
            !float.IsInfinity(readVal))
        {
            _lastGoodTime[reference] = now;
            _lastGoodValue[reference] = readVal;
            value = readVal;
            return true;
        }

        if (_lastGoodTime.TryGetValue(reference, out var lastTime) &&
            (now - lastTime).TotalSeconds <= _config.StaleSensorS)
        {
            value = _lastGoodValue[reference];
            return true;
        }

        value = 0f;
        return false;
    }
}
