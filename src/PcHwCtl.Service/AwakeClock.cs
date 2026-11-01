using System.Runtime.InteropServices;

namespace PcHwCtl.Service;

/// <summary>
/// Milliseconds the PC has been awake: QueryUnbiasedInterruptTime stops while the PC sleeps or hibernates, unlike
/// DateTime.UtcNow, Environment.TickCount64 and Stopwatch. Timeouts that mean "the service is stuck" use this, so
/// hours asleep don't look like a hung loop.
/// </summary>
public static class AwakeClock
{
    [DllImport("kernel32.dll")] private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    public static long Ms => QueryUnbiasedInterruptTime(out ulong t) ? (long)(t / 10_000) : Environment.TickCount64;
}
