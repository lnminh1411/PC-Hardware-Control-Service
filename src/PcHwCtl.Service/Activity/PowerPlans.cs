using System.Runtime.InteropServices;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>Power plans through powrprof (no powercfg process). The active plan reaches the feed as power_plan.</summary>
public sealed class PowerPlans : IPowerPlans, IFeedSource
{
    private const uint AccessScheme = 16;
    private const uint ErrorNoMoreItems = 259;

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid schemeGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid, uint accessFlags, uint index, IntPtr buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr settingGuid, IntPtr buffer, ref uint bufferSize);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    private readonly Action<string> _log;

    public PowerPlans(Action<string> log)
    {
        _log = log;
    }

    public PowerPlan? Active
    {
        get
        {
            Guid? active = ActiveGuid();
            if (active == null) return null;
            return List().FirstOrDefault(p => p.Guid == active) ?? new PowerPlan(active.Value, active.Value.ToString(), true);
        }
    }

    public IReadOnlyList<PowerPlan> List()
    {
        Guid? active = ActiveGuid();
        var plans = new List<PowerPlan>();
        IntPtr buf = Marshal.AllocHGlobal(16);
        try
        {
            for (uint i = 0; ; i++)
            {
                uint size = 16;
                uint r = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buf, ref size);
                if (r == ErrorNoMoreItems) break;
                if (r != 0) throw new InvalidOperationException($"PowerEnumerate error {r}");
                var guid = Marshal.PtrToStructure<Guid>(buf);
                plans.Add(new PowerPlan(guid, FriendlyName(guid), guid == active));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return plans;
    }

    public PowerPlan Set(string nameOrGuid)
    {
        var plan = PowerPlanMatch.Find(List(), nameOrGuid) ?? throw new ArgumentException($"No power plan '{nameOrGuid}'.");
        Guid g = plan.Guid;
        uint r = PowerSetActiveScheme(IntPtr.Zero, ref g);
        if (r != 0) throw new InvalidOperationException($"PowerSetActiveScheme error {r}");
        _log($"Power plan: {plan.Name} ({plan.Guid}) active.");
        return plan with { Active = true };
    }

    public void AddTo(List<KeyValuePair<string, double?>> values, List<KeyValuePair<string, string>> texts)
    {
        string name;
        try
        {
            name = Active?.Name ?? "";
        }
        catch (InvalidOperationException)
        {
            name = "";
        }
        texts.Add(new("power_plan", name));
    }

    private static Guid? ActiveGuid()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0) return null;
        try
        {
            return Marshal.PtrToStructure<Guid>(p);
        }
        finally
        {
            LocalFree(p);
        }
    }

    private static string FriendlyName(Guid scheme)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0) return scheme.ToString();
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return scheme.ToString();
            return Marshal.PtrToStringUni(buf) ?? scheme.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
