using Microsoft.Extensions.Logging;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services.Mediator;
using System.Collections.Concurrent;

namespace Snowcloak.Services;

// Detect when players of interest are visible
public class VisibilityService : DisposableMediatorSubscriberBase
{
    private enum TrackedPlayerStatus
    {
        NotVisible,
        Visible
    };

    private readonly DalamudUtilService _dalamudUtil;
    private readonly IFrameTickHandle _tick;
    private readonly ConcurrentDictionary<string, TrackedPlayerStatus> _trackedPlayerVisibility = new(StringComparer.Ordinal);
    private readonly List<string> _makeVisibleNextFrame = new();

    public VisibilityService(ILogger<VisibilityService> logger, SnowMediator mediator, DalamudUtilService dalamudUtil, IFrameScheduler frameScheduler)
        : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        _tick = frameScheduler.Register("Visibility", TickInterval.EveryFrame, TickPriority.Critical, FrameworkUpdate,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
    }

    protected override void Dispose(bool disposing)
    {
        _tick.Dispose();
        base.Dispose(disposing);
    }

    public void StartTracking(string ident)
    {
        _trackedPlayerVisibility.TryAdd(ident, TrackedPlayerStatus.NotVisible);
    }

    public void StopTracking(string ident)
    {
        // No PairVisibilityMessage is emitted if the player was visible when removed
        _trackedPlayerVisibility.TryRemove(ident, out _);
    }

    private void FrameworkUpdate()
    {
        List<(string Ident, bool Visible)>? toPublish = null;

        foreach (var player in _trackedPlayerVisibility)
        {
            string ident = player.Key;
            var findResult = _dalamudUtil.FindPlayerByNameHash(ident);
            var isVisible = findResult.EntityId != 0;

            if (player.Value == TrackedPlayerStatus.NotVisible && isVisible)
            {
                if (_makeVisibleNextFrame.Contains(ident))
                {
                    if (_trackedPlayerVisibility.TryUpdate(ident, newValue: TrackedPlayerStatus.Visible, comparisonValue: TrackedPlayerStatus.NotVisible))
                        (toPublish ??= new()).Add((ident, true));
                }
                else
                    _makeVisibleNextFrame.Add(ident);
            }
            else if (player.Value == TrackedPlayerStatus.Visible && !isVisible)
            {
                if (_trackedPlayerVisibility.TryUpdate(ident, newValue: TrackedPlayerStatus.NotVisible, comparisonValue: TrackedPlayerStatus.Visible))
                    (toPublish ??= new()).Add((ident, false));
            }

            if (!isVisible)
                _makeVisibleNextFrame.Remove(ident);
        }

        if (toPublish == null)
            return;

        foreach (var (ident, visible) in toPublish)
            Mediator.Publish<PlayerVisibilityMessage>(new(ident, IsVisible: visible, Invalidate: false));
    }
}
