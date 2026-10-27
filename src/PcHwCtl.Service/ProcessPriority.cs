using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcHwCtl.Service;

/// <summary>
/// The fans need their 1 s tick even when the CPU is slow or crowded (the night plan runs at ~450 MHz, and parked
/// cores leave few to share): High priority class, and opted out of Windows efficiency mode so nothing (Windows, a
/// night `eco on`) throttles it. Heavy work inside drops to below normal per thread (RGB, background CPU sensor read).
/// </summary>
public static class ProcessPriority
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 1;

    public static void Apply(Action<string> log)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.High;

            // Control bit set, state bit clear: never EcoQoS, whatever Windows would decide.
            var state = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = 0 };
            bool eco = SetProcessInformation(self.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
            log($"Process priority {self.PriorityClass}, efficiency mode {(eco ? "opted out" : $"opt-out failed ({Marshal.GetLastWin32Error()})")}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"Process priority not raised: {ex.Message}");
        }
    }
}
