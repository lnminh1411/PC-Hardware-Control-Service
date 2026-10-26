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

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}
