using Microsoft.Extensions.Logging;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Services;

// Detect when players of interest are visible
public class VisibilityService : DisposableMediatorSubscriberBase
{
    private readonly ObjectTableCache _objectTableCache;
    private readonly IFrameTickHandle _tick;
    private readonly Lock _trackingGate = new();
    private readonly HashSet<string> _trackedPlayers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visiblePending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visiblePlayers = new(StringComparer.Ordinal);
    private readonly List<string> _noLongerVisible = [];

    public VisibilityService(ILogger<VisibilityService> logger, SnowMediator mediator, ObjectTableCache objectTableCache, IFrameScheduler frameScheduler)
        : base(logger, mediator)
    {
        _objectTableCache = objectTableCache;
        _tick = frameScheduler.Register("Visibility", TickInterval.EveryMilliseconds(100), TickPriority.High, FrameworkUpdate,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
    }

    protected override void Dispose(bool disposing)
    {
        _tick.Dispose();
        base.Dispose(disposing);
    }

    public void StartTracking(string ident)
    {
        lock (_trackingGate)
            _trackedPlayers.Add(ident);
    }

    public void StopTracking(string ident)
    {
        // No PairVisibilityMessage is emitted if the player was visible when removed
        lock (_trackingGate)
        {
            _trackedPlayers.Remove(ident);
            _visiblePending.Remove(ident);
            _visiblePlayers.Remove(ident);
        }
    }

    public void RearmTracking(string ident)
    {
        lock (_trackingGate)
        {
            _trackedPlayers.Add(ident);
            _visiblePending.Remove(ident);
            _visiblePlayers.Remove(ident);
        }
    }

    private void FrameworkUpdate()
    {
        var snapshot = _objectTableCache.PlayerCharactersSnapshot;

        lock (_trackingGate)
        {
            foreach (var (ident, player) in snapshot)
            {
                if (player.EntityId == 0 || !_trackedPlayers.Contains(ident) || _visiblePlayers.Contains(ident))
                    continue;

                if (_visiblePending.Remove(ident))
                {
                    _visiblePlayers.Add(ident);
                    Mediator.Publish<PlayerVisibilityMessage>(new(ident, IsVisible: true, Invalidate: false));
                }
                else
                {
                    _visiblePending.Add(ident);
                }
            }

            _noLongerVisible.Clear();
            foreach (var ident in _visiblePending)
            {
                if (!snapshot.TryGetValue(ident, out var player) || player.EntityId == 0)
                    _noLongerVisible.Add(ident);
            }

            foreach (var ident in _noLongerVisible)
                _visiblePending.Remove(ident);

            _noLongerVisible.Clear();
            foreach (var ident in _visiblePlayers)
            {
                if (!snapshot.TryGetValue(ident, out var player) || player.EntityId == 0)
                    _noLongerVisible.Add(ident);
            }

            foreach (var ident in _noLongerVisible)
            {
                _visiblePlayers.Remove(ident);
                Mediator.Publish<PlayerVisibilityMessage>(new(ident, IsVisible: false, Invalidate: false));
            }
        }
    }
}
