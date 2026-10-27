namespace PcHwCtl.Core.Activity;

/// <summary>
/// Windows power plans for the board's night mode: "night" is a plan with the CPU capped at 5 % (~450 MHz), core
/// parking (10 of 20 cores awake) and PCIe ASPM at maximum savings; "day" goes back to the normal plan. Plans are
/// named or given by GUID. Windows only applies a processor cap on a plan switch, so the caps live in their own
/// plan instead of being edited into the active one.
/// </summary>
public sealed class PowerConfig
{
    public string NightPlan { get; set; } = "PcHwCtl Night";
    public string DayPlan { get; set; } = "High performance";
}

public sealed record PowerPlan(Guid Guid, string Name, bool Active);

public interface IPowerPlans
{
    IReadOnlyList<PowerPlan> List();

    /// <summary>The active plan; null when it cannot be read.</summary>
    PowerPlan? Active { get; }

    /// <summary>Activates the plan with this name (case-insensitive) or GUID.</summary>
    /// <exception cref="ArgumentException">No such plan.</exception>
    PowerPlan Set(string nameOrGuid);
}

public static class PowerPlanMatch
{
    /// <summary>GUID first, then exact name, case-insensitive; null when nothing or more than one name matches.</summary>
    public static PowerPlan? Find(IEnumerable<PowerPlan> plans, string nameOrGuid)
    {
        var list = plans.ToList();
        if (Guid.TryParse(nameOrGuid, out var g)) return list.FirstOrDefault(p => p.Guid == g);
        var named = list.Where(p => p.Name.Equals(nameOrGuid.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count == 1 ? named[0] : null;
    }
}
