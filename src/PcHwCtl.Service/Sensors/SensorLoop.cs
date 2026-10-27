using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using PcHwCtl.Core.Activity;
using PcHwCtl.Core.Sensors;

namespace PcHwCtl.Service.Sensors;

/// <summary>
/// The only LibreHardwareMonitor updater once started (EngineHost stops refreshing): the helper's split loop from
/// ESP32-Helper's PcStats.cs, see FeedConfig. Fans, RGB and the feed all read the values it leaves behind.
/// </summary>
public sealed class SensorLoop : IDisposable
{
    private readonly LhmHardware _lhm;
    private readonly Func<FeedConfig> _config;
    private readonly SensorFeedHub _hub;
    private readonly Action<string> _log;
    private readonly IReadOnlyList<IFeedSource> _extras;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<IHardware, long> _lastUpdateMs = new();
    private readonly HashSet<IHardware> _updating = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private List<(SensorRule Rule, List<ISensor> Sensors)> _rules = [];
    private List<IHardware> _feedHardware = [];
    private Dictionary<string, string>? _rulesFrom;
    private long _nextResolveMs;
    private long _seq;
    private Thread? _thread;

    public SensorLoop(LhmHardware lhm, Func<FeedConfig> config, SensorFeedHub hub, Action<string> log, IReadOnlyList<IFeedSource>? extras = null)
    {
        _extras = extras ?? [];
        _lhm = lhm;
        _config = config;
        _hub = hub;
        _log = log;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PcHwCtl-Sensors" };
        _thread.Start();
    }

    private void Run()
    {
        bool resolve = true;
        long slowLoggedMs = -60_000;
        while (!_cts.IsCancellationRequested)
        {
            var cfg = _config();
            int interval = Math.Clamp(cfg.IntervalMs, 100, 10_000);
            long t0 = _clock.ElapsedMilliseconds;
            try
            {
                if (!ReferenceEquals(cfg.Keys, _rulesFrom)) resolve = true;
                if (_lhm.TakeHardwareChanged()) resolve = true;
                if (t0 >= _nextResolveMs && (_lhm.HasUnresolved || _rules.Any(r => r.Sensors.Count == 0))) resolve = true;

                if (resolve)
                {
                    resolve = false;
                    FullUpdateAndResolve(cfg);
                    _nextResolveMs = t0 + Math.Max(5, cfg.ResolveS) * 1000L;
                }
                else
                {
                    PartialUpdate(cfg, t0, interval);
                }

                Publish();
            }
            catch (Exception ex)
            {
                _log($"Sensors: update failed: {ex.Message}");
            }

            long took = _clock.ElapsedMilliseconds - t0;
            if (took > interval * 8 / 10 && t0 - slowLoggedMs >= 60_000)
            {
                _log($"Sensors: hardware update took {took} ms of a {interval} ms interval.");
                slowLoggedMs = t0;
            }
            _cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Max(10, interval - took)));
        }
    }

    /// <summary>Everything, then sensor lookup again. Waits for background updates so none overlaps.</summary>
    private void FullUpdateAndResolve(FeedConfig cfg)
    {
        while (true)
        {
            lock (_updating) if (_updating.Count == 0) break;
            Thread.Sleep(10);
        }
        _lhm.Refresh();

        var rules = new List<(SensorRule, List<ISensor>)>();
        foreach (var (key, spec) in cfg.Keys)
        {
            SensorRule rule;
            try
            {
                rule = SensorRule.Parse(key, spec);
            }
            catch (FormatException ex)
            {
                _log("Sensors: " + ex.Message);
                continue;
            }
            var matched = _lhm.GetAllSensorsWithHardware()
                .Where(x => rule.Matches(Chain(x.Hardware), x.Sensor.SensorType.ToString(), x.Sensor.Name))
                .Select(x => x.Sensor)
                .ToList();
            rules.Add((rule, matched));
        }
        _rules = rules;
        _rulesFrom = cfg.Keys;

        _feedHardware = rules.SelectMany(r => r.Item2).Select(s => s.Hardware).Distinct().ToList();

        var missing = rules.Where(r => r.Item2.Count == 0).Select(r => r.Item1.Key).ToList();
        _log($"Sensors: {rules.Count} keys on {Hot().Count} hardware" + (missing.Count > 0 ? $", nothing matches {string.Join(", ", missing)}" : "") + ".");
    }

    private static List<(string Type, string Name)> Chain(IHardware hardware)
    {
        var chain = new List<(string, string)>();
        for (IHardware? h = hardware; h != null; h = h.Parent) chain.Add((h.HardwareType.ToString(), h.Name));
        return chain;
    }

    /// <summary>What the feed reads plus what fans and RGB have looked up so far (they may look up after a resolve).</summary>
    private List<IHardware> Hot() => _feedHardware.Concat(_lhm.ResolvedHardware()).Distinct().ToList();

    /// <summary>Hot hardware only: Every-matched ones in the background when due, the rest in parallel now.</summary>
    private void PartialUpdate(FeedConfig cfg, long t0, int interval)
    {
        var due = new List<IHardware>();
        foreach (var hw in Hot())
        {
            int every = EveryMs(cfg, hw);
            if (every <= 0)
            {
                due.Add(hw);
                continue;
            }
            if (_lastUpdateMs.TryGetValue(hw, out long last) && t0 - last < every - interval / 2) continue;
            if (!UpdateInBackground(hw)) continue; // the previous one is still running
            _lastUpdateMs[hw] = t0;
        }
        if (due.Count > 0) Parallel.ForEach(due, hw => hw.Update());
    }

    private static int EveryMs(FeedConfig cfg, IHardware hw)
    {
        foreach (var (pattern, ms) in cfg.Every)
        {
            if (Wildcard.Match(pattern, hw.HardwareType.ToString()) || Wildcard.Match(pattern, hw.Name)) return ms;
        }
        return 0;
    }

    private bool UpdateInBackground(IHardware hw)
    {
        lock (_updating) if (!_updating.Add(hw)) return false;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                hw.Update();
            }
            catch (Exception)
            {
                // A failed read leaves the last values; the next due update tries again.
            }
            finally
            {
                lock (_updating) _updating.Remove(hw);
            }
        });
        return true;
    }

    private void Publish()
    {
        var values = new List<KeyValuePair<string, double?>>(_rules.Count);
        foreach (var (rule, sensors) in _rules)
        {
            var read = sensors.Where(s => s.Value.HasValue).Select(s => (double)s.Value!.Value);
            values.Add(new(rule.Key, rule.Apply(read)));
        }
        var texts = new List<KeyValuePair<string, string>>();
        foreach (var source in _extras) source.AddTo(values, texts);
        _hub.Publish(new SensorSnapshot(++_seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), values, texts));
    }

    public void Dispose() => _cts.Cancel();
}
