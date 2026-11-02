using System.Globalization;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PcHwCtl.Core;
using PcHwCtl.Core.Activity;
using PcHwCtl.Core.Display;
using PcHwCtl.Core.Ipc;
using PcHwCtl.Core.Rgb;
using PcHwCtl.Core.Sensors;
using PcHwCtl.Service.Activity;
using PcHwCtl.Service.Devices;
using PcHwCtl.Service.Display;
using PcHwCtl.Service.Lasso;
using PcHwCtl.Service.Ipc;
using PcHwCtl.Service.Sensors;

namespace PcHwCtl.Service;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool isConsole = false;
        bool isDryRun = false;
        bool isList = false;
        string? configPath = null;
        string? importIn = null;
        string? importOut = null;

        // pchwctl ipc <command...>: talk to the running service (pipe ACL decides access).
        if (args.Length > 0 && string.Equals(args[0], "ipc", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: pchwctl ipc <command>   e.g. pchwctl ipc \"get state\"");
                return 1;
            }
            using var ipcCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; ipcCts.Cancel(); };
            return await PipeClient.RunAsync("PcHwCtl", string.Join(' ', args.Skip(1)), Console.Out, ipcCts.Token);
        }

        // pchwctl presence-test: two camera checks in this console (the config's presence section), for diagnosing the agent's.
        if (args.Length > 0 && string.Equals(args[0], "presence-test", StringComparison.OrdinalIgnoreCase))
        {
            var cfg = ConfigLoader.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcHwCtl", "pchwctl.json")).Presence;
            using var check = new CameraPresence(() => cfg, Console.WriteLine);
            for (int i = 0; i < 2; i++) Console.WriteLine(check.Check());   // the second shows the warm time
            return 0;
        }

        // pchwctl display-agent [--config <path>] [--parent <pid>] [--pipe <name>]: user-session monitor control (no admin needed).
        if (args.Length > 0 && string.Equals(args[0], "display-agent", StringComparison.OrdinalIgnoreCase))
        {
            string agentConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcHwCtl", "pchwctl.json");
            int? parent = null;
            string pipeName = DisplayAgent.PipeName;
            for (int i = 1; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase)) agentConfig = args[++i];
                else if (string.Equals(args[i], "--parent", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[++i], out int pid)) parent = pid;
                else if (string.Equals(args[i], "--pipe", StringComparison.OrdinalIgnoreCase)) pipeName = args[++i];
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(pipeName, @"\APcHwCtl-display(-[0-9a-f]{32})?\z"))
            {
                Console.Error.WriteLine($"Bad pipe name '{pipeName}'.");
                return 2;
            }
            return await DisplayAgent.RunAsync(agentConfig, parent, pipeName);
        }

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--console", StringComparison.OrdinalIgnoreCase))
            {
                isConsole = true;
            }
            else if (string.Equals(arg, "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                isDryRun = true;
            }
            else if (string.Equals(arg, "--list", StringComparison.OrdinalIgnoreCase))
            {
                isList = true;
            }
            else if (string.Equals(arg, "--config", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    configPath = args[++i];
                }
            }
            else if (string.Equals(arg, "import-fancontrol", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 2 < args.Length)
                {
                    importIn = args[++i];
                    importOut = args[++i];
                }
                else
                {
                    Console.Error.WriteLine("Usage: pchwctl import-fancontrol <in.json> <out.json>");
                    return 1;
                }
            }
        }

        // 1. import-fancontrol command (no elevation needed)
        if (!string.IsNullOrEmpty(importIn) && !string.IsNullOrEmpty(importOut))
        {
            try
            {
                FanControlImporter.ImportFile(importIn, importOut);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"import-fancontrol error: {ex.Message}");
                return 1;
            }
        }

        // 2. Administrator elevation check
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("pchwctl needs administrator rights (PawnIO driver)");
            return 2;
        }

        // 3. --list command
        if (isList)
        {
            try
            {
                using var lhm = new LhmHardware();
                foreach (var (hw, sensor) in lhm.GetAllSensorsWithHardware())
                {
                    string valStr = sensor.Value.HasValue
                        ? sensor.Value.Value.ToString("F2", CultureInfo.InvariantCulture)
                        : "N/A";
                    Console.WriteLine($"{sensor.Identifier}\t{hw.HardwareType}\t{hw.Name}\t{sensor.SensorType}\t{sensor.Name}\t{valStr}");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error querying sensors: {ex.Message}");
                return 1;
            }
        }

        // 4. Determine configuration path
        string effectiveConfigPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PcHwCtl",
            "pchwctl.json");

        var logger = new FileLogger(consoleOutput: isConsole);
        ProcessPriority.Apply(msg => logger.Log(msg));

        if (!File.Exists(effectiveConfigPath))
        {
            logger.Log($"Config file not found: {effectiveConfigPath}");
            Console.Error.WriteLine($"Config file not found: {effectiveConfigPath}");
            return 1;
        }

        Config config;
        try
        {
            config = ConfigLoader.Load(effectiveConfigPath);
        }
        catch (ConfigException ex)
        {
            logger.Log($"Config validation error: {ex.Message}");
            Console.Error.WriteLine($"Config validation error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            logger.Log($"Failed to read config: {ex.Message}");
            Console.Error.WriteLine($"Failed to read config: {ex.Message}");
            return 1;
        }

        // 5. Initialize hardware and validate fan controls
        var hardware = new LhmHardware();
        var enabledFans = config.Fans.Values.Where(f => f.Enabled).ToList();

        if (!hardware.ValidateFanControls(enabledFans, msg => { logger.Log(msg); Console.Error.WriteLine(msg); }))
        {
            hardware.Dispose();
            return 3;
        }

        // RX 6600: its curve and Zero RPM live in the GPU firmware, written through ADL Overdrive8.
        AmdOd8FanOutput? nativeFans = !isDryRun && config.Fans.Values.Any(f => string.Equals(f.Driver, "amd-od8", StringComparison.OrdinalIgnoreCase))
            ? AmdOd8FanOutput.TryCreate(msg => logger.Log(msg))
            : null;

        var engine = new FanEngine(
            config,
            hardware,
            hardware,
            SystemClock.Instance,
            msg => logger.Log(msg),
            isDryRun,
            nativeFans);

        // RGB: dry-run computes frames but never opens the controllers.
        List<ILedDevice> devices = isDryRun ? [] : DeviceFactory.Create(config.Devices, msg => logger.Log(msg));
        var rgb = new RgbEngine(config.Rgb, devices, reference => ReadRgbSource(engine, reference), SystemClock.Instance, msg => logger.Log(msg), flushOnThreads: true);

        // Zero RPM toggles and brightness set over IPC outlive restarts; a dry run reads them but never saves.
        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcHwCtl");
        if (!isConsole) InstallAclCheck.Warn(dataDir, msg => logger.Log(msg));
        var persisted = new PersistedSettings(Path.Combine(dataDir, "settings.json"), msg => logger.Log(msg), save: !isDryRun);
        persisted.Apply(engine, rgb);

        // Monitors: session 0 (the service) sees none, so a display agent in the user's session does the DDC/CI and
        // Windows calls. Run from a console (session > 0), the service talks to the monitors itself.
        IDisplayControl? displays = null;
        AgentDisplayClient? displayClient = null;
        DisplayAgentLauncher? displayLauncher = null;
        // Not disposed: ProcessExit cancels it after Main has returned.
        var displayCts = new CancellationTokenSource();
        // Presence (webcam) and screenshots need the user's session too, so they ride on the same agent.
        IPresence? presence = null;
        ScreenUploader? screenshot = null;
        ScreenShooter? localShooter = null;
        CameraPresence? localPresence = null;
        // The service posts screenshots and keeps their deletes: the webhook never enters the user's session.
        string shotDeletes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcHwCtl", "shot-deletes.txt");
        if ((config.Displays.Enabled || config.Presence.Enabled || config.Screenshot.Enabled) && !isDryRun)
        {
            if (System.Diagnostics.Process.GetCurrentProcess().SessionId == 0)
            {
                displayClient = new AgentDisplayClient(() => config.Displays.PollS, msg => logger.Log(msg));
                displayLauncher = new DisplayAgentLauncher(Environment.ProcessPath!, effectiveConfigPath, displayClient, msg => logger.Log(msg));
                displayClient.Start();
                displayLauncher.Start();
                if (config.Displays.Enabled) displays = displayClient;
                var features = new AgentFeatures(displayClient);
                if (config.Presence.Enabled) presence = features;
                if (config.Screenshot.Enabled) screenshot = new ScreenUploader(() => config.Screenshot, features.Capture, shotDeletes, msg => logger.Log(msg));
            }
            else
            {
                DisplayController? controller = null;
                if (config.Displays.Enabled)
                {
                    controller = new DisplayController(new DdcDisplayBus(), () => config.Displays, SystemClock.Instance);
                    new DisplayPoller(controller, () => config.Displays.PollS, msg => logger.Log(msg)).Start(displayCts.Token);
                    displays = controller;
                }
                if (config.Presence.Enabled) presence = localPresence = new CameraPresence(() => config.Presence, msg => logger.Log(msg));
                if (config.Screenshot.Enabled)
                {
                    localShooter = new ScreenShooter(() => config.Screenshot, () => controller?.Displays ?? [], msg => logger.Log(msg));
                    screenshot = new ScreenUploader(() => config.Screenshot, localShooter.Capture, shotDeletes, msg => logger.Log(msg));
                }
            }
        }
        screenshot?.Start();

        // Process Lasso control needs SYSTEM (prolasso.ini, other users' affinity), so it lives here, not in the display agent.
        LassoControl? lasso = null;
        if (config.Lasso.Enabled && !isDryRun)
        {
            lasso = new LassoControl(() => config.Lasso, msg => logger.Log(msg));
            lasso.Start();
        }

        // Sensor feed: from here on the only LibreHardwareMonitor updater (split loop, see FeedConfig); fans and RGB
        // read what it leaves, and ESP32-Helper subscribes instead of running its own reader.
        // Activity for the board's night, sleep and hibernate decisions: per-exe CPU and power requests (read only).
        var processCpu = new ProcessCpuSampler(msg => logger.Log(msg));
        processCpu.Start();
        var powerRequests = new PowerRequestsPoller(() => config.Feed.RequestsPollS, msg => logger.Log(msg));
        powerRequests.Start();
        var powerPlans = new PowerPlans(msg => logger.Log(msg));
        var powerWatcher = new PowerWatcher(msg => logger.Log(msg));
        powerWatcher.Start();
        VpnWatcher? vpnWatcher = null;
        if (config.Vpn.Enabled)
        {
            vpnWatcher = new VpnWatcher(() => config.Vpn, msg => logger.Log(msg));
            vpnWatcher.Start();
        }

        SensorFeedHub? sensorHub = null;
        SensorLoop? sensorLoop = null;
        if (config.Feed.Enabled)
        {
            sensorHub = new SensorFeedHub();
            sensorLoop = new SensorLoop(hardware, () => config.Feed, sensorHub, msg => logger.Log(msg), vpnWatcher == null ? [processCpu, powerRequests, powerPlans, powerWatcher] : [processCpu, powerRequests, powerPlans, powerWatcher, vpnWatcher]);
            sensorLoop.Start();
        }

        PipeServer? pipe = null;
        EngineHost? hostRef = null;
        if (config.Ipc.Enabled)
        {
            var processor = new CommandProcessor(engine, rgb, () => config.Ipc, SystemClock.Instance, persisted, displays, lasso, sensorHub, processCpu, powerRequests,
                powerPlans, () => config.Power, new PowerActions(powerWatcher), powerWatcher, vpnWatcher, presence, screenshot);
            pipe = new PipeServer(processor, config.Ipc, () => hostRef?.TickMs ?? 1000, msg => logger.Log(msg), sensorHub);
        }

        var stateFile = new StateFileWriter(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcHwCtl", "state.txt"),
            msg => logger.Log(msg)) { Displays = displays, Power = powerRequests, Plans = powerPlans, Events = powerWatcher, Vpn = vpnWatcher };

        var host = new EngineHost(
            config,
            effectiveConfigPath,
            hardware,
            engine,
            rgb,
            pipe,
            stateFile,
            logger,
            isConsole,
            afterReload: () => persisted.Apply(engine, rgb),
            refreshHardware: sensorLoop == null);
        hostRef = host;

        // Register shutdown handlers (Ctrl+C in console mode cancels the loop, which then shuts down)
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            sensorLoop?.Dispose();
            processCpu.Dispose();
            powerRequests.Dispose();
            powerWatcher.Dispose();
            vpnWatcher?.Dispose();
            host.ShutdownAll();
            displayCts.Cancel();
            displayLauncher?.Dispose();
            displayClient?.Dispose();
            localPresence?.Dispose();
            screenshot?.Dispose();
            localShooter?.Dispose();
            lasso?.Dispose();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            logger.Log($"AppDomain unhandled exception: {e.ExceptionObject}");
            host.ShutdownAll();
        };

        if (isConsole)
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await host.RunAsync(cts.Token);
            return 0;
        }
        else
        {
            var builder = Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = "PcHwCtl";
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton(host);
                    services.AddHostedService<FanBackgroundService>();
                });

            await builder.Build().RunAsync();
            return 0;
        }
    }

    /// <summary>RGB gradient input: "fan:&lt;name&gt;.rpm|pct" from fan state, anything else as a curve source.</summary>
    private static float? ReadRgbSource(FanEngine engine, string reference)
    {
        if (reference.StartsWith("fan:", StringComparison.OrdinalIgnoreCase))
        {
            string spec = reference[4..];
            int dot = spec.LastIndexOf('.');
            if (dot <= 0 || !engine.States.TryGetValue(spec[..dot], out var state)) return null;
            return spec[(dot + 1)..].ToLowerInvariant() switch
            {
                "rpm" => state.Rpm,
                "pct" => state.CurrentPct,
                _ => null
            };
        }

        return engine.TryReadSource(reference, out float value) ? value : null;
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
