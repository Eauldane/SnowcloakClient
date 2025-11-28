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
        Visible,
        SnowHandled
    };

    private readonly DalamudUtilService _dalamudUtil;
    private readonly ConcurrentDictionary<string, TrackedPlayerStatus> _trackedPlayerVisibility = new(StringComparer.Ordinal);
    private readonly List<string> _makeVisibleNextFrame = new();
    private readonly IpcCallerSnow _snow;
    private readonly HashSet<nint> cachedSnowAddresses = new();
    private uint _cachedAddressSum = 0;
    private uint _cachedAddressSumDebounce = 1;

    public VisibilityService(ILogger<VisibilityService> logger, SnowMediator mediator, IpcCallerSnow snow, DalamudUtilService dalamudUtil)
        : base(logger, mediator)
    {
        _snow = snow;
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
        var snowHandledAddresses = _snow.GetHandledGameAddresses();
        uint addressSum = 0;

        foreach (var addr in snowHandledAddresses)
            addressSum ^= (uint)addr.GetHashCode();

        if (addressSum != _cachedAddressSum)
        {
            if (addressSum == _cachedAddressSumDebounce)
            {
                cachedSnowAddresses.Clear();
                foreach (var addr in snowHandledAddresses)
                    cachedSnowAddresses.Add(addr);
                _cachedAddressSum = addressSum;
            }
            else
            {
                _cachedAddressSumDebounce = addressSum;
            }
        }

        foreach (var player in _trackedPlayerVisibility)
        {
            string ident = player.Key;
            var findResult = _dalamudUtil.FindPlayerByNameHash(ident);
            var isSnowHandled = cachedSnowAddresses.Contains(findResult.Address);
            var isVisible = findResult.ObjectId != 0 && !isSnowHandled;

            if (player.Value == TrackedPlayerStatus.SnowHandled && !isSnowHandled)
                _trackedPlayerVisibility.TryUpdate(ident, newValue: TrackedPlayerStatus.NotVisible, comparisonValue: TrackedPlayerStatus.SnowHandled);

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
            else if (player.Value == TrackedPlayerStatus.NotVisible && isSnowHandled)
            {
                // Send a technically redundant visibility update with the added intent of triggering PairHandler to undo the application by name
                if (_trackedPlayerVisibility.TryUpdate(ident, newValue: TrackedPlayerStatus.SnowHandled, comparisonValue: TrackedPlayerStatus.NotVisible))
                    Mediator.Publish<PlayerVisibilityMessage>(new(ident, IsVisible: false, Invalidate: true));
            }
            else if (player.Value == TrackedPlayerStatus.Visible && !isVisible)
            {
                var newTrackedStatus = isSnowHandled ? TrackedPlayerStatus.SnowHandled : TrackedPlayerStatus.NotVisible;
                if (_trackedPlayerVisibility.TryUpdate(ident, newValue: newTrackedStatus, comparisonValue: TrackedPlayerStatus.Visible))
                    Mediator.Publish<PlayerVisibilityMessage>(new(ident, IsVisible: false, Invalidate: isSnowHandled));
            }

            if (!isVisible)
                _makeVisibleNextFrame.Remove(ident);
        }
    }
}