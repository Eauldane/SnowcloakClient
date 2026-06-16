using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.WebAPI;

namespace Snowcloak.Services.Pairing;

internal sealed class NearbyPresenceScanner : IDisposable
{
    private static readonly TimeSpan NearbyAvailabilityPollInterval = TimeSpan.FromSeconds(5);
    private const int EmptyNearbySnapshotConfirmations = 2;
    private const int MaxNearbySnapshot = 1024;
    private static readonly Action<ILogger, Exception?> LogAvailabilityQueryFailed =
        LoggerMessage.Define(LogLevel.Trace, new EventId(1, nameof(LogAvailabilityQueryFailed)),
            "Failed to query nearby pairing availability");
    private static readonly Action<ILogger, string, int, Exception?> LogResumingSubscription =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(2, nameof(LogResumingSubscription)),
            "Resuming pairing availability subscription (token: {ResumeToken}, nearbyHint: {NearbyHint})");
    private static readonly Action<ILogger, Exception?> LogResumeFailed =
        LoggerMessage.Define(LogLevel.Trace, new EventId(3, nameof(LogResumeFailed)),
            "Failed to resume pairing availability subscription");
    private static readonly Action<ILogger, Exception?> LogRefreshFailed =
        LoggerMessage.Define(LogLevel.Trace, new EventId(4, nameof(LogRefreshFailed)),
            "Failed to refresh nearby pairing availability");
    private static readonly Action<ILogger, Exception?> LogResumeLocationFailed =
        LoggerMessage.Define(LogLevel.Trace, new EventId(5, nameof(LogResumeLocationFailed)),
            "Failed to retrieve map data while resuming pairing availability subscription");

    private readonly ILogger _logger;
    private readonly SnowcloakConfigService _configService;
    private readonly Lazy<ApiController> _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly PairManager _pairManager;
    private readonly PairingAvailabilityStore _availabilityStore;
    private readonly AvailabilitySubscriptionClient _subscriptionClient;
    private readonly Func<HashSet<string>, Task> _evaluatePendingRequests;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly HashSet<string> _lastNearbyIdentSnapshot = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _scanCts = new();
    private DateTime _lastNearbyAvailabilityCheck = DateTime.MinValue;
    private int _emptyNearbySnapshotCount;

    public NearbyPresenceScanner(ILogger logger, SnowcloakConfigService configService,
        Lazy<ApiController> apiController, DalamudUtilService dalamudUtilService, PairManager pairManager,
        PairingAvailabilityStore availabilityStore, AvailabilitySubscriptionClient subscriptionClient,
        Func<HashSet<string>, Task> evaluatePendingRequests)
    {
        _logger = logger;
        _configService = configService;
        _apiController = apiController;
        _dalamudUtilService = dalamudUtilService;
        _pairManager = pairManager;
        _availabilityStore = availabilityStore;
        _subscriptionClient = subscriptionClient;
        _evaluatePendingRequests = evaluatePendingRequests;
    }

    public IReadOnlyCollection<string> GetLastNearbySnapshot()
    {
        lock (_lastNearbyIdentSnapshot)
        {
            return _lastNearbyIdentSnapshot.ToArray();
        }
    }

    public CancellationToken Token => _scanCts.Token;

    public Task RunAsync() => PollAsync(_scanCts.Token);

    public void ResetCooldown() => _lastNearbyAvailabilityCheck = DateTime.MinValue;

    public void ResetLocation() => _subscriptionClient.ResetLocation();

    public void ResetConnection()
    {
        _subscriptionClient.ResetConnection();
        _availabilityStore.SetAvailabilityChannelActive(false);
        _lastNearbyAvailabilityCheck = DateTime.MinValue;
        _emptyNearbySnapshotCount = 0;
    }

    public void ClearNearbySnapshot()
    {
        lock (_lastNearbyIdentSnapshot)
        {
            _lastNearbyIdentSnapshot.Clear();
        }
        _emptyNearbySnapshotCount = 0;
    }

    public async Task RefreshWithRetriesAsync()
    {
        const int retryDelayMs = 1000;
        const int maxAttempts = 5;

        for (var attempt = 0; attempt < maxAttempts && !_scanCts.IsCancellationRequested; attempt++)
        {
            await RefreshAsync(force: true).ConfigureAwait(false);

            if (_subscriptionClient.PushChannelAvailable)
                break;

            try
            {
                await Task.Delay(retryDelayMs, _scanCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastNearbyAvailabilityCheck < NearbyAvailabilityPollInterval)
            return;

        if (force)
        {
            await _scanGate.WaitAsync().ConfigureAwait(false);
        }
        else if (!await _scanGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!_configService.Current.PairingSystemEnabled)
            {
                _availabilityStore.Clear();
                _availabilityStore.SetAvailabilityChannelActive(false);
                _lastNearbyAvailabilityCheck = DateTime.UtcNow;
                return;
            }

            if (!_apiController.Value.IsConnected)
            {
                _subscriptionClient.ResetConnection();
                _availabilityStore.SetAvailabilityChannelActive(false);
                _lastNearbyAvailabilityCheck = DateTime.MinValue;
                return;
            }

            _lastNearbyAvailabilityCheck = DateTime.UtcNow;
            HashSet<string> nearbySet = new(StringComparer.Ordinal);

            try
            {
                var localIdent = await _dalamudUtilService.GetPlayerNameHashedAsync().ConfigureAwait(false);
                _availabilityStore.SetLocalPlayerIdent(localIdent);
                var nearby = await _dalamudUtilService.GetNearbyPlayerNameHashesAsync(MaxNearbySnapshot)
                    .ConfigureAwait(false);
                var location = await _dalamudUtilService.GetMapDataAsync().ConfigureAwait(false);

                nearbySet = BuildNearbySet(nearby, localIdent);
                if (ShouldHoldEmptyNearbySnapshot(nearbySet, force))
                {
                    await _evaluatePendingRequests(GetLastNearbySnapshot().ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);
                    return;
                }

                var (entered, left) = ApplyNearbySnapshot(nearbySet, force);

                if (left.Count > 0)
                    _availabilityStore.ApplyDelta(Array.Empty<string>(), left, publishImmediately: true);

                if (nearbySet.Count == 0)
                    _availabilityStore.Clear();

                var subscribed = await _subscriptionClient.UpdateAsync(location, nearbySet, entered, left, force, cancellationToken: _scanCts.Token)
                    .ConfigureAwait(false);
                _availabilityStore.SetAvailabilityChannelActive(subscribed);

                if (entered.Count == 0 && left.Count == 0 && !force && _subscriptionClient.PushChannelAvailable)
                {
                    await _evaluatePendingRequests(nearbySet).ConfigureAwait(false);
                    return;
                }

                var shouldPollAvailability = force || !_subscriptionClient.PushChannelAvailable;
                var queryTargets = shouldPollAvailability ? nearbySet : entered;
                if (shouldPollAvailability && queryTargets.Count > 0)
                {
                    await _apiController.Value
                        .UserQueryPairingAvailability(new PairingAvailabilityQueryDto([.. queryTargets]))
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_scanCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogAvailabilityQueryFailed(_logger, ex);
            }

            await _evaluatePendingRequests(nearbySet).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public async Task ResumeAsync(PairingAvailabilityResumeRequestDto resumeRequest)
    {
        LogResumingSubscription(_logger, resumeRequest.ResumeToken, resumeRequest.NearbyIdentsCount, null);

        await _scanGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_configService.Current.PairingSystemEnabled)
                return;

            if (!_apiController.Value.IsConnected)
            {
                _subscriptionClient.ResetConnection();
                _availabilityStore.SetAvailabilityChannelActive(false);
                return;
            }

            var localIdent = await _dalamudUtilService.GetPlayerNameHashedAsync().ConfigureAwait(false);
            _availabilityStore.SetLocalPlayerIdent(localIdent);
            var nearby = await _dalamudUtilService.GetNearbyPlayerNameHashesAsync(MaxNearbySnapshot)
                .ConfigureAwait(false);
            var nearbySet = BuildNearbySet(nearby, localIdent);
            ReplaceNearbySnapshot(nearbySet);

            var location = await GetResumeLocationAsync(resumeRequest).ConfigureAwait(false);
            _lastNearbyAvailabilityCheck = DateTime.UtcNow;

            var subscribed = await _subscriptionClient.UpdateAsync(
                    location,
                    nearbySet,
                    nearbySet,
                    Array.Empty<string>(),
                    force: true,
                    forceFullSnapshot: true,
                    cancellationToken: _scanCts.Token)
                .ConfigureAwait(false);
            _availabilityStore.SetAvailabilityChannelActive(subscribed);

            await _evaluatePendingRequests(nearbySet).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogResumeFailed(_logger, ex);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRefreshFailed(_logger, ex);
            }

            try
            {
                await Task.Delay(NearbyAvailabilityPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private HashSet<string> BuildNearbySet(IEnumerable<string> nearby, string localIdent)
    {
        var nearbySet = new HashSet<string>(nearby, StringComparer.Ordinal);
        nearbySet.Remove(localIdent);
        nearbySet.ExceptWith(_pairManager.DirectPairs
            .Select(pair => pair.Ident)
            .Where(ident => !string.IsNullOrEmpty(ident)));
        return nearbySet;
    }

    private bool ShouldHoldEmptyNearbySnapshot(HashSet<string> nearbySet, bool force)
    {
        if (nearbySet.Count > 0)
        {
            _emptyNearbySnapshotCount = 0;
            return false;
        }

        if (force || GetLastNearbySnapshot().Count == 0)
        {
            _emptyNearbySnapshotCount = 0;
            return false;
        }

        _emptyNearbySnapshotCount++;
        if (_emptyNearbySnapshotCount < EmptyNearbySnapshotConfirmations)
            return true;

        _emptyNearbySnapshotCount = 0;
        return false;
    }

    private (HashSet<string> Entered, HashSet<string> Left) ApplyNearbySnapshot(HashSet<string> nearbySet, bool force)
    {
        lock (_lastNearbyIdentSnapshot)
        {
            var entered = new HashSet<string>(nearbySet, StringComparer.Ordinal);
            if (!force)
                entered.ExceptWith(_lastNearbyIdentSnapshot);

            var left = new HashSet<string>(_lastNearbyIdentSnapshot, StringComparer.Ordinal);
            left.ExceptWith(nearbySet);

            _lastNearbyIdentSnapshot.Clear();
            foreach (var ident in nearbySet)
                _lastNearbyIdentSnapshot.Add(ident);

            return (entered, left);
        }
    }

    private void ReplaceNearbySnapshot(HashSet<string> nearbySet)
    {
        lock (_lastNearbyIdentSnapshot)
        {
            _lastNearbyIdentSnapshot.Clear();
            foreach (var ident in nearbySet)
                _lastNearbyIdentSnapshot.Add(ident);
        }
    }

    private async Task<LocationInfo> GetResumeLocationAsync(PairingAvailabilityResumeRequestDto resumeRequest)
    {
        var location = new LocationInfo { ServerId = resumeRequest.WorldId, TerritoryId = resumeRequest.TerritoryId };
        try
        {
            location = await _dalamudUtilService.GetMapDataAsync().ConfigureAwait(false);
            if (location.ServerId == 0)
                location.ServerId = resumeRequest.WorldId;
            if (location.TerritoryId == 0)
                location.TerritoryId = resumeRequest.TerritoryId;
        }
        catch (Exception ex)
        {
            LogResumeLocationFailed(_logger, ex);
        }

        return location;
    }

    public void Cancel() => _scanCts.Cancel();

    public void Dispose()
    {
        _scanCts.Dispose();
        _scanGate.Dispose();
    }
}
