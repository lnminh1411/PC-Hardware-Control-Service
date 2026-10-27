using System.Globalization;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PcHwCtl.Core;
using PcHwCtl.Core.Display;
using PcHwCtl.Core.Ipc;
using PcHwCtl.Core.Rgb;
using PcHwCtl.Service.Devices;
using PcHwCtl.Service.Display;
using PcHwCtl.Service.Ipc;

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

        // pchwctl display-agent [--config <path>] [--parent <pid>]: user-session monitor control (no admin needed).
        if (args.Length > 0 && string.Equals(args[0], "display-agent", StringComparison.OrdinalIgnoreCase))
        {
            string agentConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcHwCtl", "pchwctl.json");
            int? parent = null;
            for (int i = 1; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase)) agentConfig = args[++i];
                else if (string.Equals(args[i], "--parent", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[++i], out int pid)) parent = pid;
            }
            return await DisplayAgent.RunAsync(agentConfig, parent);
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
        var persisted = new PersistedSettings(Path.Combine(dataDir, "settings.json"), msg => logger.Log(msg), save: !isDryRun);
        persisted.Apply(engine, rgb);

        // Monitors: session 0 (the service) sees none, so a display agent in the user's session does the DDC/CI and
        // Windows calls. Run from a console (session > 0), the service talks to the monitors itself.
        IDisplayControl? displays = null;
        AgentDisplayClient? displayClient = null;
        DisplayAgentLauncher? displayLauncher = null;
        // Not disposed: ProcessExit cancels it after Main has returned.
        var displayCts = new CancellationTokenSource();
        if (config.Displays.Enabled && !isDryRun)
        {
            if (System.Diagnostics.Process.GetCurrentProcess().SessionId == 0)
            {
                displayClient = new AgentDisplayClient(() => config.Displays.PollS, msg => logger.Log(msg));
                displayLauncher = new DisplayAgentLauncher(Environment.ProcessPath!, effectiveConfigPath, displayClient, msg => logger.Log(msg));
                displayClient.Start();
                displayLauncher.Start();
                displays = displayClient;
            }
            else
            {
                var controller = new DisplayController(new DdcDisplayBus(), () => config.Displays, SystemClock.Instance);
                new DisplayPoller(controller, () => config.Displays.PollS, msg => logger.Log(msg)).Start(displayCts.Token);
                displays = controller;
            }
        }

        PipeServer? pipe = null;
        EngineHost? hostRef = null;
        if (config.Ipc.Enabled)
        {
            var processor = new CommandProcessor(engine, rgb, () => config.Ipc, SystemClock.Instance, persisted, displays);
            pipe = new PipeServer(processor, config.Ipc, () => hostRef?.TickMs ?? 1000, msg => logger.Log(msg));
        }

        var stateFile = new StateFileWriter(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcHwCtl", "state.txt"),
            msg => logger.Log(msg)) { Displays = displays };

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
            afterReload: () => persisted.Apply(engine, rgb));
        hostRef = host;

        // Register shutdown handlers (Ctrl+C in console mode cancels the loop, which then shuts down)
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            host.ShutdownAll();
            displayCts.Cancel();
            displayLauncher?.Dispose();
            displayClient?.Dispose();
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
