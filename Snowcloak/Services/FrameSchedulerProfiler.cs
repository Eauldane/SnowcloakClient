using Snowcloak.Game.Scheduling;

namespace Snowcloak.Services;

public sealed class FrameSchedulerProfiler : IFrameTickProfiler
{
    private readonly PerformanceCollectorService _performanceCollector;

    public FrameSchedulerProfiler(PerformanceCollectorService performanceCollector)
    {
        _performanceCollector = performanceCollector;
    }

    public void Run(string counterName, Action action)
        => _performanceCollector.LogPerformance(this, $"{counterName}", action);

    public void RecordAllocation(string counterName, long allocatedBytes)
        => _performanceCollector.RecordAllocation($"FrameScheduler=>{counterName}", allocatedBytes);
}
