namespace PcHwCtl.Core.Control;

/// <summary>What the IPC layer may do to fans. Implemented by FanEngine. Thread-safe.</summary>
public interface IFanControl
{
    IReadOnlyDictionary<string, FanState> States { get; }
    IReadOnlyCollection<string> FanNames { get; }
    bool HasCurve(string curve);
    void SetOverride(string fan, float pct, TimeSpan ttl);
    void ClearOverride(string fan);
    void SetCurveOverride(string fan, string curve);
    void ClearCurveOverride(string fan);

    /// <summary>True when the fan has a zeroRpm config block.</summary>
    bool SupportsZeroRpm(string fan);

    /// <summary>Turns Zero RPM on or off until the next config reload; null goes back to the configured setting.</summary>
    void SetZeroRpm(string fan, bool? enabled);
}
