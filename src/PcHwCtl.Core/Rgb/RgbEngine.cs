using System.Globalization;
using PcHwCtl.Core.Control;

namespace PcHwCtl.Core.Rgb;

/// <summary>
/// Core RGB lighting engine that coordinates hardware devices, evaluates lighting rules,
/// manages overrides, and buffers zone colors with frame-level caching.
/// </summary>
public sealed class RgbEngine : IRgbControl, IDisposable
{
    private sealed class GroupOverride
    {
        public RgbRuleConfig Rule { get; }
        public DateTime? ExpiresUtc { get; }

        public GroupOverride(RgbRuleConfig rule, DateTime? expiresUtc)
        {
            Rule = rule;
            ExpiresUtc = expiresUtc;
        }
    }

    private sealed class GroupEmaState
    {
        public bool HasSample;
        public float SmoothedValue;
        public DateTime LastSampleTime;
    }

    private readonly struct ResolvedZone
    {
        public ILedDevice Device { get; }
        public string ZoneName { get; }
        public int LedCount { get; }

        public ResolvedZone(ILedDevice device, string zoneName, int ledCount)
        {
            Device = device;
            ZoneName = zoneName;
            LedCount = ledCount;
        }
    }

    private readonly object _gate = new();
    private readonly IReadOnlyList<ILedDevice> _devices;
    private readonly Dictionary<string, ILedDevice> _devicesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, float?> _readSource;
    private readonly IClock _clock;
    private readonly Action<string> _log;
    private readonly DateTime _startTime;

    private RgbConfig _config;
    private float _brightness;
    private bool _disposed;

    private readonly Dictionary<string, string> _deviceStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastInitAttempt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GroupOverride> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GroupEmaState> _emaStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RgbColor[]> _groupColorCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedUnknowns = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DeviceFlusher> _flushers = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _flushOnThreads;
    private int _framesSinceReport;
    private DateTime _lastReport;

    /// <summary>
    /// Owns all writes to one device. Tick only stages the newest colors per zone; SetZone, Flush,
    /// Initialize, Invalidate and Dispose run under DeviceLock. With threads on, each device flushes
    /// on its own thread, so a slow device (ENE on the SMBus) never holds up the frame or the others.
    /// Lock order: the engine gate may take DeviceLock and the stage lock; the device thread never takes the gate.
    /// </summary>
    private sealed class DeviceFlusher
    {
        public readonly ILedDevice Device;
        public readonly object DeviceLock = new();
        private readonly object _stageLock = new();
        private readonly Dictionary<string, RgbColor[]> _staged = new(StringComparer.OrdinalIgnoreCase);
        private readonly AutoResetEvent _signal = new(false);
        private readonly IClock _clock;
        private Thread? _thread;
        private volatile bool _stop;
        private DateTime? _lastFlush;
        private int _count;
        private double _totalMs;
        private double _maxMs;

        public volatile int CapFps;
        public volatile bool Healthy;
        public volatile string? Fault;

        public DeviceFlusher(ILedDevice device, IClock clock)
        {
            Device = device;
            _clock = clock;
        }

        public void Stage(string zone, ReadOnlySpan<RgbColor> colors)
        {
            lock (_stageLock) _staged[zone] = colors.ToArray();
        }

        private bool HasStaged
        {
            get { lock (_stageLock) return _staged.Count > 0; }
        }

        /// <summary>Time left before the fps cap allows the next flush.</summary>
        public TimeSpan WaitForCap(DateTime now)
        {
            int cap = CapFps;
            if (cap <= 0 || _lastFlush == null) return TimeSpan.Zero;
            TimeSpan left = TimeSpan.FromMilliseconds(1000.0 / cap - 1.0) - (now - _lastFlush.Value);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        /// <summary>Writes the staged zones and flushes. Returns the error message, or null.</summary>
        public string? FlushNow(DateTime now)
        {
            Dictionary<string, RgbColor[]> batch;
            lock (_stageLock)
            {
                if (_staged.Count == 0) return null;
                batch = new Dictionary<string, RgbColor[]>(_staged, StringComparer.OrdinalIgnoreCase);
                _staged.Clear();
            }

            _lastFlush = now;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                lock (DeviceLock)
                {
                    foreach (var (zone, colors) in batch)
                    {
                        Device.SetZone(zone, colors);
                    }
                    Device.Flush();
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            lock (_stageLock)
            {
                _count++;
                _totalMs += ms;
                _maxMs = Math.Max(_maxMs, ms);
            }
            return null;
        }

        public (int Count, double TotalMs, double MaxMs) TakeStats()
        {
            lock (_stageLock)
            {
                var result = (_count, _totalMs, _maxMs);
                _count = 0;
                _totalMs = 0;
                _maxMs = 0;
                return result;
            }
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = $"PcHwCtl-Rgb-{Device.Id}", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Signal() => _signal.Set();

        public bool Stop(TimeSpan timeout)
        {
            _stop = true;
            _signal.Set();
            return _thread == null || _thread.Join(timeout);
        }

        private void Run()
        {
            while (!_stop)
            {
                _signal.WaitOne();
                while (!_stop && Healthy && HasStaged)
                {
                    TimeSpan wait = WaitForCap(_clock.UtcNow);
                    if (wait > TimeSpan.Zero)
                    {
                        Thread.Sleep(wait); // newer colors keep replacing the staged ones meanwhile
                        continue;
                    }

                    string? fault = FlushNow(_clock.UtcNow);
                    if (fault != null)
                    {
                        // The engine marks the device faulted on its next Tick and retries Initialize.
                        Healthy = false;
                        Fault = fault;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets group configuration status, active rules, and override states.
    /// </summary>
    public IReadOnlyDictionary<string, RgbGroupState> Groups
    {
        get
        {
            lock (_gate)
            {
                var result = new Dictionary<string, RgbGroupState>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, groupCfg) in _config.Groups)
                {
                    bool hasOverride = _overrides.TryGetValue(name, out var ovr);
                    string configuredRule = groupCfg.Rule?.Type ?? "off";
                    string? overrideRule = hasOverride && ovr != null ? ovr.Rule.Type : null;
                    DateTime? expiresUtc = hasOverride && ovr != null ? ovr.ExpiresUtc : null;
                    result[name] = new RgbGroupState(configuredRule, overrideRule, expiresUtc);
                }
                return result;
            }
        }
    }

    /// <summary>
    /// Gets device health statuses mapped by device identifier ("ok" or "faulted: &lt;reason&gt;").
    /// </summary>
    public IReadOnlyDictionary<string, string> DeviceStatus
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_deviceStatus, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Gets or sets global brightness percentage in range 0..100.
    /// </summary>
    public float Brightness
    {
        get
        {
            lock (_gate) return _brightness;
        }
        set
        {
            lock (_gate)
            {
                _brightness = Math.Clamp(value, 0f, 100f);
                _groupColorCache.Clear();
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RgbEngine"/> class.
    /// </summary>
    public RgbEngine(
        RgbConfig config,
        IReadOnlyList<ILedDevice> devices,
        Func<string, float?> readSource,
        IClock clock,
        Action<string> log,
        bool flushOnThreads = false)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _readSource = readSource ?? throw new ArgumentNullException(nameof(readSource));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _startTime = _clock.UtcNow;
        _lastReport = _startTime;
        _flushOnThreads = flushOnThreads;
        _brightness = Math.Clamp(config.Brightness, 0f, 100f);

        foreach (var device in _devices)
        {
            _devicesById[device.Id] = device;

            try
            {
                if (device.Initialize())
                {
                    _deviceStatus[device.Id] = "ok";
                }
                else
                {
                    _deviceStatus[device.Id] = "faulted: initialization failed";
                    _lastInitAttempt[device.Id] = _startTime;
                    _log($"RGB device '{device.Id}' faulted: initialization failed");
                }
            }
            catch (Exception ex)
            {
                _deviceStatus[device.Id] = $"faulted: {ex.Message}";
                _lastInitAttempt[device.Id] = _startTime;
                _log($"RGB device '{device.Id}' faulted: {ex.Message}");
            }

            var flusher = new DeviceFlusher(device, _clock) { Healthy = _deviceStatus[device.Id] == "ok" };
            _flushers[device.Id] = flusher;
            if (_flushOnThreads) flusher.Start();
        }
    }

    /// <summary>
    /// Sets a temporary or persistent rule override on a group or all groups.
    /// </summary>
    public void SetOverride(string group, RgbRuleConfig rule, TimeSpan? ttl)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(rule);

            DateTime? expiresUtc = ttl.HasValue ? _clock.UtcNow + ttl.Value : null;

            if (string.Equals(group, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var groupName in _config.Groups.Keys)
                {
                    _overrides[groupName] = new GroupOverride(rule, expiresUtc);
                }
            }
            else
            {
                if (!_config.Groups.ContainsKey(group))
                {
                    throw new ArgumentException($"Unknown RGB group '{group}'", nameof(group));
                }

                _overrides[group] = new GroupOverride(rule, expiresUtc);
            }

            _groupColorCache.Clear();
        }
    }

    /// <summary>
    /// Clears any active override on a specific group or all groups.
    /// </summary>
    public void ClearOverride(string group)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(group);

            if (string.Equals(group, "all", StringComparison.OrdinalIgnoreCase))
            {
                _overrides.Clear();
            }
            else
            {
                _overrides.Remove(group);
            }

            _groupColorCache.Clear();
        }
    }

    /// <summary>
    /// Advances the engine by one frame, evaluating rules, checking overrides, and flushing changed zones.
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed) return;

            DateTime now = _clock.UtcNow;

            // 0. Faults raised on device threads since the last frame
            foreach (var flusher in _flushers.Values)
            {
                if (flusher.Fault is string fault)
                {
                    flusher.Fault = null;
                    MarkFaulted(flusher.Device, fault);
                }
            }

            // 1. Expiry checked each Tick
            var expired = new List<string>();
            foreach (var (grp, ovr) in _overrides)
            {
                if (ovr.ExpiresUtc.HasValue && now >= ovr.ExpiresUtc.Value)
                {
                    expired.Add(grp);
                }
            }

            if (expired.Count > 0)
            {
                foreach (var grp in expired)
                {
                    _overrides.Remove(grp);
                }
                _groupColorCache.Clear();
            }

            // 2. Retry Initialize for faulted devices at most every 10 s
            foreach (var device in _devices)
            {
                if (_deviceStatus.TryGetValue(device.Id, out var status) && status.StartsWith("faulted", StringComparison.OrdinalIgnoreCase))
                {
                    if (_lastInitAttempt.TryGetValue(device.Id, out var lastAttempt) && (now - lastAttempt).TotalSeconds >= 10.0)
                    {
                        _lastInitAttempt[device.Id] = now;
                        var flusher = _flushers[device.Id];
                        try
                        {
                            bool ok;
                            lock (flusher.DeviceLock)
                            {
                                ok = device.Initialize();
                                if (ok)
                                {
                                    try { device.Invalidate(); } catch { }
                                }
                            }

                            if (ok)
                            {
                                _deviceStatus[device.Id] = "ok";
                                flusher.Healthy = true;
                                _log($"RGB device '{device.Id}' recovered");
                                _groupColorCache.Clear();
                            }
                        }
                        catch (Exception ex)
                        {
                            _deviceStatus[device.Id] = $"faulted: {ex.Message}";
                        }
                    }
                }
            }

            // 3. Time elapsed since start
            float t = Math.Max(0f, (float)(now - _startTime).TotalSeconds);

            // 4. Track devices that get new colors this frame
            var devicesToFlush = new HashSet<DeviceFlusher>();

            // 5. Per group, evaluate active rule
            foreach (var (groupName, groupCfg) in _config.Groups)
            {
                RgbRuleConfig? activeRule = _overrides.TryGetValue(groupName, out var ovr) ? ovr.Rule : groupCfg.Rule;
                if (activeRule == null) continue;

                var resolvedZones = ResolveZonesForGroup(groupName, groupCfg);
                int totalLeds = resolvedZones.Sum(z => z.LedCount);
                if (totalLeds == 0) continue;

                RgbColor[] computedColors = ComputeGroupColors(groupName, activeRule, totalLeds, t, now);

                // Multiply by global brightness / 100
                float brightFactor = _brightness / 100f;
                if (brightFactor < 1f)
                {
                    for (int i = 0; i < computedColors.Length; i++)
                    {
                        computedColors[i] = computedColors[i].Scale(brightFactor);
                    }
                }

                // Skip SetZone+Flush for a group whose computed colors equal previous frame's
                if (_groupColorCache.TryGetValue(groupName, out var cachedColors) && ColorsEqual(cachedColors, computedColors))
                {
                    continue;
                }

                _groupColorCache[groupName] = computedColors;

                int offset = 0;
                foreach (var rZone in resolvedZones)
                {
                    var zoneColors = computedColors.AsSpan(offset, rZone.LedCount);
                    offset += rZone.LedCount;

                    if (_deviceStatus.TryGetValue(rZone.Device.Id, out var devStatus) && devStatus == "ok")
                    {
                        var flusher = _flushers[rZone.Device.Id];
                        flusher.Stage(rZone.ZoneName, zoneColors);
                        devicesToFlush.Add(flusher);
                    }
                }
            }

            // 6. Hand new colors to each device, which flushes at most at its own rate cap.
            //    Colors a capped device skipped stay staged and go out on its next slot.
            _framesSinceReport++;
            foreach (var flusher in _flushers.Values)
            {
                flusher.CapFps = FpsCapFor(flusher.Device.Id);
                if (!flusher.Healthy) continue;

                if (_flushOnThreads)
                {
                    if (devicesToFlush.Contains(flusher)) flusher.Signal();
                }
                else if (flusher.WaitForCap(now) == TimeSpan.Zero && flusher.FlushNow(now) is string fault)
                {
                    MarkFaulted(flusher.Device, fault);
                }
            }
        }
    }

    /// <summary>
    /// Swaps the active configuration, retains overrides for surviving groups, and clears color caches.
    /// </summary>
    public void ReplaceConfig(RgbConfig config)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(config);
            _config = config;
            _brightness = Math.Clamp(config.Brightness, 0f, 100f);

            var toRemove = _overrides.Keys
                .Where(grp => !_config.Groups.ContainsKey(grp))
                .ToList();
            foreach (var grp in toRemove)
            {
                _overrides.Remove(grp);
            }

            var removedEma = _emaStates.Keys
                .Where(grp => !_config.Groups.ContainsKey(grp))
                .ToList();
            foreach (var grp in removedEma)
            {
                _emaStates.Remove(grp);
            }

            _groupColorCache.Clear();
        }
    }

    /// <summary>
    /// Invalidates all devices and clears frame color caches (typically called after system resume).
    /// </summary>
    public void InvalidateAll()
    {
        lock (_gate)
        {
            foreach (var device in _devices)
            {
                try
                {
                    lock (_flushers[device.Id].DeviceLock) device.Invalidate();
                }
                catch (Exception ex)
                {
                    _log($"RGB device '{device.Id}' Invalidate threw: {ex.Message}");
                }
            }

            _groupColorCache.Clear();
        }
    }

    /// <summary>
    /// Shuts down the engine and disposes all managed devices. Idempotent.
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var flusher in _flushers.Values)
            {
                if (!flusher.Stop(TimeSpan.FromMilliseconds(500)))
                {
                    _log($"RGB device '{flusher.Device.Id}' flush thread did not stop within 500 ms");
                }
            }

            foreach (var device in _devices)
            {
                // A device stuck mid-flush keeps its lock; skip it rather than hang shutdown.
                object deviceLock = _flushers[device.Id].DeviceLock;
                if (!Monitor.TryEnter(deviceLock, TimeSpan.FromMilliseconds(500)))
                {
                    _log($"RGB device '{device.Id}' busy, skipped Dispose");
                    continue;
                }
                try
                {
                    device.Dispose();
                }
                catch (Exception ex)
                {
                    _log($"RGB device '{device.Id}' Dispose threw: {ex.Message}");
                }
                finally
                {
                    Monitor.Exit(deviceLock);
                }
            }
        }
    }

    /// <summary>
    /// Disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        Shutdown();
    }

    /// <summary>
    /// Frame rate and per-device flush timing since the last call, e.g. "52 fps, it5711 60/s avg 1.1 max 2.0 ms". Resets the counters.
    /// </summary>
    public string TakeTimingReport()
    {
        lock (_gate)
        {
            DateTime now = _clock.UtcNow;
            double seconds = Math.Max((now - _lastReport).TotalSeconds, 0.001);
            var parts = new List<string> { $"{_framesSinceReport / seconds:0} fps" };
            foreach (var (id, flusher) in _flushers)
            {
                var (count, totalMs, maxMs) = flusher.TakeStats();
                if (count == 0) continue;
                parts.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{id} {count / seconds:0}/s avg {totalMs / count:0.0} max {maxMs:0.0} ms"));
            }
            _framesSinceReport = 0;
            _lastReport = now;
            return string.Join(", ", parts);
        }
    }

    private int FpsCapFor(string deviceId)
    {
        if (_config.DeviceFps.TryGetValue(deviceId, out int exact)) return exact;
        foreach (var (key, fps) in _config.DeviceFps)
        {
            if (key.EndsWith('*') && deviceId.StartsWith(key[..^1], StringComparison.OrdinalIgnoreCase)) return fps;
        }
        return 0;
    }

    /// <summary>
    /// Gets the current smoothed EMA value for a gradient group, or null if no sample exists.
    /// </summary>
    public float? GetGroupSmoothedValue(string group)
    {
        lock (_gate)
        {
            return _emaStates.TryGetValue(group, out var s) && s.HasSample ? s.SmoothedValue : null;
        }
    }

    private void MarkFaulted(ILedDevice device, string reason)
    {
        string status = $"faulted: {reason}";
        bool wasOk = !_deviceStatus.TryGetValue(device.Id, out var current) || current == "ok";
        _deviceStatus[device.Id] = status;
        _lastInitAttempt[device.Id] = _clock.UtcNow;
        if (_flushers.TryGetValue(device.Id, out var flusher)) flusher.Healthy = false;

        if (wasOk)
        {
            _log($"RGB device '{device.Id}' faulted: {reason}");
        }
    }

    private List<ResolvedZone> ResolveZonesForGroup(string groupName, RgbGroupConfig groupCfg)
    {
        var list = new List<ResolvedZone>();
        if (groupCfg.Zones == null) return list;

        foreach (var zoneRef in groupCfg.Zones)
        {
            if (string.IsNullOrWhiteSpace(zoneRef)) continue;

            int slash = zoneRef.IndexOf('/');
            if (slash <= 0 || slash == zoneRef.Length - 1)
            {
                string key = $"badref:{groupName}:{zoneRef}";
                if (_loggedUnknowns.Add(key))
                {
                    _log($"Invalid zone reference '{zoneRef}' in group '{groupName}'.");
                }
                continue;
            }

            string deviceId = zoneRef[..slash];
            string zoneSpec = zoneRef[(slash + 1)..];

            if (!_devicesById.TryGetValue(deviceId, out var device))
            {
                string key = $"dev:{groupName}:{deviceId}";
                if (_loggedUnknowns.Add(key))
                {
                    _log($"Unknown RGB device '{deviceId}' in group '{groupName}'.");
                }
                continue;
            }

            if (zoneSpec == "*")
            {
                if (device.Zones != null)
                {
                    foreach (var z in device.Zones)
                    {
                        if (z.LedCount > 0)
                        {
                            list.Add(new ResolvedZone(device, z.Name, z.LedCount));
                        }
                    }
                }
            }
            else
            {
                var match = device.Zones?.FirstOrDefault(z => string.Equals(z.Name, zoneSpec, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    string key = $"zone:{groupName}:{deviceId}/{zoneSpec}";
                    if (_loggedUnknowns.Add(key))
                    {
                        _log($"Unknown RGB zone '{zoneSpec}' on device '{deviceId}' in group '{groupName}'.");
                    }
                    continue;
                }

                if (match.LedCount > 0)
                {
                    list.Add(new ResolvedZone(device, match.Name, match.LedCount));
                }
            }
        }

        return list;
    }

    private RgbColor[] ComputeGroupColors(string groupName, RgbRuleConfig rule, int ledCount, float t, DateTime now)
    {
        var colors = new RgbColor[ledCount];
        string ruleType = rule.Type?.ToLowerInvariant() ?? "off";

        switch (ruleType)
        {
            case "off":
                Array.Fill(colors, RgbColor.Black);
                break;

            case "static":
                RgbColor staticColor = RgbColor.Black;
                if (!string.IsNullOrEmpty(rule.Color) && RgbColor.TryParse(rule.Color, out var parsedStatic))
                {
                    staticColor = parsedStatic;
                }
                Array.Fill(colors, staticColor);
                break;

            case "breathing":
                RgbColor baseColor = RgbColor.Black;
                if (!string.IsNullOrEmpty(rule.Color) && RgbColor.TryParse(rule.Color, out var parsedBase))
                {
                    baseColor = parsedBase;
                }
                float period = rule.PeriodS > 0f ? rule.PeriodS : 1f;
                float factor = (1f - MathF.Cos(2f * MathF.PI * t / period)) / 2f;
                RgbColor breathingColor = baseColor.Scale(factor);
                Array.Fill(colors, breathingColor);
                break;

            case "rainbow":
                float rainbowPeriod = rule.PeriodS > 0f ? rule.PeriodS : 1f;
                for (int i = 0; i < ledCount; i++)
                {
                    float frac = (t / rainbowPeriod) + ((float)i / ledCount);
                    colors[i] = HsvToRgb(frac, 1f, 1f);
                }
                break;

            case "gradient":
                RgbColor gradientColor = EvaluateGradient(groupName, rule, now);
                Array.Fill(colors, gradientColor);
                break;

            default:
                Array.Fill(colors, RgbColor.Black);
                break;
        }

        return colors;
    }

    private RgbColor EvaluateGradient(string groupName, RgbRuleConfig rule, DateTime now)
    {
        float? rawSourceValue = null;
        if (!string.IsNullOrWhiteSpace(rule.Source))
        {
            rawSourceValue = _readSource(rule.Source);
        }

        if (!_emaStates.TryGetValue(groupName, out var emaState))
        {
            emaState = new GroupEmaState();
            _emaStates[groupName] = emaState;
        }

        if (rawSourceValue == null)
        {
            if (!string.IsNullOrEmpty(rule.Fallback) && RgbColor.TryParse(rule.Fallback, out var fallbackColor))
            {
                return fallbackColor;
            }

            if (rule.Stops != null && rule.Stops.Count > 0)
            {
                var lastStop = rule.Stops.OrderBy(s => s.At).Last();
                if (RgbColor.TryParse(lastStop.Color, out var lastColor))
                {
                    return lastColor;
                }
            }

            return RgbColor.Black;
        }

        float sourceVal = rawSourceValue.Value;

        if (!emaState.HasSample || rule.SmoothingS <= 0f)
        {
            emaState.SmoothedValue = sourceVal;
            emaState.HasSample = true;
            emaState.LastSampleTime = now;
        }
        else
        {
            float dt = (float)(now - emaState.LastSampleTime).TotalSeconds;
            emaState.LastSampleTime = now;
            if (dt > 0f)
            {
                float alpha = 1f - MathF.Exp(-dt / rule.SmoothingS);
                emaState.SmoothedValue += alpha * (sourceVal - emaState.SmoothedValue);
            }
        }

        float val = emaState.SmoothedValue;

        if (rule.Stops == null || rule.Stops.Count == 0)
        {
            return RgbColor.Black;
        }

        var sortedStops = rule.Stops.OrderBy(s => s.At).ToList();
        if (sortedStops.Count == 1)
        {
            return RgbColor.TryParse(sortedStops[0].Color, out var sc) ? sc : RgbColor.Black;
        }

        if (val <= sortedStops[0].At)
        {
            return RgbColor.TryParse(sortedStops[0].Color, out var sc) ? sc : RgbColor.Black;
        }

        if (val >= sortedStops[^1].At)
        {
            return RgbColor.TryParse(sortedStops[^1].Color, out var sc) ? sc : RgbColor.Black;
        }

        for (int k = 0; k < sortedStops.Count - 1; k++)
        {
            var stop0 = sortedStops[k];
            var stop1 = sortedStops[k + 1];

            if (val >= stop0.At && val <= stop1.At)
            {
                if (!RgbColor.TryParse(stop0.Color, out var c0)) c0 = RgbColor.Black;
                if (!RgbColor.TryParse(stop1.Color, out var c1)) c1 = RgbColor.Black;

                float range = stop1.At - stop0.At;
                float tInterp = range > 0f ? (val - stop0.At) / range : 0f;

                float r = c0.R + tInterp * (c1.R - c0.R);
                float g = c0.G + tInterp * (c1.G - c0.G);
                float b = c0.B + tInterp * (c1.B - c0.B);

                byte rByte = (byte)Math.Clamp(MathF.Round(r, MidpointRounding.AwayFromZero), 0, 255);
                byte gByte = (byte)Math.Clamp(MathF.Round(g, MidpointRounding.AwayFromZero), 0, 255);
                byte bByte = (byte)Math.Clamp(MathF.Round(b, MidpointRounding.AwayFromZero), 0, 255);

                return new RgbColor(rByte, gByte, bByte);
            }
        }

        return RgbColor.Black;
    }

    private static RgbColor HsvToRgb(float hue, float s, float v)
    {
        float h = hue - MathF.Floor(hue);
        float h6 = h * 6f;
        int sector = ((int)MathF.Floor(h6)) % 6;
        if (sector < 0) sector += 6;
        float f = h6 - MathF.Floor(h6);

        float p = v * (1f - s);
        float q = v * (1f - s * f);
        float t = v * (1f - s * (1f - f));

        float r, g, b;
        switch (sector)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }

        return new RgbColor(
            (byte)Math.Clamp(MathF.Round(r * 255f, MidpointRounding.AwayFromZero), 0, 255),
            (byte)Math.Clamp(MathF.Round(g * 255f, MidpointRounding.AwayFromZero), 0, 255),
            (byte)Math.Clamp(MathF.Round(b * 255f, MidpointRounding.AwayFromZero), 0, 255)
        );
    }

    private static bool ColorsEqual(RgbColor[] a, RgbColor[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }
}
