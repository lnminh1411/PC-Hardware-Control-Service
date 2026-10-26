namespace PcHwCtl.Core;

public interface IClock
{
    DateTime UtcNow { get; }
}

public interface ISensorSource
{
    bool TryRead(string reference, out float value); // false = missing/NaN
}

public interface IFanOutput
{
    void Set(string controlId, float percent);
    void SetDefault(string controlId);
}

/// <summary>
/// A fan curve that runs in the fan's own firmware (AMD Overdrive8): the engine hands over the whole curve and the
/// Zero RPM flag when they change, instead of a % every tick.
/// </summary>
public sealed record NativeFanCurve(IReadOnlyList<(float TempC, float Pct)> Points, bool ZeroRpm)
{
    public bool SameAs(NativeFanCurve? other) =>
        other != null && other.ZeroRpm == ZeroRpm && other.Points.SequenceEqual(Points);
}

public interface INativeFanOutput
{
    void Apply(string controlId, NativeFanCurve curve);

    /// <summary>Back to the driver's default curve and Zero RPM setting.</summary>
    void Reset(string controlId);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}
