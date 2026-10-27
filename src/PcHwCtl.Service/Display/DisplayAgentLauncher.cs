using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PcHwCtl.Service.Display;

/// <summary>
/// Keeps one display agent running in the active console session: starts it with the logged-on user's token
/// (CreateProcessAsUser, which needs the service's LocalSystem rights), again after a crash or a new logon,
/// and kills it on shutdown. Checks every 10 s.
/// </summary>
public sealed class DisplayAgentLauncher : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint NoSession = 0xFFFFFFFF;

    private readonly string _exePath;
    private readonly string _configPath;
    private readonly AgentDisplayClient _client;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private Process? _agent;
    private uint _agentSession = NoSession;
    private string? _lastError;

    public DisplayAgentLauncher(string exePath, string configPath, AgentDisplayClient client, Action<string> log)
    {
        _exePath = exePath;
        _configPath = configPath;
        _client = client;
        _log = log;
    }

    public void Start()
    {
        var thread = new Thread(() =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    EnsureAgent();
                }
                catch (Exception ex)
                {
                    if (_lastError != ex.Message) _log($"Display agent launch failed: {ex.Message}");
                    _lastError = ex.Message;
                }
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            }
        })
        {
            IsBackground = true,
            Name = "PcHwCtl-DisplayAgentLauncher"
        };
        thread.Start();
    }

    private void EnsureAgent()
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == NoSession || session == 0) return; // nobody at the console

        bool ours = _agent is { HasExited: false } && _agentSession == session;
        if (ours) return;
        if (_agent is { HasExited: false }) StopAgent("console session changed");
        if (_client.AgentAnswers()) return; // one already running (e.g. left by a service restart; it exits with its parent)

        Launch(session);
    }

    private void Launch(uint session)
    {
        if (!WTSQueryUserToken(session, out IntPtr userToken))
        {
            throw new InvalidOperationException($"no user token for session {session} (error {Marshal.GetLastWin32Error()}), nobody logged on?");
        }

        IntPtr primary = IntPtr.Zero, environment = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
            {
                throw new InvalidOperationException($"DuplicateTokenEx failed ({Marshal.GetLastWin32Error()})");
            }
            CreateEnvironmentBlock(out environment, primary, false);

            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var commandLine = new StringBuilder($"\"{_exePath}\" display-agent --config \"{_configPath}\" --parent {Environment.ProcessId}");
            if (!CreateProcessAsUser(primary, _exePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, environment, Path.GetDirectoryName(_exePath), ref startup, out var info))
            {
                throw new InvalidOperationException($"CreateProcessAsUser failed ({Marshal.GetLastWin32Error()})");
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            _agent = Process.GetProcessById(info.dwProcessId);
            _agentSession = session;
            _lastError = null;
            _log($"Display agent started in session {session} (pid {info.dwProcessId}).");
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            CloseHandle(userToken);
        }
    }

    private void StopAgent(string why)
    {
        if (_agent == null) return;
        try
        {
            if (!_agent.HasExited)
            {
                _agent.Kill();
                _log($"Display agent (pid {_agent.Id}) stopped: {why}.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        _agent.Dispose();
        _agent = null;
        _agentSession = NoSession;
    }

    public void Dispose()
    {
        _cts.Cancel();
        StopAgent("service stopping");
    }
}
