using Microsoft.Extensions.Logging;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using System.Collections.Concurrent;

namespace Snowcloak.Services.Performance;

public sealed partial class DeferredApplicationRetryCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<int, Action> _callbacks = [];
    private readonly ILogger<DeferredApplicationRetryCoordinator> _logger;
    private readonly IFrameTickHandle _tick;
    private int _nextId;
    private int _disposed;

    public DeferredApplicationRetryCoordinator(ILogger<DeferredApplicationRetryCoordinator> logger, IFrameScheduler frameScheduler)
    {
        _logger = logger;
        _tick = frameScheduler.Register("PairHandlerRetry", TickInterval.EveryMilliseconds(100), TickPriority.Normal, RetryDeferredApplications,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
    }

    public IDisposable Register(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var id = Interlocked.Increment(ref _nextId);
        _callbacks[id] = callback;
        return new Registration(this, id);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _tick.Dispose();
        _callbacks.Clear();
    }

    private void RetryDeferredApplications()
    {
        foreach (var (id, callback) in _callbacks)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                LogRetryFailure(_logger, ex, id);
            }
        }
    }

    private void Unregister(int id) => _callbacks.TryRemove(id, out _);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deferred pair application retry {registrationId} failed")]
    private static partial void LogRetryFailure(ILogger logger, Exception exception, int registrationId);

    private sealed class Registration(DeferredApplicationRetryCoordinator owner, int id) : IDisposable
    {
        private DeferredApplicationRetryCoordinator? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Unregister(id);
        }
    }
}
