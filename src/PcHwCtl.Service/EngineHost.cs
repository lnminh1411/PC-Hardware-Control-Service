using System.Diagnostics;
using System.Runtime.InteropServices;
using PcHwCtl.Core;
using PcHwCtl.Core.Rgb;
using PcHwCtl.Service.Ipc;

namespace PcHwCtl.Service;

public sealed class EngineHost : IDisposable
{
    private readonly string _configPath;
    private readonly LhmHardware _hardware;
    private readonly FanEngine _engine;
    private readonly RgbEngine? _rgb;
    private readonly PipeServer? _pipe;
    private readonly StateFileWriter? _stateFile;
    private readonly FileLogger _logger;
    private readonly bool _consoleOutput;
    private readonly Action? _afterReload;
    private readonly bool _refreshHardware;

    private DateTime _lastConfigMtime;
    private DateTime _lastTickCompleted = DateTime.UtcNow;
    private volatile bool _stopped;
    private int _shutdownRan;
    private int _tickMs;
    private double _rgbFrameMs;
    private int _consecutiveFailures;
    private const int MaxConsecutiveFailures = 5;

    public EngineHost(
        Config initialConfig,
        string configPath,
        LhmHardware hardware,
        FanEngine engine,
        RgbEngine? rgb,
        PipeServer? pipe,
        StateFileWriter? stateFile,
        FileLogger logger,
        bool consoleOutput,
        Action? afterReload = null,
        bool refreshHardware = true)
    {
        _refreshHardware = refreshHardware;
        _configPath = configPath;
        _hardware = hardware;
        _engine = engine;
        _rgb = rgb;
        _pipe = pipe;
        _stateFile = stateFile;
        _logger = logger;
        _consoleOutput = consoleOutput;
        _afterReload = afterReload;
        _tickMs = initialConfig.TickMs;
        _rgbFrameMs = 1000.0 / Math.Clamp(initialConfig.Rgb.Fps, 1, 60);

        try
        {
            _lastConfigMtime = File.GetLastWriteTimeUtc(_configPath);
        }
        catch
        {
            _lastConfigMtime = DateTime.UtcNow;
        }
    }

    public int TickMs => _tickMs;

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
        _pipe?.Start();
        Task rgbLoop = _rgb != null
            ? Task.Factory.StartNew(() => RgbLoop(cancellationToken), TaskCreationOptions.LongRunning)
            : Task.CompletedTask;

        _logger.Log("PcHwCtl engine started.");

        DateTime lastTickStart = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested && !_stopped)
        {
            DateTime tickStart = DateTime.UtcNow;

            // A gap far longer than the tick period means the PC slept: the board may have
            // dropped PWM and RGB state, so write everything again.
            if ((tickStart - lastTickStart).TotalMilliseconds > _tickMs * 3 + 2000)
            {
                _logger.Log($"Resume detected ({(tickStart - lastTickStart).TotalSeconds:0} s gap), rewriting fans and RGB.");
                _engine.ForceRewrite();
                _rgb?.InvalidateAll();
            }
            lastTickStart = tickStart;

            try
            {
                // 1. Config hot reload
                CheckConfigReload();

                // 2. Hardware refresh, unless the sensor loop keeps the values fresh
                if (_refreshHardware) _hardware.Refresh();

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

            // 5. State file for ESP32PcStats
            if (_stateFile != null)
            {
                float? mix = _engine.TryReadSource("mix", out float mixValue) ? mixValue : null;
                _stateFile.Write(_engine, _rgb, mix);
            }

            // 6. Console status line in console mode
            if (_consoleOutput)
            {
                var fanStatus = _engine.States.Select(kv =>
                {
                    string tempStr = kv.Value.CurveTemp.HasValue
                        ? $"{kv.Value.CurveTemp.Value:0.#}C"
                        : "N/A";
                    string rpmStr = kv.Value.Rpm.HasValue ? $" {kv.Value.Rpm.Value:0}rpm" : "";
                    return $"{kv.Key}={kv.Value.CurrentPct:0.#}%@{tempStr}{rpmStr}";
                });
                string line = string.Join(" ", fanStatus);
                if (_rgb != null)
                {
                    line += " | rgb " + string.Join(" ", _rgb.DeviceStatus.Select(kv => $"{kv.Key}={kv.Value}"));
                    line += " | " + _rgb.TakeTimingReport();
                }
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

        _stopped = true;
        try
        {
            await rgbLoop;
        }
        catch (Exception ex)
        {
            _logger.Log($"RGB loop ended with error: {ex.Message}");
        }

        ShutdownAll();
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMs);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMs);

    private void RgbLoop(CancellationToken cancellationToken)
    {
        // Below the fan tick: on a slow or crowded CPU, frames may drop, fan updates may not.
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        // The default 15.6 ms timer tick cannot pace 60 fps; ask for 1 ms while the loop runs.
        timeBeginPeriod(1);
        try
        {
            var clock = Stopwatch.StartNew();
            double nextFrameMs = 0;
            while (!cancellationToken.IsCancellationRequested && !_stopped)
            {
                try
                {
                    _rgb!.Tick();
                }
                catch (Exception ex)
                {
                    // RgbEngine handles device faults itself; anything here is a bug, keep the fans running.
                    _logger.Log($"RGB frame failed: {ex.Message}");
                }

                // Fixed schedule; after a stall, restart from now instead of bursting to catch up.
                nextFrameMs += _rgbFrameMs;
                double nowMs = clock.Elapsed.TotalMilliseconds;
                if (nextFrameMs < nowMs) nextFrameMs = nowMs;
                int sleepMs = (int)(nextFrameMs - nowMs);
                if (sleepMs > 0) Thread.Sleep(sleepMs);
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
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
                    _rgb?.ReplaceConfig(newConfig.Rgb);
                    _tickMs = newConfig.TickMs;
                    _rgbFrameMs = 1000.0 / Math.Clamp(newConfig.Rgb.Fps, 1, 60);
                    _afterReload?.Invoke();
                    _logger.Log("Config successfully reloaded. Device and IPC settings apply after a restart.");
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

            // Fans first: they are the part that must reach a safe state.
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
                _pipe?.Stop();
            }
            catch (Exception ex)
            {
                _logger.Log($"Error stopping IPC: {ex.Message}");
            }

            // A hung RGB device must not keep the process alive (watchdog path), so give it 2 s.
            if (_rgb != null)
            {
                var rgbShutdown = Task.Run(_rgb.Shutdown);
                try
                {
                    if (!rgbShutdown.Wait(TimeSpan.FromSeconds(2)))
                    {
                        _logger.Log("RGB shutdown timed out after 2 s.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error during RGB shutdown: {ex.InnerException?.Message ?? ex.Message}");
                }
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
