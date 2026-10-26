using System.Globalization;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PcHwCtl.Core;

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

        var engine = new FanEngine(
            config,
            hardware,
            hardware,
            SystemClock.Instance,
            msg => logger.Log(msg),
            isDryRun);

        var host = new EngineHost(
            config,
            effectiveConfigPath,
            hardware,
            engine,
            logger,
            isConsole);

        // Register shutdown handlers (Ctrl+C in console mode cancels the loop, which then shuts down)
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            host.ShutdownAll();
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

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
