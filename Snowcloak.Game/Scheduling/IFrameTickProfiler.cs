namespace Snowcloak.Game.Scheduling;

public interface IFrameTickProfiler
{
    void Run(string counterName, Action action);
    void RecordAllocation(string counterName, long allocatedBytes);
}
