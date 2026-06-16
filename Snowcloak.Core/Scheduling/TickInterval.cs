namespace Snowcloak.Core.Scheduling;

public readonly struct TickInterval : IEquatable<TickInterval>
{
    public const long NeverRanFrame = -1;
    public const double NeverRanMs = double.NaN;

    private enum Mode : byte
    {
        EveryFrame,
        EveryNthFrame,
        EveryMilliseconds,
    }

    private readonly Mode _mode;
    private readonly int _frames;
    private readonly double _milliseconds;

    private TickInterval(Mode mode, int frames, double milliseconds)
    {
        _mode = mode;
        _frames = frames;
        _milliseconds = milliseconds;
    }

    public static TickInterval EveryFrame => new(Mode.EveryFrame, 1, 0);

    public static TickInterval EveryNthFrame(int frames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        return new(Mode.EveryNthFrame, frames, 0);
    }

    public static TickInterval EveryMilliseconds(double milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        return new(Mode.EveryMilliseconds, 1, milliseconds);
    }

    public bool IsDue(long frame, double nowMs, long lastRanFrame, double lastRanMs)
    {
        return _mode switch
        {
            Mode.EveryFrame => true,
            Mode.EveryNthFrame => lastRanFrame < 0 || frame - lastRanFrame >= _frames,
            Mode.EveryMilliseconds => double.IsNaN(lastRanMs) || nowMs - lastRanMs >= _milliseconds,
            _ => true,
        };
    }

    public bool Equals(TickInterval other)
        => _mode == other._mode && _frames == other._frames && _milliseconds.Equals(other._milliseconds);

    public override bool Equals(object? obj) => obj is TickInterval other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_mode, _frames, _milliseconds);

    public static bool operator ==(TickInterval left, TickInterval right) => left.Equals(right);

    public static bool operator !=(TickInterval left, TickInterval right) => !left.Equals(right);
}
