using Microsoft.Extensions.Logging;
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
    private readonly ConcurrentDictionary<string, TrackedPlayerStatus> _trackedPlayerVisibility = new(StringComparer.Ordinal);
    private readonly List<string> _makeVisibleNextFrame = new();

    public VisibilityService(ILogger<VisibilityService> logger, SnowMediator mediator, DalamudUtilService dalamudUtil)
        : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        Mediator.Subscribe<FrameworkUpdateMessage>(this, (_) => FrameworkUpdate());
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
        foreach (var player in _trackedPlayerVisibility)
        {
            string ident = player.Key;
            var findResult = _dalamudUtil.FindPlayerByNameHash(ident);
            var isVisible = findResult.ObjectId != 0;
            
            if (player.Value == TrackedPlayerStatus.NotVisible && isVisible)
            {
                if (_makeVisibleNextFrame.Contains(ident))
                {
                    if (_trackedPlayerVisibility.TryUpdate(ident, newValue: TrackedPlayerStatus.Visible, comparisonValue: TrackedPlayerStatus.NotVisible))
                        Mediator.Publish<PlayerVisibilityMessage>(new(ident, IsVisible: true));
                }
                else
                    _makeVisibleNextFrame.Add(ident);
            }
            else if (player.Value == TrackedPlayerStatus.Visible && !isVisible)
            {
                if (_trackedPlayerVisibility.TryUpdate(ident, newValue: TrackedPlayerStatus.NotVisible, comparisonValue: TrackedPlayerStatus.Visible))
                    Mediator.Publish<PlayerVisibilityMessage>(new(ident, IsVisible: false, Invalidate: false));
            }

            if (!isVisible)
                _makeVisibleNextFrame.Remove(ident);
        }
    }
}