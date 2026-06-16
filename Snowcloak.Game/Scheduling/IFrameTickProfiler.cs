namespace Snowcloak.Game.Scheduling;

public interface IFrameTickProfiler
{
    void Run(string counterName, Action action);
}
