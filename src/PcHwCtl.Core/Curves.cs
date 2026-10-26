namespace PcHwCtl.Core;

public interface IFanCurve
{
    float? Evaluate(float? temp, DateTime now);
}

public sealed class GraphCurve : IFanCurve
{
    private enum Direction { None, Up, Down }

    public (float Temp, float Pct)[] Points { get; }
    public float HysteresisUp { get; }
    public float HysteresisDown { get; }
    public float ResponseUpS { get; }
    public float ResponseDownS { get; }
    public bool IgnoreHysteresisAtLimits { get; }

    private float? _appliedTemp;
    private Direction _pendingDirection = Direction.None;
    private DateTime? _conditionStartTime;

    public GraphCurve(
        (float Temp, float Pct)[] points,
        float hysteresisUp,
        float hysteresisDown,
        float responseUpS,
        float responseDownS,
        bool ignoreHysteresisAtLimits)
    {
        if (points == null || points.Length == 0)
        {
            throw new ArgumentException("Points cannot be empty.", nameof(points));
        }

        Points = points;
        HysteresisUp = hysteresisUp;
        HysteresisDown = hysteresisDown;
        ResponseUpS = responseUpS;
        ResponseDownS = responseDownS;
        IgnoreHysteresisAtLimits = ignoreHysteresisAtLimits;
    }

    public float Interpolate(float temp)
    {
        if (temp <= Points[0].Temp) return Points[0].Pct;
        if (temp >= Points[^1].Temp) return Points[^1].Pct;

        for (int i = 0; i < Points.Length - 1; i++)
        {
            if (temp >= Points[i].Temp && temp <= Points[i + 1].Temp)
            {
                float span = Points[i + 1].Temp - Points[i].Temp;
                if (span <= 0) return Points[i].Pct;
                float ratio = (temp - Points[i].Temp) / span;
                return Points[i].Pct + ratio * (Points[i + 1].Pct - Points[i].Pct);
            }
        }

        return Points[^1].Pct;
    }

    public float? Evaluate(float? temp, DateTime now)
    {
        if (!temp.HasValue)
        {
            _conditionStartTime = null;
            _pendingDirection = Direction.None;
            return null;
        }

        float currentTemp = temp.Value;

        if (!_appliedTemp.HasValue)
        {
            _appliedTemp = currentTemp;
            return Interpolate(_appliedTemp.Value);
        }

        float currentApplied = _appliedTemp.Value;

        if (IgnoreHysteresisAtLimits)
        {
            bool atLimit = currentTemp <= Points[0].Temp || currentTemp >= Points[^1].Temp;
            if (atLimit && Interpolate(currentTemp) != Interpolate(currentApplied))
            {
                _appliedTemp = currentTemp;
                _conditionStartTime = null;
                _pendingDirection = Direction.None;
                return Interpolate(_appliedTemp.Value);
            }
        }

        if (currentTemp > currentApplied && (currentTemp - currentApplied) >= HysteresisUp)
        {
            if (_conditionStartTime == null || _pendingDirection != Direction.Up)
            {
                _pendingDirection = Direction.Up;
                _conditionStartTime = now;
            }

            if (now - _conditionStartTime.Value >= TimeSpan.FromSeconds(ResponseUpS))
            {
                _appliedTemp = currentTemp;
                _conditionStartTime = null;
                _pendingDirection = Direction.None;
            }
        }
        else if (currentTemp < currentApplied && (currentApplied - currentTemp) >= HysteresisDown)
        {
            if (_conditionStartTime == null || _pendingDirection != Direction.Down)
            {
                _pendingDirection = Direction.Down;
                _conditionStartTime = now;
            }

            if (now - _conditionStartTime.Value >= TimeSpan.FromSeconds(ResponseDownS))
            {
                _appliedTemp = currentTemp;
                _conditionStartTime = null;
                _pendingDirection = Direction.None;
            }
        }
        else
        {
            _conditionStartTime = null;
            _pendingDirection = Direction.None;
        }

        return Interpolate(_appliedTemp.Value);
    }
}

public sealed class AutoCurve : IFanCurve
{
    public float IdleTemp { get; }
    public float LoadTemp { get; }
    public float MinPct { get; }
    public float MaxPct { get; }
    public float Step { get; }
    public float Deadband { get; }
    public float ResponseS { get; }

    private float? _appliedTemp;
    private DateTime? _conditionStartTime;

    public AutoCurve(
        float idleTemp,
        float loadTemp,
        float minPct,
        float maxPct,
        float step,
        float deadband,
        float responseS)
    {
        IdleTemp = idleTemp;
        LoadTemp = loadTemp;
        MinPct = minPct;
        MaxPct = maxPct;
        Step = step;
        Deadband = deadband;
        ResponseS = responseS;
    }

    public float CalculateTarget(float temp)
    {
        float span = LoadTemp - IdleTemp;
        float ratio = span != 0 ? (temp - IdleTemp) / span : 0f;
        float raw = MinPct + ratio * (MaxPct - MinPct);
        float min = Math.Min(MinPct, MaxPct);
        float max = Math.Max(MinPct, MaxPct);
        float clamped = Math.Clamp(raw, min, max);
        float rounded = Step > 0 ? MathF.Round(clamped / Step, MidpointRounding.AwayFromZero) * Step : clamped;
        return Math.Clamp(rounded, min, max);
    }

    public float? Evaluate(float? temp, DateTime now)
    {
        if (!temp.HasValue)
        {
            _conditionStartTime = null;
            return null;
        }

        float currentTemp = temp.Value;

        if (!_appliedTemp.HasValue)
        {
            _appliedTemp = currentTemp;
            return CalculateTarget(_appliedTemp.Value);
        }

        float currentApplied = _appliedTemp.Value;
        float currentTarget = CalculateTarget(currentApplied);
        float newTarget = CalculateTarget(currentTemp);

        if ((newTarget == MinPct || newTarget == MaxPct) && newTarget != currentTarget)
        {
            _appliedTemp = currentTemp;
            _conditionStartTime = null;
            return newTarget;
        }

        if (Math.Abs(currentTemp - currentApplied) >= Deadband)
        {
            if (!_conditionStartTime.HasValue)
            {
                _conditionStartTime = now;
            }

            if (now - _conditionStartTime.Value >= TimeSpan.FromSeconds(ResponseS))
            {
                _appliedTemp = currentTemp;
                _conditionStartTime = null;
            }
        }
        else
        {
            _conditionStartTime = null;
        }

        return CalculateTarget(_appliedTemp.Value);
    }
}

public sealed class FlatCurve : IFanCurve
{
    public float Pct { get; }

    public FlatCurve(float pct)
    {
        Pct = pct;
    }

    public float? Evaluate(float? temp, DateTime now) => Pct;
}
