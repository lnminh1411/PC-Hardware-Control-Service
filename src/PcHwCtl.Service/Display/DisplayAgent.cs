using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PcHwCtl.Core;
using PcHwCtl.Core.Activity;
using PcHwCtl.Core.Display;
using PcHwCtl.Service.Activity;

namespace PcHwCtl.Service.Display;

/// <summary>
/// "pchwctl display-agent": the user-session half of display control. The service runs in session 0, which sees
/// no monitors, so it starts this in the logged-on user's session and relays "display ..." commands over
/// \\.\pipe\PcHwCtl-display-&lt;random&gt; (same words without "display"): the service names a new pipe for every launch
/// and only talks to the process it started. Exits when the parent service process is gone.
/// </summary>
public static class DisplayAgent
{
    public const string PipeName = "PcHwCtl-display";

    public static async Task<int> RunAsync(string configPath, int? parentPid, string pipeName = PipeName)
    {
        string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcHwCtl", "display-agent.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        void Log(string message)
        {
            try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}"); } catch (IOException) { }
        }

        var config = new ReloadingConfig(configPath, Log);
        var controller = new DisplayController(new DdcDisplayBus(), () => config.Get().Displays, SystemClock.Instance);
        using var cts = new CancellationTokenSource();
        Log($"Display agent started (session {Process.GetCurrentProcess().SessionId}, parent {parentPid?.ToString() ?? "none"}).");

        var poller = new DisplayPoller(controller, () => config.Get().Displays.PollS, Log);
        poller.Start(cts.Token);
        using var presence = new CameraPresence(() => config.Get().Presence, Log);
        using var shooter = new ScreenShooter(() => config.Get().Screenshot, () => controller.Displays, Log);
        var agent = new AgentCommands(controller, presence, shooter);

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
            await ServeAsync(agent, pipeName, Log, cts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pipe name taken: another agent already serves this session, or something else holds the name.
            Log($"Pipe {pipeName} unavailable ({ex.Message}), exiting.");
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

    private static async Task ServeAsync(AgentCommands agent, string pipeName, Action<string> log, CancellationToken ct)
    {
        // One client at a time, but the next instance exists before the last one closes: the name is never free for
        // another process to take, and FirstPipeInstance refuses to start on a name someone already holds.
        var pipe = NewPipe(pipeName, PipeOptions.FirstPipeInstance);
        try
        {
            while (!ct.IsCancellationRequested)
            {
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
                        byte[]? payload = null;
                        try
                        {
                            (reply, payload) = agent.Execute(tokens);
                        }
                        catch (Exception ex)
                        {
                            reply = $"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}";
                        }
                        try
                        {
                            // Not "presence": a line a minute would be a diary of when someone sits at the PC.
                            if (tokens.Length > 0 && tokens[0].ToLowerInvariant() is not ("list" or "presence")) log($"{string.Join(' ', tokens)} -> {reply}");
                            await writer.WriteLineAsync(reply);
                            if (payload != null) await pipe.WriteAsync(payload, ct);
                        }
                        finally
                        {
                            if (payload != null) Array.Clear(payload);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // Client went away mid-conversation.
                }

                var next = NewPipe(pipeName, PipeOptions.None);
                pipe.Dispose();
                pipe = next;
            }
        }
        finally
        {
            pipe.Dispose();
        }
    }

    private static NamedPipeServerStream NewPipe(string pipeName, PipeOptions extra) => NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 2,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | extra, 0, 0, Security());

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

    /// <summary>The service config (displays, presence, screenshot sections), reread when the file changes.</summary>
    private sealed class ReloadingConfig(string path, Action<string> log)
    {
        private readonly object _lock = new();
        private Config _current = new();
        private DateTime _mtime;

        public Config Get()
        {
            lock (_lock) return Reload();
        }

        private Config Reload()
        {
            try
            {
                DateTime mtime = File.GetLastWriteTimeUtc(path);
                if (mtime != _mtime)
                {
                    _mtime = mtime;
                    _current = ConfigLoader.Load(path);
                    log($"Loaded config from {path}: {_current.Displays.Models.Count} display model rule(s).");
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
