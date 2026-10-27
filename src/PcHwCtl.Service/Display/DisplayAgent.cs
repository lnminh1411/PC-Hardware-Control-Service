using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PcHwCtl.Core;
using PcHwCtl.Core.Display;

namespace PcHwCtl.Service.Display;

/// <summary>
/// "pchwctl display-agent": the user-session half of display control. The service runs in session 0, which sees
/// no monitors, so it starts this in the logged-on user's session and relays "display ..." commands over
/// \\.\pipe\PcHwCtl-display (same words without "display"). Exits when the parent service process is gone.
/// </summary>
public static class DisplayAgent
{
    public const string PipeName = "PcHwCtl-display";

    public static async Task<int> RunAsync(string configPath, int? parentPid)
    {
        string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcHwCtl", "display-agent.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        void Log(string message)
        {
            try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}"); } catch (IOException) { }
        }

        var config = new ReloadingDisplaysConfig(configPath, Log);
        var controller = new DisplayController(new DdcDisplayBus(), config.Get, SystemClock.Instance);
        using var cts = new CancellationTokenSource();
        Log($"Display agent started (session {Process.GetCurrentProcess().SessionId}, parent {parentPid?.ToString() ?? "none"}).");

        var poller = new DisplayPoller(controller, () => config.Get().PollS, Log);
        poller.Start(cts.Token);

        if (parentPid is int pid)
        {
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(5000);
                    if (!IsAlive(pid))
                    {
                        Log($"Parent {pid} gone, exiting.");
                        cts.Cancel();
                    }
                }
            });
        }

        try
        {
            await ServeAsync(controller, Log, cts.Token);
        }
        catch (IOException ex)
        {
            // Pipe name taken: another agent already serves this session.
            Log($"Pipe {PipeName} unavailable ({ex.Message}), exiting.");
            return 1;
        }

        Log("Display agent stopped.");
        return 0;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task ServeAsync(DisplayController controller, Action<string> log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is string line)
                {
                    string[] tokens = line.TrimStart('\uFEFF').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string reply;
                    try
                    {
                        reply = DisplayCommands.Execute(controller, tokens);
                    }
                    catch (Exception ex)
                    {
                        reply = $"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}";
                    }
                    if (tokens.Length > 0 && !tokens[0].Equals("list", StringComparison.OrdinalIgnoreCase)) log($"{line} -> {reply}");
                    await writer.WriteLineAsync(reply);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Client went away mid-conversation.
            }
        }
    }

    /// <summary>The service (SYSTEM), administrators and the user this agent runs as.</summary>
    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var me = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>The displays section of the service config, reread when the file changes.</summary>
    private sealed class ReloadingDisplaysConfig(string path, Action<string> log)
    {
        private DisplaysConfig _current = new();
        private DateTime _mtime;

        public DisplaysConfig Get()
        {
            try
            {
                DateTime mtime = File.GetLastWriteTimeUtc(path);
                if (mtime != _mtime)
                {
                    _mtime = mtime;
                    _current = ConfigLoader.Load(path).Displays;
                    log($"Loaded displays config from {path}: {_current.Models.Count} model rule(s).");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException)
            {
                log($"Config {path} not loaded, keeping the last one: {ex.Message}");
            }
            return _current;
        }
    }
}

/// <summary>Rereads monitor state every PollS on its own thread (DDC reads are slow I2C round trips).</summary>
public sealed class DisplayPoller(DisplayController controller, Func<int> pollS, Action<string> log)
{
    private string? _lastError;

    public void Start(CancellationToken ct)
    {
        var thread = new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    controller.Refresh();
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    if (_lastError != ex.Message) log($"Display refresh failed: {ex.Message}");
                    _lastError = ex.Message;
                }
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(1, pollS())));
            }
        })
        {
            IsBackground = true,
            Name = "PcHwCtl-Displays"
        };
        thread.Start();
    }
}
