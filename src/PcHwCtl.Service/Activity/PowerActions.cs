using System.ComponentModel;
using System.Runtime.InteropServices;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>
/// Power actions as SYSTEM: SetSuspendState for sleep/hibernate, InitiateShutdown with SeShutdownPrivilege for
/// restart/shutdown, and lock through rundll32 user32.dll,LockWorkStation started in the console session with the
/// signed-in user's token (session 0 can't lock a user's desktop). Every request ends in PowerWatcher as accepted,
/// declined (with why, before anything starts) or failed (Windows refused after the reply, e.g. a sleep).
/// </summary>
public sealed class PowerActions : IPowerActions
{
    private const uint ShutdownForceOthers = 0x1, ShutdownRestart = 0x4, ShutdownPowerOff = 0x8;
    private const uint ReasonPlanned = 0x80000000;   // SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_FLAG_PLANNED
    private const uint CreateNoWindow = 0x08000000;
    private const int SuspendDelayMs = 300;          // the pipe reply goes out first
    private const string Comment = "Requested by the ESP32 board (PcHwCtl power do)";   // event 1074's comment

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool GetPwrCapabilities(IntPtr caps);
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint InitiateShutdown(string? machine, string? message, uint gracePeriod, uint flags, uint reason);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string app, string cmdLine, IntPtr procAttr, IntPtr threadAttr,
        bool inherit, uint flags, IntPtr env, string? dir, ref StartupInfo si, out ProcessInformation pi);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    private readonly PowerWatcher _watcher;

    public PowerActions(PowerWatcher watcher)
    {
        _watcher = watcher;
    }

    public PowerCaps Caps()
    {
        IntPtr buf = Marshal.AllocHGlobal(128);
        try
        {
            for (int i = 0; i < 128; i++) Marshal.WriteByte(buf, i, 0);
            if (!GetPwrCapabilities(buf)) throw new InvalidOperationException($"GetPwrCapabilities: {Win32Message()}");
            // SYSTEM_POWER_CAPABILITIES: 1-byte flags; SystemS1..S3 at 3..5, HiberFilePresent at 8, AoAc (modern standby) at 20.
            bool sleep = Marshal.ReadByte(buf, 3) != 0 || Marshal.ReadByte(buf, 4) != 0 || Marshal.ReadByte(buf, 5) != 0 || Marshal.ReadByte(buf, 20) != 0;
            return new PowerCaps(sleep, Marshal.ReadByte(buf, 8) != 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public void Do(PowerAction action, int delayMs = 0)
    {
        try
        {
            Decline(action);
            if (delayMs == 0 && action is not (PowerAction.Sleep or PowerAction.Hibernate)) Start(action);
        }
        catch (InvalidOperationException ex)
        {
            _watcher.Done(action, "declined", ex.Message);
            throw;
        }
        _watcher.Done(action, "accepted", "-");
        if (delayMs > 0 || action is PowerAction.Sleep or PowerAction.Hibernate)
        {
            // Later, after the reply (and the caller's ack) went out; Windows refusing then shows as failed.
            var thread = new Thread(() =>
            {
                Thread.Sleep(Math.Max(delayMs, SuspendDelayMs));
                try
                {
                    Start(action);
                }
                catch (InvalidOperationException ex)
                {
                    _watcher.Done(action, "failed", ex.Message);
                }
            }) { IsBackground = true, Name = "PcHwCtl-PowerDo" };
            thread.Start();
        }
    }

    /// <summary>Throws with the reason when the action can't or shouldn't start now.</summary>
    private void Decline(PowerAction action)
    {
        if (action == PowerAction.Lock)
        {
            ConsoleToken().Dispose();
            return;
        }
        var status = _watcher.Status;
        string going = status.Going.Kind;
        if (going.EndsWith("shutdown", StringComparison.Ordinal) || going.EndsWith("restart", StringComparison.Ordinal))
            throw new InvalidOperationException($"A {going.Replace('_', ' ')} is already under way (by {status.Going.By}).");
        if (action is PowerAction.Sleep or PowerAction.Hibernate)
        {
            if (going is "sleep" or "hibernate") throw new InvalidOperationException($"Already going to {going}.");
            if (status.Update.Running) throw new InvalidOperationException("Windows is installing updates.");
            var caps = Caps();
            if (action == PowerAction.Sleep && !caps.Sleep) throw new InvalidOperationException("This PC can't sleep.");
            if (action == PowerAction.Hibernate && !caps.Hibernate) throw new InvalidOperationException("Hibernation is off.");
        }
    }

    private void Start(PowerAction action)
    {
        switch (action)
        {
            case PowerAction.Lock:
                LockConsole();
                break;
            case PowerAction.Sleep:
            case PowerAction.Hibernate:
                // Returns after the wake; false = Windows refused (a driver vetoed, or sleep is blocked).
                if (!SetSuspendState(action == PowerAction.Hibernate, false, false)) throw new InvalidOperationException($"SetSuspendState: {Win32Message()}");
                break;
            case PowerAction.Restart:
            case PowerAction.Shutdown:
                EnableShutdownPrivilege();
                uint flags = ShutdownForceOthers | (action == PowerAction.Restart ? ShutdownRestart : ShutdownPowerOff);
                uint r = InitiateShutdown(null, Comment, 0, flags, ReasonPlanned);
                if (r != 0) throw new InvalidOperationException($"InitiateShutdown: {new Win32Exception((int)r).Message} ({r})");
                break;
            default:
                throw new InvalidOperationException($"Unknown power action {action}.");
        }
    }

    private static void EnableShutdownPrivilege()
    {
        // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out IntPtr token)) throw new InvalidOperationException($"OpenProcessToken: {Win32Message()}");
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out Luid luid)) throw new InvalidOperationException($"LookupPrivilegeValue: {Win32Message()}");
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 };   // SE_PRIVILEGE_ENABLED
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException($"AdjustTokenPrivileges: {Win32Message()}");
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private sealed class Token(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public void Dispose() => CloseHandle(Handle);
    }

    private static Token ConsoleToken()
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF || !WTSQueryUserToken(session, out IntPtr token))
            throw new InvalidOperationException($"No user signed in at the console ({Win32Message()}).");
        return new Token(token);
    }

    private static void LockConsole()
    {
        using var token = ConsoleToken();
        var si = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
        string exe = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
        if (!CreateProcessAsUser(token.Handle, exe, $"\"{exe}\" user32.dll,LockWorkStation", IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow,
                IntPtr.Zero, null, ref si, out var pi))
            throw new InvalidOperationException($"CreateProcessAsUser: {Win32Message()}");
        CloseHandle(pi.Thread);
        CloseHandle(pi.Process);
    }

    private static string Win32Message()
    {
        int e = Marshal.GetLastWin32Error();
        return $"{new Win32Exception(e).Message} ({e})";
    }
}
