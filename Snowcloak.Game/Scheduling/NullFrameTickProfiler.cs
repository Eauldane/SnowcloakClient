namespace Snowcloak.Game.Scheduling;

public sealed class NullFrameTickProfiler : IFrameTickProfiler
{
    public static NullFrameTickProfiler Instance { get; } = new();

    public void Run(string counterName, Action action) => action();
}
