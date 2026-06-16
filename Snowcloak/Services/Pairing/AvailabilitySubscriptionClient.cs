using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.API.Dto.User;
using Snowcloak.WebAPI;

namespace Snowcloak.Services.Pairing;

internal sealed class AvailabilitySubscriptionClient : IDisposable
{
    private const int MaxSubscriptionSnapshot = 256;
    private static readonly Action<ILogger, int, int, Exception?> LogSnapshotTrimmed =
        LoggerMessage.Define<int, int>(LogLevel.Warning, new EventId(1, nameof(LogSnapshotTrimmed)),
            "Nearby ident snapshot exceeds server cap; trimming to {MaxCount} entries (had {Count})");
    private static readonly Action<ILogger, Exception?> LogSubscriptionUpdateFailed =
        LoggerMessage.Define(LogLevel.Trace, new EventId(2, nameof(LogSubscriptionUpdateFailed)),
            "Failed to update pairing availability subscription");
    private static readonly Action<ILogger, Exception?> LogUnsubscribeFailed =
        LoggerMessage.Define(LogLevel.Trace, new EventId(3, nameof(LogUnsubscribeFailed)),
            "Failed to unsubscribe from pairing availability push channel");

    private readonly ILogger _logger;
    private readonly Lazy<ApiController> _apiController;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private PairingAvailabilitySubscriptionState _state = PairingAvailabilitySubscriptionState.Idle;
    private bool _pushChannelAvailable;
    private LocationInfo? _lastLocation;

    public AvailabilitySubscriptionClient(ILogger logger, Lazy<ApiController> apiController)
    {
        _logger = logger;
        _apiController = apiController;
    }

    public bool IsChannelActive
    {
        get
        {
            lock (_stateLock)
            {
                return _state == PairingAvailabilitySubscriptionState.Active && _pushChannelAvailable;
            }
        }
    }

    public bool PushChannelAvailable
    {
        get
        {
            lock (_stateLock)
            {
                return _pushChannelAvailable;
            }
        }
    }

    public void ResetConnection()
    {
        lock (_stateLock)
        {
            _state = PairingAvailabilitySubscriptionState.Idle;
            _pushChannelAvailable = false;
            _lastLocation = null;
        }
    }

    public void ResetLocation()
    {
        lock (_stateLock)
        {
            _lastLocation = null;
        }
    }

    public async Task<bool> UpdateAsync(LocationInfo location,
        IReadOnlyCollection<string> nearbySnapshot, IReadOnlyCollection<string> entered,
        IReadOnlyCollection<string> left, bool force = false, bool forceFullSnapshot = false,
        CancellationToken cancellationToken = default)
    {
        if (force)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return PushChannelAvailable;
        }

        try
        {
            if (!_apiController.Value.IsConnected && (!force || !await WaitForApiConnectionAsync(cancellationToken).ConfigureAwait(false)))
            {
                SetState(PairingAvailabilitySubscriptionState.Idle, false, null);
                return false;
            }

            var sendFullSnapshot = forceFullSnapshot || RequiresNewSubscription(location);
            var nearbyPayload = sendFullSnapshot ? nearbySnapshot : Array.Empty<string>();
            var addedPayload = sendFullSnapshot ? nearbySnapshot : entered;
            var removedPayload = left;

            if (sendFullSnapshot && nearbyPayload.Count > MaxSubscriptionSnapshot)
            {
                LogSnapshotTrimmed(_logger, MaxSubscriptionSnapshot, nearbyPayload.Count, null);
                nearbyPayload = nearbyPayload.Take(MaxSubscriptionSnapshot).ToArray();
                addedPayload = addedPayload.Take(MaxSubscriptionSnapshot).ToArray();
            }

            if (!IsChannelActive)
                SetState(forceFullSnapshot ? PairingAvailabilitySubscriptionState.Resuming : PairingAvailabilitySubscriptionState.Subscribing, false, _lastLocation);

            var subscription = new PairingAvailabilitySubscriptionDto(
                location.ServerId,
                location.TerritoryId,
                nearbyPayload,
                addedPayload,
                removedPayload);

            var subscribed = await _apiController.Value.UserSubscribePairingAvailability(subscription)
                .ConfigureAwait(false);

            SetState(subscribed ? PairingAvailabilitySubscriptionState.Active : PairingAvailabilitySubscriptionState.Idle, subscribed, location);
            return subscribed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSubscriptionUpdateFailed(_logger, ex);
            SetState(PairingAvailabilitySubscriptionState.Idle, false, _lastLocation);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsChannelActive)
        {
            ResetConnection();
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(PairingAvailabilitySubscriptionState.Stopping, PushChannelAvailable, _lastLocation);
            if (_apiController.Value.IsConnected)
                await _apiController.Value.UserUnsubscribePairingAvailability().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogUnsubscribeFailed(_logger, ex);
        }
        finally
        {
            SetState(PairingAvailabilitySubscriptionState.Idle, false, null);
            _gate.Release();
        }
    }

    private bool RequiresNewSubscription(LocationInfo location)
    {
        lock (_stateLock)
        {
            return !_lastLocation.HasValue
                || _lastLocation.Value.ServerId != location.ServerId
                || _lastLocation.Value.TerritoryId != location.TerritoryId;
        }
    }

    private void SetState(PairingAvailabilitySubscriptionState state, bool pushChannelAvailable, LocationInfo? location)
    {
        lock (_stateLock)
        {
            _state = state;
            _pushChannelAvailable = pushChannelAvailable;
            _lastLocation = location;
        }
    }

    private async Task<bool> WaitForApiConnectionAsync(CancellationToken cancellationToken)
    {
        const int retryCount = 10;
        const int retryDelayMs = 200;

        for (var attempt = 0; attempt < retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);

            if (_apiController.Value.IsConnected)
                return true;
        }

        return _apiController.Value.IsConnected;
    }

    public void Dispose() => _gate.Dispose();
}

internal enum PairingAvailabilitySubscriptionState
{
    Idle,
    Subscribing,
    Active,
    Resuming,
    Stopping,
}
