using Microsoft.Extensions.Logging;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.Services;
using System.Collections.Concurrent;

namespace Snowcloak.PlayerData.Handlers;

public sealed partial class GameObjectHandlerMonitor : IDisposable
{
    private readonly ConcurrentDictionary<GameObjectHandler, byte> _activeHandlers = [];
    private readonly ConcurrentDictionary<GameObjectHandler, byte> _handlers = [];
    private readonly ILogger<GameObjectHandlerMonitor> _logger;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly IFrameTickHandle _tick;
    private int _disposed;

    public GameObjectHandlerMonitor(
        ILogger<GameObjectHandlerMonitor> logger,
        PerformanceCollectorService performanceCollector,
        IFrameScheduler frameScheduler)
    {
        ArgumentNullException.ThrowIfNull(frameScheduler);

        _logger = logger;
        _performanceCollector = performanceCollector;
        _tick = frameScheduler.Register("GameObjects", TickInterval.EveryFrame, TickPriority.High, FrameworkUpdate,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
    }

    public void Register(GameObjectHandler handler, bool active = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _handlers.TryAdd(handler, 0);
        if (active)
        {
            _activeHandlers.TryAdd(handler, 0);
        }
    }

    public void SetActive(GameObjectHandler handler, bool active)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_handlers.ContainsKey(handler))
        {
            return;
        }

        if (active)
        {
            _activeHandlers.TryAdd(handler, 0);
        }
        else
        {
            _activeHandlers.TryRemove(handler, out _);
        }
    }

    public void Unregister(GameObjectHandler handler)
    {
        _activeHandlers.TryRemove(handler, out _);
        _handlers.TryRemove(handler, out _);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _tick.Dispose();
        _activeHandlers.Clear();
        _handlers.Clear();
    }

    private void FrameworkUpdate()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        foreach (var handler in _activeHandlers.Keys)
        {
            if (!handler.ShouldProcessFrameworkUpdate)
            {
                continue;
            }

            try
            {
                _performanceCollector.LogPerformance(handler, $"{handler.PerformanceCounterName}", handler.RefreshFromFramework);
            }
            catch (Exception ex)
            {
                LogFrameworkUpdateFailure(_logger, ex, handler);
            }
        }
    }

    [LoggerMessage(EventId = 4050, Level = LogLevel.Warning, Message = "Error during framework update of {Handler}")]
    private static partial void LogFrameworkUpdateFailure(ILogger logger, Exception exception, GameObjectHandler handler);
}
