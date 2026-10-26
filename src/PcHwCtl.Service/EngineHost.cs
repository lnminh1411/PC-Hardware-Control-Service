using PcHwCtl.Core;

namespace PcHwCtl.Service;

public sealed class EngineHost : IDisposable
{
    private readonly string _configPath;
    private readonly LhmHardware _hardware;
    private readonly FanEngine _engine;
    private readonly FileLogger _logger;
    private readonly bool _consoleOutput;

    private DateTime _lastConfigMtime;
    private DateTime _lastTickCompleted = DateTime.UtcNow;
    private volatile bool _stopped;
    private int _shutdownRan;
    private int _tickMs;
    private int _consecutiveFailures;
    private const int MaxConsecutiveFailures = 5;

    public EngineHost(
        Config initialConfig,
        string configPath,
        LhmHardware hardware,
        FanEngine engine,
        FileLogger logger,
        bool consoleOutput)
    {
        _configPath = configPath;
        _hardware = hardware;
        _engine = engine;
        _logger = logger;
        _consoleOutput = consoleOutput;
        _tickMs = initialConfig.TickMs;

        try
        {
            _lastConfigMtime = File.GetLastWriteTimeUtc(_configPath);
        }
        catch
        {
            _lastConfigMtime = DateTime.UtcNow;
        }
    }

    public void StartWatchdog()
    {
        _lastTickCompleted = DateTime.UtcNow;
        var thread = new Thread(() =>
        {
            while (!_stopped)
            {
                Thread.Sleep(1000);
                if (!_stopped && (DateTime.UtcNow - _lastTickCompleted) > TimeSpan.FromSeconds(10))
                {
                    _logger.Log("Watchdog: no tick completed for 10 s, shutting down and exiting.");
                    ShutdownAll();
                    Environment.Exit(4);
                }
            }
        })
        {
            IsBackground = true,
            Name = "PcHwCtl-Watchdog"
        };
        thread.Start();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        StartWatchdog();

        _logger.Log("PcHwCtl engine started.");

        while (!cancellationToken.IsCancellationRequested && !_stopped)
        {
            DateTime tickStart = DateTime.UtcNow;
            try
            {
                // 1. Config hot reload
                CheckConfigReload();

                // 2. Hardware refresh
                _hardware.Refresh();

                // 3. Engine tick
                _engine.Tick();
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _logger.Log($"Tick failed ({_consecutiveFailures + 1}/{MaxConsecutiveFailures}): {ex}");
                if (++_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    // Non-zero exit so the service recovery actions restart us.
                    ShutdownAll();
                    Environment.Exit(5);
                }
            }

            // 4. Update last tick completed for watchdog
            _lastTickCompleted = DateTime.UtcNow;

            // 5. Console status line in console mode
            if (_consoleOutput)
            {
                var fanStatus = _engine.States.Select(kv =>
                {
                    string tempStr = kv.Value.CurveTemp.HasValue
                        ? $"{kv.Value.CurveTemp.Value:0.#}C"
                        : "N/A";
                    return $"{kv.Key}={kv.Value.CurrentPct:0.#}%@{tempStr}";
                });
                string line = string.Join(" ", fanStatus);
                if (!string.IsNullOrEmpty(line))
                {
                    _logger.Log(line);
                }
            }

            try
            {
                // Hardware refresh takes ~0.5 s; subtract it so ticks stay on the configured period.
                int remaining = _tickMs - (int)(DateTime.UtcNow - tickStart).TotalMilliseconds;
                await Task.Delay(Math.Max(remaining, 50), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        ShutdownAll();
    }

    private void CheckConfigReload()
    {
        try
        {
            if (!File.Exists(_configPath)) return;

            DateTime mtime = File.GetLastWriteTimeUtc(_configPath);
            if (mtime != _lastConfigMtime)
            {
                _lastConfigMtime = mtime;
                _logger.Log($"Detected config file change: {_configPath}");

                try
                {
                    Config newConfig = ConfigLoader.Load(_configPath);
                    _engine.ReplaceConfig(newConfig);
                    _tickMs = newConfig.TickMs;
                    _logger.Log("Config successfully reloaded.");
                }
                catch (ConfigException ex)
                {
                    _logger.Log($"Config reload rejected, keeping active config: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error checking config reload: {ex.Message}");
        }
    }

    public void ShutdownAll()
    {
        if (Interlocked.Exchange(ref _shutdownRan, 1) == 0)
        {
            _stopped = true;
            _logger.Log("Shutdown initiated.");

            try
            {
                _engine.Shutdown();
            }
            catch (Exception ex)
            {
                _logger.Log($"Error during engine shutdown: {ex.Message}");
            }

            try
            {
                _hardware.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Log($"Error during hardware disposal: {ex.Message}");
            }

            _logger.Log("Shutdown completed.");
        }
    }

    public void Dispose()
    {
        ShutdownAll();
    }
}
