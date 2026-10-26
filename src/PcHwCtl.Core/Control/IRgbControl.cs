using PcHwCtl.Core.Rgb;

namespace PcHwCtl.Core.Control;

public sealed record RgbGroupState(string Rule, string? OverrideRule, DateTime? OverrideExpiresUtc);

/// <summary>What the IPC layer may do to RGB. Implemented by RgbEngine. Thread-safe.</summary>
public interface IRgbControl
{
    IReadOnlyDictionary<string, RgbGroupState> Groups { get; }

    /// <summary>Device id to "ok" or "faulted: reason".</summary>
    IReadOnlyDictionary<string, string> DeviceStatus { get; }

    /// <summary>0..100.</summary>
    float Brightness { get; set; }

    /// <summary>ttl null = until ClearOverride.</summary>
    void SetOverride(string group, RgbRuleConfig rule, TimeSpan? ttl);

    void ClearOverride(string group);
}
