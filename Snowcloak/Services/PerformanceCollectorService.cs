using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Utils;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Snowcloak.Services;

public sealed partial class PerformanceCollectorService : IHostedService, IDisposable
{
    private const string _counterSplit = "=>";
    private readonly ILogger<PerformanceCollectorService> _logger;
    private readonly SnowcloakConfigService _snowcloakConfigService;
    public ConcurrentDictionary<string, RollingList<(TimeOnly, long)>> PerformanceCounters { get; } = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, RollingList<(TimeOnly, long)>> AllocationCounters { get; } = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _periodicLogPruneTaskCts = new();
    private Task? _periodicLogPruneTask;

    public PerformanceCollectorService(ILogger<PerformanceCollectorService> logger, SnowcloakConfigService snowcloakConfigService)
    {
        _logger = logger;
        _snowcloakConfigService = snowcloakConfigService;
    }

    public T LogPerformance<T>(object sender, InterpolatedStringHandler counterName, Func<T> func, int maxEntries = 10000)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(func);

        if (!_snowcloakConfigService.Current.LogPerformance) return func.Invoke();

        string cn = sender.GetType().Name + _counterSplit + counterName.BuildMessage();

        if (!PerformanceCounters.TryGetValue(cn, out var list))
        {
            list = PerformanceCounters[cn] = new(maxEntries);
        }

        var dt = DateTime.UtcNow.Ticks;
        try
        {
            return func.Invoke();
        }
        finally
        {
            var elapsed = DateTime.UtcNow.Ticks - dt;
#if DEBUG
            if (TimeSpan.FromTicks(elapsed) > TimeSpan.FromMilliseconds(10))
                _logger.LogWarning(">10ms spike on {counterName}: {time}", cn, TimeSpan.FromTicks(elapsed));
#endif
            list.Add((TimeOnly.FromDateTime(DateTime.Now), elapsed));
        }
    }

    public void LogPerformance(object sender, InterpolatedStringHandler counterName, Action act, int maxEntries = 10000)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(act);

        if (!_snowcloakConfigService.Current.LogPerformance) { act.Invoke(); return; }

        var cn = sender.GetType().Name + _counterSplit + counterName.BuildMessage();

        if (!PerformanceCounters.TryGetValue(cn, out var list))
        {
            list = PerformanceCounters[cn] = new(maxEntries);
        }

        var dt = DateTime.UtcNow.Ticks;
        try
        {
            act.Invoke();
        }
        finally
        {
            var elapsed = DateTime.UtcNow.Ticks - dt;
#if DEBUG
            if (TimeSpan.FromTicks(elapsed) > TimeSpan.FromMilliseconds(10))
                _logger.LogWarning(">10ms spike on {counterName}: {time}", cn, TimeSpan.FromTicks(elapsed));
#endif
            list.Add(new(TimeOnly.FromDateTime(DateTime.Now), elapsed));
        }
    }

    public void RecordTimeToFirstModdedRender(TimeSpan elapsed)
    {
        RecordCounter("F5=>TimeToFirstModdedRender", elapsed.Ticks);
    }

    internal void RecordAllocation(string counterName, long allocatedBytes, int maxEntries = 10000)
    {
        if (!_snowcloakConfigService.Current.LogPerformance)
        {
            return;
        }

        if (!AllocationCounters.TryGetValue(counterName, out var list))
        {
            list = AllocationCounters[counterName] = new(maxEntries);
        }

        list.Add((TimeOnly.FromDateTime(DateTime.Now), allocatedBytes));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogStartingPerformanceCollectorService(_logger);
        _periodicLogPruneTask = Task.Run(PeriodicLogPrune, _periodicLogPruneTaskCts.Token);
        LogStartedPerformanceCollectorService(_logger);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _periodicLogPruneTaskCts.CancelAsync().ConfigureAwait(false);

        if (_periodicLogPruneTask != null)
        {
            try
            {
                await _periodicLogPruneTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_periodicLogPruneTaskCts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                LogPerformanceCollectorStopCancelled(_logger);
            }
        }
    }

    public void Dispose()
    {
        _periodicLogPruneTaskCts.Dispose();
    }

    internal void PrintPerformanceStats(int limitBySeconds = 0)
    {
        if (!_snowcloakConfigService.Current.LogPerformance)
        {
            LogPerformanceCountersDisabled(_logger);
        }

        var snapshots = CreateSnapshots(limitBySeconds).ToList();
        var allocationSnapshots = CreateAllocationSnapshots(limitBySeconds).ToList();
        if (snapshots.Count == 0 && allocationSnapshots.Count == 0)
        {
            LogNoPerformanceCountersRecorded(_logger);
            return;
        }

        var width = Math.Max("Counter".Length, snapshots.Count == 0 ? 0 : snapshots.Max(snapshot => snapshot.Name.Length));
        var sb = new StringBuilder();
        sb.AppendLine(limitBySeconds > 0
            ? string.Format(CultureInfo.InvariantCulture, "Performance metrics over the past {0} seconds", limitBySeconds)
            : "Performance metrics over total lifetime");
        sb.AppendLine("Timing counters (milliseconds)");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "{0,-12} {1,-12} {2,-12} {3,-12} {4,-12} {5,-16} {6,-8} {7}",
            "Last", "Max", "Average", "P95", "P99", "Last Update", "Entries", "Counter".PadRight(width)));

        foreach (var snapshot in snapshots)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-12:0.00000} {1,-12:0.00000} {2,-12:0.00000} {3,-12:0.00000} {4,-12:0.00000} {5,-16} {6,-8} {7}",
                snapshot.LastMs,
                snapshot.MaxMs,
                snapshot.AverageMs,
                snapshot.P95Ms,
                snapshot.P99Ms,
                snapshot.LastUpdate,
                snapshot.Entries,
                snapshot.Name));
        }

        if (allocationSnapshots.Count > 0)
        {
            width = Math.Max("Counter".Length, allocationSnapshots.Max(snapshot => snapshot.Name.Length));
            sb.AppendLine();
            sb.AppendLine("Framework-thread allocation counters (bytes)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-12} {1,-12} {2,-12} {3,-12} {4,-12} {5,-16} {6,-8} {7}",
                "Last", "Max", "Average", "P95", "P99", "Last Update", "Entries", "Counter".PadRight(width)));
            foreach (var snapshot in allocationSnapshots)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-12} {1,-12} {2,-12:0.0} {3,-12} {4,-12} {5,-16} {6,-8} {7}",
                    snapshot.LastBytes,
                    snapshot.MaxBytes,
                    snapshot.AverageBytes,
                    snapshot.P95Bytes,
                    snapshot.P99Bytes,
                    snapshot.LastUpdate,
                    snapshot.Entries,
                    snapshot.Name));
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var performanceStats = sb.ToString();
            LogPerformanceStats(_logger, performanceStats);
        }
    }

    private void RecordCounter(string counterName, long elapsedTicks, int maxEntries = 10000)
    {
        if (!PerformanceCounters.TryGetValue(counterName, out var list))
        {
            list = PerformanceCounters[counterName] = new(maxEntries);
        }

        list.Add((TimeOnly.FromDateTime(DateTime.Now), elapsedTicks));
    }

    private IEnumerable<PerformanceCounterSnapshot> CreateSnapshots(int limitBySeconds)
    {
        foreach (var entry in PerformanceCounters.OrderBy(counter => counter.Key, StringComparer.OrdinalIgnoreCase))
        {
            var values = limitBySeconds > 0
                ? entry.Value.Where(value => value.Item1.AddMinutes(limitBySeconds / 60.0d) >= TimeOnly.FromDateTime(DateTime.Now)).ToList()
                : [.. entry.Value];
            if (values.Count == 0)
            {
                continue;
            }

            yield return new PerformanceCounterSnapshot(
                entry.Key,
                TimeSpan.FromTicks(values[^1].Item2).TotalMilliseconds,
                TimeSpan.FromTicks(values.Max(value => value.Item2)).TotalMilliseconds,
                TimeSpan.FromTicks((long)values.Average(value => value.Item2)).TotalMilliseconds,
                TimeSpan.FromTicks(Percentile(values.Select(value => value.Item2), 0.95)).TotalMilliseconds,
                TimeSpan.FromTicks(Percentile(values.Select(value => value.Item2), 0.99)).TotalMilliseconds,
                values[^1].Item1.ToString("HH:mm:ss.ffff", CultureInfo.InvariantCulture),
                values.Count);
        }
    }

    private IEnumerable<AllocationCounterSnapshot> CreateAllocationSnapshots(int limitBySeconds)
    {
        foreach (var entry in AllocationCounters.OrderBy(counter => counter.Key, StringComparer.OrdinalIgnoreCase))
        {
            var values = limitBySeconds > 0
                ? entry.Value.Where(value => value.Item1.AddMinutes(limitBySeconds / 60.0d) >= TimeOnly.FromDateTime(DateTime.Now)).ToList()
                : [.. entry.Value];
            if (values.Count == 0)
            {
                continue;
            }

            yield return new AllocationCounterSnapshot(
                entry.Key,
                values[^1].Item2,
                values.Max(value => value.Item2),
                values.Average(value => value.Item2),
                Percentile(values.Select(value => value.Item2), 0.95),
                Percentile(values.Select(value => value.Item2), 0.99),
                values[^1].Item1.ToString("HH:mm:ss.ffff", CultureInfo.InvariantCulture),
                values.Count);
        }
    }

    private static long Percentile(IEnumerable<long> source, double percentile)
    {
        var ordered = source.Order().ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private async Task PeriodicLogPrune()
    {
        while (!_periodicLogPruneTaskCts.Token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(10), _periodicLogPruneTaskCts.Token).ConfigureAwait(false);

            foreach (var entries in PerformanceCounters.ToList())
            {
                try
                {
                    var last = entries.Value.ToList()[^1];
                    if (last.Item1.AddMinutes(10) < TimeOnly.FromDateTime(DateTime.Now) && !PerformanceCounters.TryRemove(entries.Key, out _))
                    {
                        LogCouldNotRemovePerformanceCounter(_logger, entries.Key);
                    }
                }
                catch (InvalidOperationException e)
                {
                    LogErrorRemovingPerformanceCounter(_logger, e, entries.Key);
                }
                catch (ArgumentOutOfRangeException e)
                {
                    LogErrorRemovingPerformanceCounter(_logger, e, entries.Key);
                }
            }
        }
    }

    private sealed record PerformanceCounterSnapshot(
        string Name,
        double LastMs,
        double MaxMs,
        double AverageMs,
        double P95Ms,
        double P99Ms,
        string LastUpdate,
        int Entries);

    private sealed record AllocationCounterSnapshot(
        string Name,
        long LastBytes,
        long MaxBytes,
        double AverageBytes,
        long P95Bytes,
        long P99Bytes,
        string LastUpdate,
        int Entries);

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Could not remove performance counter {Counter}")]
    private static partial void LogCouldNotRemovePerformanceCounter(ILogger logger, string counter);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Error removing performance counter {Counter}")]
    private static partial void LogErrorRemovingPerformanceCounter(ILogger logger, Exception exception, string counter);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Starting PerformanceCollectorService")]
    private static partial void LogStartingPerformanceCollectorService(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Started PerformanceCollectorService")]
    private static partial void LogStartedPerformanceCollectorService(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Performance counters are disabled")]
    private static partial void LogPerformanceCountersDisabled(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "No performance counters recorded")]
    private static partial void LogNoPerformanceCountersRecorded(ILogger logger);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "{PerformanceStats}")]
    private static partial void LogPerformanceStats(ILogger logger, string performanceStats);

    [LoggerMessage(EventId = 7, Level = LogLevel.Trace, Message = "PerformanceCollectorService stop was cancelled")]
    private static partial void LogPerformanceCollectorStopCancelled(ILogger logger);
}
