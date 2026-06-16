using Microsoft.Extensions.Logging;

namespace Snowcloak.Utils;

public static class BackgroundTaskTrackerExtensions
{
    private static readonly Action<ILogger, string, Exception?> LogSynchronousStopTimedOut = LoggerMessage.Define<string>(
        LogLevel.Debug,
        new EventId(1, nameof(StopSynchronously)),
        "Timed out waiting for {OperationName} background tasks during synchronous disposal");

    public static void StopSynchronously(this BackgroundTaskTracker tracker, ILogger logger, TimeSpan timeout, string operationName)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(operationName);

        StopSynchronouslyCore(tracker, logger, timeout, operationName, []);
    }

    public static void StopSynchronously(this BackgroundTaskTracker tracker, ILogger logger, TimeSpan timeout, string operationName, params Task?[] additionalTasks)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(operationName);
        ArgumentNullException.ThrowIfNull(additionalTasks);

        StopSynchronouslyCore(tracker, logger, timeout, operationName, additionalTasks);
    }

    private static void StopSynchronouslyCore(BackgroundTaskTracker tracker, ILogger logger, TimeSpan timeout, string operationName, ReadOnlySpan<Task?> additionalTasks)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            Task? activeTask = null;
            List<Task>? activeTasks = null;

            foreach (var task in additionalTasks)
            {
                if (task is not { IsCompleted: false })
                {
                    continue;
                }

                if (activeTask is null)
                {
                    activeTask = task;
                    continue;
                }

                activeTasks ??= [activeTask];
                activeTasks.Add(task);
            }

            if (activeTasks is not null)
            {
                Task.WhenAll(activeTasks).WaitAsync(timeoutCts.Token).GetAwaiter().GetResult();
            }
            else if (activeTask is not null)
            {
                activeTask.WaitAsync(timeoutCts.Token).GetAwaiter().GetResult();
            }

            tracker.StopAsync(timeoutCts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            LogSynchronousStopTimedOut(logger, operationName, ex);
        }
    }
}
