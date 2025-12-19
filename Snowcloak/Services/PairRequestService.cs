using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Microsoft.Extensions.DependencyInjection;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.Configuration.Models;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.IO;
using System.Text.Json;
using Snowcloak.Services.ServerConfiguration;
using System.Threading;
using Snowcloak.PlayerData.Pairs;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using System.Runtime.InteropServices;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.Services;

public class PairRequestService : DisposableMediatorSubscriberBase
{
    private readonly ILogger<PairRequestService> _logger;
    private readonly SnowcloakConfigService _configService;
    private readonly Lazy<ApiController> _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IpcManager _ipcManager;
    private readonly IToastGui _toastGui;
    private readonly PairManager _pairManager;
    private readonly SnowProfileManager _snowProfileManager;
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pendingRequests = new();
    private readonly HashSet<string> _availableIdents = new(StringComparer.Ordinal);
    private readonly object _availabilityFilterLock = new();
    private HashSet<string> _filteredAvailableIdents = new(StringComparer.Ordinal);
    private HashSet<string> _unfilteredAvailableIdents = new(StringComparer.Ordinal);
    private CancellationTokenSource? _availabilityFilterCts;
    private readonly IContextMenu _contextMenu;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly HashSet<string> _lastNearbyIdentSnapshot = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _nearbyAvailabilityCts = new();
    private readonly Task _nearbyAvailabilityLoop;
    private static readonly TimeSpan AvailabilityApplyDebounce = TimeSpan.FromMilliseconds(150);
    private readonly SemaphoreSlim _nearbyAvailabilitySemaphore = new(1, 1);
    private DateTime _lastNearbyAvailabilityCheck = DateTime.MinValue;
    private string _localPlayerIdent = string.Empty;
    // Fallback frequency for polling when the push channel is unavailable; capped to once per minute.
    private static readonly TimeSpan NearbyAvailabilityPollInterval = TimeSpan.FromSeconds(15);
    private const int MaxNearbySnapshot = 1024;
    private bool _advertisingPairing;
    private bool _pushChannelAvailable;
    private bool _hasPersistentKey = true;
    private DateTime _lastPersistentKeyWarningAt = DateTime.MinValue;
    private static readonly TimeSpan PersistentKeyWarningCooldown = TimeSpan.FromSeconds(10);
    private string MissingPersistentKeyWarning = "You need to link a XIVAuth account before opting into Frostbrand. Please visit the user guide to learn how to do this.";
    private bool _availabilitySubscriptionActive;
    private LocationInfo? _lastSubscriptionLocation;
    private readonly SemaphoreSlim _availabilitySubscriptionSemaphore = new(1, 1);
    private readonly object _availabilityUpdateLock = new();
    private readonly record struct PendingRequest(PairingRequestDto Request, bool DeferredAutoFilter);
    private readonly record struct AutoRejectResult(bool ShouldReject, string Reason, bool WasDeferred);

    public PairRequestService(ILogger<PairRequestService> logger, SnowcloakConfigService configService,
        SnowMediator mediator, DalamudUtilService dalamudUtilService,
        IpcManager ipcManager, IToastGui toastGui, IContextMenu contextMenu, IServiceProvider serviceProvider,
        ServerConfigurationManager serverConfigurationManager, PairManager pairManager, SnowProfileManager snowProfileManager)
        : base(logger, mediator)
    {
        _logger = logger;
        _configService = configService;
        _apiController = new Lazy<ApiController>(() => serviceProvider.GetRequiredService<ApiController>());
        _dalamudUtilService = dalamudUtilService;
        _ipcManager = ipcManager;
        _toastGui = toastGui;
        _contextMenu = contextMenu;
        _pairManager = pairManager;
        _snowProfileManager = snowProfileManager;
        _serverConfigurationManager = serverConfigurationManager;
        _contextMenu.OnMenuOpened += ContextMenuOnMenuOpened;
        Mediator.Subscribe<TargetPlayerChangedMessage>(this, OnTargetPlayerChanged);
        _configService.ConfigSave += OnConfigSave;
        
        
        Mediator.Subscribe<DalamudLoginMessage>(this, OnPlayerLoggedIn);
        Mediator.Subscribe<DalamudLogoutMessage>(this, OnPlayerLoggedOut);
        Mediator.Subscribe<ZoneSwitchEndMessage>(this, OnZoneChanged);
        Mediator.Subscribe<ConnectedMessage>(this, OnConnected);
        Mediator.Subscribe<HubReconnectedMessage>(this, OnHubReconnected);
        Mediator.Subscribe<DisconnectedMessage>(this, _ => HandleDisconnect());
        _nearbyAvailabilityLoop = Task.Run(() => PollNearbyAvailabilityAsync(_nearbyAvailabilityCts.Token));
    }

    private void OnConnected(ConnectedMessage message)
    {
        ApplyPersistentKeyState(message.Connection.HasPersistentKey);
        _ = OnConnectedAsync();
    }
    
    private void OnHubReconnected(HubReconnectedMessage message) => _ = OnConnectedAsync();
    
    public Task ResumePairingAvailabilitySubscriptionAsync(PairingAvailabilityResumeRequestDto resumeRequest)
        => ResumePairingAvailabilitySubscriptionInternalAsync(resumeRequest);
    private void OnPlayerLoggedIn(DalamudLoginMessage message) => _ = OnPlayerLoggedInAsync();
    private Task OnPlayerLoggedInAsync()
    {
        _lastSubscriptionLocation = null;
        return RefreshNearbyAvailabilityWithRetriesAsync();
    }
    
    private void OnPlayerLoggedOut(DalamudLogoutMessage message) => _ = OnPlayerLoggedOutAsync();
    private async Task OnPlayerLoggedOutAsync()
    {
        await StopAvailabilitySubscriptionAsync().ConfigureAwait(false);
        _lastNearbyIdentSnapshot.Clear();
        ClearAvailability();
    }
    
    
    private void ApplyPersistentKeyState(bool hasPersistentKey)
    {
        _hasPersistentKey = hasPersistentKey;

        if (_hasPersistentKey)
            return;

        EnforcePairingOptOutForMissingKey(showWarning: _configService.Current.PairingSystemEnabled);
    }

    private void EnforcePairingOptOutForMissingKey(bool showWarning)
    {
        var wasEnabled = _configService.Current.PairingSystemEnabled;
        if (wasEnabled)
        {
            _configService.Current.PairingSystemEnabled = false;
            _configService.Save();
        }

        _advertisingPairing = false;
        _pushChannelAvailable = false;
        _availabilitySubscriptionActive = false;
        _lastSubscriptionLocation = null;
        _lastNearbyAvailabilityCheck = DateTime.MinValue;
        _lastNearbyIdentSnapshot.Clear();

        _ = StopAvailabilitySubscriptionAsync();
        ClearAvailability();

        if (showWarning && wasEnabled)
            PublishMissingPersistentKeyWarning();
    }

    private void PublishMissingPersistentKeyWarning()
    {
        if (DateTime.UtcNow - _lastPersistentKeyWarningAt < PersistentKeyWarningCooldown)
            return;

        _lastPersistentKeyWarningAt = DateTime.UtcNow;
        Mediator.Publish(new NotificationMessage("Pairing availability unavailable", MissingPersistentKeyWarning, NotificationType.Warning, TimeSpan.FromSeconds(7.5)));
    }
    
    private void HandleDisconnect()
    {
        _availabilitySubscriptionActive = false;
        _pushChannelAvailable = false;
        _lastSubscriptionLocation = null;

        HashSet<string> unavailable;
        lock (_availabilityUpdateLock)
        {
            unavailable = _availableIdents.ToHashSet(StringComparer.Ordinal);
        }

        if (unavailable.Count > 0)
        {
            ApplyAvailabilityDelta(Array.Empty<string>(), unavailable, publishImmediately: true);
        }
    }
    
    private void OnZoneChanged(ZoneSwitchEndMessage message) => _ = OnZoneChangedAsync();
    private Task OnZoneChangedAsync()
    {
        _lastSubscriptionLocation = null;
        return RefreshNearbyAvailabilityAsync(force: true);
    }


    private async Task OnConnectedAsync()
    {
        _availabilitySubscriptionActive = false;
        _pushChannelAvailable = false;
        _lastSubscriptionLocation = null;
        _lastNearbyAvailabilityCheck = DateTime.MinValue;

        await SyncAdvertisingAsync(force: true).ConfigureAwait(false);
        await RefreshNearbyAvailabilityWithRetriesAsync().ConfigureAwait(false);
    }

    private async Task RefreshNearbyAvailabilityWithRetriesAsync()
    {
        const int retryDelayMs = 1000;
        const int maxAttempts = 5;

        for (var attempt = 0; attempt < maxAttempts && !_nearbyAvailabilityCts.IsCancellationRequested; attempt++)
        {
            await RefreshNearbyAvailabilityAsync(force: true).ConfigureAwait(false);

            if (_pushChannelAvailable)
                break;

            try
            {
                await Task.Delay(retryDelayMs, _nearbyAvailabilityCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _contextMenu.OnMenuOpened -= ContextMenuOnMenuOpened;
        _configService.ConfigSave -= OnConfigSave;
        
        _nearbyAvailabilityCts.Cancel();
        try
        {
            _ = StopAvailabilitySubscriptionAsync();
            _nearbyAvailabilityLoop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // ignored
        }
    }

    public IReadOnlyCollection<string> AvailableIdents
        => _availableIdents.ToArray();
    
    public AvailabilityFilterSnapshot GetAvailabilityFilterSnapshot()
    {
        lock (_availabilityFilterLock)
        {
            return new AvailabilityFilterSnapshot(
                new List<string>(_unfilteredAvailableIdents),
                _filteredAvailableIdents.Count);
        }
    }

    public IReadOnlyCollection<PairingRequestDto> PendingRequests
        => _pendingRequests.Values.Select(p => p.Request).ToList();
    
    public bool IsAvailabilityChannelActive => _availabilitySubscriptionActive && _pushChannelAvailable;
    
    private void ContextMenuOnMenuOpened(IMenuOpenedArgs args)
    {
        if (!_configService.Current.EnableRightClickMenus) return;
        if (!_configService.Current.PairingSystemEnabled) return;
        if (args.MenuType == ContextMenuType.Inventory) return;
        if (!_dalamudUtilService.TryGetIdentFromMenuTarget(args, out var ident)) return;
        if (!_availableIdents.Contains(ident)) return;

        void Add(string name, Action<IMenuItemClickedArgs>? action)
        {
            args.AddMenuItem(new MenuItem
            {
                Name = name,
                PrefixChar = 'S',
                PrefixColor = 526,
                OnClicked = action
            });
        }

        Add("Send Snowcloak Pair Request", async _ => await SendPairRequestAsync(ident).ConfigureAwait(false));
        Add("View Snowcloak Profile", async _ => await RequestProfileAsync(ident).ConfigureAwait(false));
    }

    public async Task RequestProfileAsync(string ident)
    {
        Mediator.Publish(new NotificationMessage("Profile request", "Requesting profile from nearby player", NotificationType.Info, TimeSpan.FromSeconds(4)));

        try
        {
            var profile = await _snowProfileManager.GetSnowProfileAsync(userData: null, ident: ident, visibilityOverride: ProfileVisibility.Public, forceRefresh: true).ConfigureAwait(false);
            var userData = profile.User ?? new UserData(ident);
            var pair = _pairManager.GetOrCreateTransientPair(userData);
            Mediator.Publish(new ProfileOpenStandaloneMessage(userData, pair, profile.Visibility));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to request profile for ident {ident}", ident);
            Mediator.Publish(new NotificationMessage("Profile request failed", "Could not retrieve that profile right now.", NotificationType.Warning, TimeSpan.FromSeconds(5)));
        }
        
    }

    public async Task SyncAdvertisingAsync(bool force = false)
    {
        var advertise = _configService.Current.PairingSystemEnabled;

        if (advertise && !_hasPersistentKey)
        {
            EnforcePairingOptOutForMissingKey(showWarning: true);
            advertise = false;
        }

        if (!force && _advertisingPairing == advertise) return;
        
        _advertisingPairing = advertise;

        try
        {
            await _apiController.Value.UserSetPairingOptIn(new PairingOptInDto(advertise)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send pairing availability update");
        }
    }

    public void UpdateAvailability(IEnumerable<PairingAvailabilityDto> available,
        IReadOnlyCollection<string>? authoritativeScope = null, bool publishImmediately = true)
    {
        var incoming = available?.Select(dto => dto.Ident)
            .Where(ident => !string.IsNullOrWhiteSpace(ident))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        
        if (!string.IsNullOrEmpty(_localPlayerIdent))
            incoming.Remove(_localPlayerIdent);
        
        var pairedIdents = _pairManager.DirectPairs
            .Where(pair => !string.IsNullOrEmpty(pair.GetPlayerNameHash()))
            .Select(p => p.Ident)
            .Where(ident => !string.IsNullOrEmpty(ident))
            .ToHashSet(StringComparer.Ordinal);

        incoming.ExceptWith(pairedIdents);
        
        if (_lastNearbyIdentSnapshot.Count > 0)
            incoming.IntersectWith(_lastNearbyIdentSnapshot);
        
        var unavailable = authoritativeScope != null
            ? authoritativeScope.Where(ident => !incoming.Contains(ident))
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        ApplyAvailabilityDelta(incoming, unavailable, publishImmediately);
        
    }

    public void ApplyAvailabilityDelta(IEnumerable<string> availableIdents,
        IReadOnlyCollection<string>? unavailableIdents = null, bool publishImmediately = true)
    {
        if (!_configService.Current.PairingSystemEnabled)
        {
            ClearAvailability();
            return;
        }

        var additions = availableIdents?.Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var removals = unavailableIdents?.Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(_localPlayerIdent))
        {
            additions.Remove(_localPlayerIdent);
            removals.Remove(_localPlayerIdent);
        }

        bool changed;
        lock (_availabilityUpdateLock)
        {
            changed = RemoveUnavailable(removals);
            changed |= AddAvailable(additions);
        }
        if (!changed)
            return;

        _ = RebuildAvailabilityFiltersAsync();

        if (publishImmediately)
            Mediator.Publish(new PairingAvailabilityChangedMessage());
    }

    private bool AddAvailable(HashSet<string> additions)
    {
        var changed = false;
        foreach (var ident in additions)
        {
            if (_availableIdents.Add(ident))
                changed = true;
        }

        return changed;
    }

    private bool RemoveUnavailable(HashSet<string> removals)
    {
        var changed = false;
        foreach (var ident in removals)
        {
            if (_availableIdents.Remove(ident))
                changed = true;
        }
        
        return changed;
    }

    private void ClearAvailability()
    {
        lock (_availabilityUpdateLock)
        {
            if (_availableIdents.Count == 0)
                return;
            _availableIdents.Clear();
        }

        lock (_availabilityFilterLock)
        {
            _filteredAvailableIdents.Clear();
            _unfilteredAvailableIdents.Clear();
        }
        
        Mediator.Publish(new PairingAvailabilityChangedMessage());
    }

    private async Task PollNearbyAvailabilityAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshNearbyAvailabilityAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Failed to refresh nearby pairing availability");
            }

            try
            {
                await Task.Delay(NearbyAvailabilityPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
        }
    }

    public async Task RefreshNearbyAvailabilityAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastNearbyAvailabilityCheck < NearbyAvailabilityPollInterval)
            return;

        if (force)
        {
            await _nearbyAvailabilitySemaphore.WaitAsync().ConfigureAwait(false);
        }
        else if (!await _nearbyAvailabilitySemaphore.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            if (!_configService.Current.PairingSystemEnabled)
            {
                ClearAvailability();
                _lastNearbyAvailabilityCheck = DateTime.UtcNow;
                return;
            }

            if (!_apiController.Value.IsConnected)
            {
                _pushChannelAvailable = false;
                _lastNearbyAvailabilityCheck = DateTime.MinValue;
                return;
            }
            _lastNearbyAvailabilityCheck = DateTime.UtcNow;
            HashSet<string> nearbySet = new(StringComparer.Ordinal);
            LocationInfo? location = null;

            try
            {
                _localPlayerIdent = await _dalamudUtilService.GetPlayerNameHashedAsync().ConfigureAwait(false);

                var nearby = await _dalamudUtilService.GetNearbyPlayerNameHashesAsync(MaxNearbySnapshot)
                    .ConfigureAwait(false);

                location = await _dalamudUtilService.GetMapDataAsync().ConfigureAwait(false);
                
                nearbySet = new HashSet<string>(nearby, StringComparer.Ordinal);

                nearbySet.Remove(_localPlayerIdent);

                nearbySet.ExceptWith(_pairManager.DirectPairs
                    .Select(p => p.Ident)
                    .Where(ident => !string.IsNullOrEmpty(ident)));
                
                var entered = new HashSet<string>(nearbySet, StringComparer.Ordinal);
                if (!force)
                    entered.ExceptWith(_lastNearbyIdentSnapshot);

                var left = new HashSet<string>(_lastNearbyIdentSnapshot, StringComparer.Ordinal);
                left.ExceptWith(nearbySet);

                if (left.Count > 0)
                    ApplyAvailabilityDelta(Array.Empty<string>(), left, publishImmediately: true);

                _lastNearbyIdentSnapshot.Clear();
                foreach (var ident in nearbySet)
                {
                    _lastNearbyIdentSnapshot.Add(ident);
                }

                if (nearbySet.Count == 0)
                    ClearAvailability();
  

                if (location.HasValue)
                {
                    await UpdateAvailabilitySubscriptionAsync(location.Value, nearbySet, entered, left, force)
                        .ConfigureAwait(false);
                }

                if (entered.Count == 0 && left.Count == 0 && !force && _pushChannelAvailable)
                {
                    await EvaluatePendingRequestsAsync(nearbySet).ConfigureAwait(false);
                    return;
                }

                var shouldPollAvailability = force || !_pushChannelAvailable;
                var queryTargets = force || !_pushChannelAvailable ? nearbySet : entered;
                if (shouldPollAvailability && queryTargets.Count > 0)
                {
                    await _apiController.Value
                        .UserQueryPairingAvailability(new PairingAvailabilityQueryDto([.. queryTargets]))
                        .ConfigureAwait(false);

                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Failed to query nearby pairing availability");
            }

            await EvaluatePendingRequestsAsync(nearbySet).ConfigureAwait(false);
        }
        finally
        {
            _nearbyAvailabilitySemaphore.Release();
        }
    }
    
    private async Task<bool> UpdateAvailabilitySubscriptionAsync(LocationInfo location,
        IReadOnlyCollection<string> nearbySnapshot, IReadOnlyCollection<string> entered,
        IReadOnlyCollection<string> left, bool force = false, bool forceFullSnapshot = false)
    {
        var requiresNewSubscription = !_lastSubscriptionLocation.HasValue
            || _lastSubscriptionLocation.Value.ServerId != location.ServerId
            || _lastSubscriptionLocation.Value.TerritoryId != location.TerritoryId;

        if (force)
        {
            await _availabilitySubscriptionSemaphore.WaitAsync().ConfigureAwait(false);
        }
        else if (!await _availabilitySubscriptionSemaphore.WaitAsync(0).ConfigureAwait(false))
            return _pushChannelAvailable;

        try
        {
            if (!_apiController.Value.IsConnected)
            {
                if (!force || !await WaitForApiConnectionAsync(_nearbyAvailabilityCts.Token).ConfigureAwait(false))
                {
                    _pushChannelAvailable = false;
                    _availabilitySubscriptionActive = false;
                    return false;
                }
            }

            var sendFullSnapshot = forceFullSnapshot || requiresNewSubscription;
            var nearbyPayload = sendFullSnapshot ? nearbySnapshot : Array.Empty<string>();
            var addedPayload = sendFullSnapshot ? nearbySnapshot : entered;
            var removedPayload = left;

            if (sendFullSnapshot && nearbyPayload.Count > 256)
            {
                _logger.LogWarning("Nearby ident snapshot exceeds server cap; trimming to 256 entries (had {Count})",
                    nearbyPayload.Count);
                nearbyPayload = nearbyPayload.Take(256).ToArray();
                addedPayload = addedPayload.Take(256).ToArray();
            }
            
            var subscription = new PairingAvailabilitySubscriptionDto(
                location.ServerId,
                location.TerritoryId,
                nearbyPayload,
                addedPayload,
                removedPayload);

            _pushChannelAvailable = await _apiController.Value
                .UserSubscribePairingAvailability(subscription)
                .ConfigureAwait(false);

            _availabilitySubscriptionActive = _pushChannelAvailable;
            _lastSubscriptionLocation = location;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to update pairing availability subscription");
            _pushChannelAvailable = false;
        }
        finally
        {
            _availabilitySubscriptionSemaphore.Release();
        }

        return _pushChannelAvailable;
    }

       private async Task ResumePairingAvailabilitySubscriptionInternalAsync(PairingAvailabilityResumeRequestDto resumeRequest)
    {
        _logger.LogInformation(
            "Resuming pairing availability subscription (token: {ResumeToken}, nearbyHint: {NearbyHint})",
            resumeRequest.ResumeToken,
            resumeRequest.NearbyIdentsCount);

        await _nearbyAvailabilitySemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_configService.Current.PairingSystemEnabled)
            {
                return;
            }

            if (!_apiController.Value.IsConnected)
            {
                _pushChannelAvailable = false;
                _availabilitySubscriptionActive = false;
                return;
            }

            _localPlayerIdent = await _dalamudUtilService.GetPlayerNameHashedAsync().ConfigureAwait(false);
            var nearby = await _dalamudUtilService.GetNearbyPlayerNameHashesAsync(MaxNearbySnapshot)
                .ConfigureAwait(false);

            var nearbySet = new HashSet<string>(nearby, StringComparer.Ordinal);
            nearbySet.Remove(_localPlayerIdent);
            nearbySet.ExceptWith(_pairManager.DirectPairs
                .Select(p => p.Ident)
                .Where(ident => !string.IsNullOrEmpty(ident)));

            _lastNearbyIdentSnapshot.Clear();
            foreach (var ident in nearbySet)
            {
                _lastNearbyIdentSnapshot.Add(ident);
            }

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
                _logger.LogTrace(ex, "Failed to retrieve map data while resuming pairing availability subscription");
            }

            _lastNearbyAvailabilityCheck = DateTime.UtcNow;

            await UpdateAvailabilitySubscriptionAsync(
                    location,
                    nearbySet,
                    nearbySet,
                    Array.Empty<string>(),
                    force: true,
                    forceFullSnapshot: true)
                .ConfigureAwait(false);

            await EvaluatePendingRequestsAsync(nearbySet).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to resume pairing availability subscription");
        }
        finally
        {
            _nearbyAvailabilitySemaphore.Release();
        }
    }

    private async Task<bool> WaitForApiConnectionAsync(CancellationToken cancellationToken)
    {
        const int retryCount = 10;
        const int retryDelayMs = 200;

        for (var attempt = 0; attempt < retryCount; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            if (_apiController.Value.IsConnected)
                return true;
        }

        return _apiController.Value.IsConnected;
    }
    
    private async Task StopAvailabilitySubscriptionAsync()
    {
        if (!_availabilitySubscriptionActive)
        {
            _pushChannelAvailable = false;
            _lastSubscriptionLocation = null;
            return;
        }

        await _availabilitySubscriptionSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_apiController.Value.IsConnected)
                await _apiController.Value.UserUnsubscribePairingAvailability().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to unsubscribe from pairing availability push channel");
        }
        finally
        {
            _availabilitySubscriptionActive = false;
            _pushChannelAvailable = false;
            _lastSubscriptionLocation = null;
            _availabilitySubscriptionSemaphore.Release();
        }
    }

    
    public async Task SendPairRequestAsync(string ident)
    {
        if (!_configService.Current.PairingSystemEnabled)
        {
            _logger.LogDebug("Pair request send ignored: pairing system disabled");
            return;
        }

        try
        {
            await _apiController.Value.UserSendPairRequest(new PairingRequestTargetDto(ident)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send pair request to {ident}", ident);
        }
    }

    public async Task RespondAsync(PairingRequestDto request, bool accepted, string? reason = null)
    {
        var note = GetRequesterDisplayName(request);
        await RespondWithDecisionAsync(request.RequestId, accepted, reason).ConfigureAwait(false);

        if (accepted)
        {
            ApplyAutoNote(request, note);
        }
        
        _pendingRequests.TryRemove(request.RequestId, out _);
        Mediator.Publish(new PairingRequestListChangedMessage());
    }
    
    public Task RespondAsync(Guid requestId, bool accepted, string? reason = null)
    {
        if (_pendingRequests.TryGetValue(requestId, out var request))
            return RespondAsync(request.Request, accepted, reason);
        
        return RespondWithDecisionAsync(requestId, accepted, reason);
    }

    private async Task RespondWithDecisionAsync(Guid requestId, bool accepted, string? reason)
    {
        try
        {
            await _apiController.Value
                .UserRespondToPairRequest(new PairingRequestDecisionDto(requestId, accepted, reason))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to respond to request {requestId}", requestId);
        }
    }

    public void ReceiveRequest(PairingRequestDto dto)
    {
        _ = HandleRequestAsync(dto);
    }

    private async Task HandleRequestAsync(PairingRequestDto dto)
    {
        var autoRejectResult = await ShouldAutoRejectAsync(dto.RequesterIdent).ConfigureAwait(false);
        if (autoRejectResult.ShouldReject)
        {
            await RespondAsync(dto.RequestId, false, autoRejectResult.Reason).ConfigureAwait(false);
            return;
        }

        _pendingRequests[dto.RequestId] = new PendingRequest(dto, autoRejectResult.WasDeferred);
        Mediator.Publish(new PairingRequestReceivedMessage(dto));
        Mediator.Publish(new PairingRequestListChangedMessage());
        var requesterName = GetRequesterDisplayName(dto, setNoteFromNearby: true);
        _toastGui.ShowQuest(requesterName + " sent a pairing request.");
    }

    public RequesterDisplay GetRequesterDisplay(PairingRequestDto dto, bool setNoteFromNearby = false)
    {
        var resolved = TryResolveRequester(dto, setNoteFromNearby);
        return new RequesterDisplay(resolved.Name ?? dto.Requester.UID, resolved.WorldId);
    }
    
    public string GetRequesterDisplayName(PairingRequestDto dto, bool setNoteFromNearby = false)
    {
        return GetRequesterDisplay(dto, setNoteFromNearby).NameOrUid;
    }

    private RequesterDisplay TryResolveRequester(PairingRequestDto dto, bool setNoteFromNearby)
    {
        var pc = _dalamudUtilService.FindPlayerByNameHash(dto.RequesterIdent);
        if (pc.ObjectId != 0 && pc.Address != IntPtr.Zero && !string.IsNullOrWhiteSpace(pc.Name))
        {
            var name = pc.Name;
            var world = (ushort?)pc.HomeWorldId;
            _serverConfigurationManager.SetNameForUid(dto.Requester.UID, name);
            if (setNoteFromNearby && string.IsNullOrWhiteSpace(_serverConfigurationManager.GetNoteForUid(dto.Requester.UID)))
            {
                _serverConfigurationManager.SetNoteForUid(dto.Requester.UID, name);
            }

            return new RequesterDisplay(name, world);
        }

        return new RequesterDisplay(null, null);
        
        
    }

    private Task RebuildAvailabilityFiltersAsync()
    {
        var existing = _availableIdents.ToArray();
        _availabilityFilterCts?.Cancel();
        _availabilityFilterCts = new CancellationTokenSource();
        var token = _availabilityFilterCts.Token;

        return Task.Run(async () =>
        {
            var filtered = new HashSet<string>(StringComparer.Ordinal);
            var accepted = new HashSet<string>(StringComparer.Ordinal);

            foreach (var ident in existing)
            {
                if (token.IsCancellationRequested)
                    return;

                var autoRejectResult = await ShouldAutoRejectAsync(ident, deferIfUnavailable: false).ConfigureAwait(false);
                if (autoRejectResult.ShouldReject)
                    filtered.Add(ident);
                else
                    accepted.Add(ident);
            }

            lock (_availabilityFilterLock)
            {
                if (token.IsCancellationRequested)
                    return;

                _filteredAvailableIdents = filtered;
                _unfilteredAvailableIdents = accepted;
            }

            Mediator.Publish(new PairingAvailabilityChangedMessage());
        });
    }

    private void OnConfigSave(object? sender, EventArgs e)
    {
        _ = RebuildAvailabilityFiltersAsync();
    }
    
    private void ApplyAutoNote(PairingRequestDto request, string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;

        if (_serverConfigurationManager.GetNoteForUid(request.Requester.UID) != null)
            return;

        _serverConfigurationManager.SetNoteForUid(request.Requester.UID, note);
    }

    private async Task EvaluatePendingRequestsAsync(HashSet<string> nearbySet)
    {
        foreach (var pending in _pendingRequests.Values)
        {
            var request = pending.Request;
            if (!nearbySet.Contains(request.RequesterIdent))
                continue;

            var autoRejectResult = await ShouldAutoRejectAsync(request.RequesterIdent, deferIfUnavailable: false)
                .ConfigureAwait(false);

            if (!autoRejectResult.ShouldReject)
                continue;

            var reason = autoRejectResult.Reason;
            if (pending.DeferredAutoFilter)
            {
                await RespondAsync(request, false, reason: null).ConfigureAwait(false);
                continue;
            }

            await RespondAsync(request, false, reason).ConfigureAwait(false);
            var requesterName = GetRequesterDisplayName(request);
            var message = $"{requesterName}'s pending pairing request was auto-rejected after they came into range and were found to match your filters.";

            _toastGui.ShowNormal(message);
        }
    }

    private async Task<AutoRejectResult> ShouldAutoRejectAsync(string ident, bool deferIfUnavailable = true)
    {
        if (!_configService.Current.PairingSystemEnabled)
            return new AutoRejectResult(false, string.Empty, false);
        
        var rejectedHomeworlds = _configService.Current.PairRequestRejectedHomeworlds;
        var hasAppearanceFilters = _configService.Current.AutoRejectCombos.Count > 0;
        var hasHomeworldFilters = rejectedHomeworlds.Count > 0;
        var minimumLevel = Math.Max(0, _configService.Current.PairRequestMinimumLevel);
        if (!hasAppearanceFilters && minimumLevel == 0 && !hasHomeworldFilters)
            return new AutoRejectResult(false, string.Empty, false);
        
        var pc = _dalamudUtilService.FindPlayerByNameHash(ident);
        if (pc.ObjectId == 0 || pc.Address == IntPtr.Zero)
            return deferIfUnavailable
                ? new AutoRejectResult(false, string.Empty, true)
                : new AutoRejectResult(true, "Auto rejected: requester unavailable for filtering", false);
        
        if (minimumLevel > 0)
        {
            if (pc.Level <= 0)
                return deferIfUnavailable
                    ? new AutoRejectResult(false, string.Empty, true)
                    : new AutoRejectResult(true, "Auto rejected: requester level unavailable", false);

            
            if (pc.Level < minimumLevel)
                return new AutoRejectResult(true, $"Auto rejected: This user isn't interested in pairing with users below level {minimumLevel}.", false);
        }

        if (hasHomeworldFilters)
        {
            if (pc.HomeWorldId == 0)
                return deferIfUnavailable
                    ? new AutoRejectResult(false, string.Empty, true)
                    : new AutoRejectResult(true, "Auto rejected: requester homeworld unavailable", false);

            var homeworldId = (ushort)pc.HomeWorldId;
            if (rejectedHomeworlds.Contains(homeworldId))
            {
                var homeworldName = _dalamudUtilService.WorldData.Value.GetValueOrDefault(homeworldId, homeworldId.ToString());
                return new AutoRejectResult(true, $"Auto rejected: This user isn't interested in pairing with users from {homeworldName}.", false);
            }
        }

        var appearance = await ExtractAppearanceAsync(pc.Address).ConfigureAwait(false);
        if (appearance == null)
            return hasAppearanceFilters
                ? deferIfUnavailable
                    ? new AutoRejectResult(false, string.Empty, true)
                    : new AutoRejectResult(true, "Auto rejected: appearance unavailable", false)
                : new AutoRejectResult(false, string.Empty, false);
     
        if (appearance.Gender.HasValue && appearance.Race.HasValue && appearance.Clan.HasValue)
        {
            var key = new AutoRejectCombo(appearance.Race.Value, appearance.Clan.Value, appearance.Gender.Value);
            if (_configService.Current.AutoRejectCombos.Contains(key))
                return new AutoRejectResult(true, "Auto rejected: This user isn't interested in your vanilla gender/clan combination.", false);
        }
        

        return hasAppearanceFilters
            ? new AutoRejectResult(true, "Auto rejected: appearance unavailable", false)
            : new AutoRejectResult(false, string.Empty, false);

        
    }

    private record DecodedAppearance(byte? Gender, byte? Race, byte? Clan, string RawBase64, string? DecodedJson, string? DecodeNotes);
    private const int CustomizeDataLength = 0x1A;
    private enum CustomizeIndex : byte
    {
        Race = 0,
        Gender = 1,
        Tribe = 4,
    }
    
    private async Task<DecodedAppearance?> ExtractAppearanceAsync(IntPtr characterAddress)
    {
        if (TryExtractAppearanceFromGameData(characterAddress, out var appearance))
        {
            _logger.LogInformation("Extracted appearance from game.");
            return appearance;

        }
        // If for some reason that fails, try Glamourer
        try
        {
            var glamourerState = await _ipcManager.Glamourer.GetCharacterCustomizationAsync(characterAddress).ConfigureAwait(false);
            if (string.IsNullOrEmpty(glamourerState))
                return null;

            var decoded = TryDecodeGlamourerState(glamourerState, out var gender, out var race, out var clan, out var decodeNotes);
            
            return new DecodedAppearance(gender, race, clan, glamourerState, decoded, decodeNotes);
            
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to extract appearance data");
            return null;
        }
    }
    
    private unsafe bool TryExtractAppearanceFromGameData(IntPtr characterAddress, out DecodedAppearance? appearance)
    {
        appearance = null;

        try
        {
            if (characterAddress == IntPtr.Zero)
                return false;

            var chara = (BattleChara*)characterAddress;
            if (chara == null)
                return false;

            var customizeData = MemoryMarshal.CreateReadOnlySpan(ref chara->DrawData.CustomizeData.Data[0], CustomizeDataLength);

            byte? gender = GetCustomizeValue(customizeData, CustomizeIndex.Gender);
            byte? race = GetCustomizeValue(customizeData, CustomizeIndex.Race);
            byte? tribe = GetCustomizeValue(customizeData, CustomizeIndex.Tribe);

            if (gender == null && race == null && tribe == null)
                return false;

            appearance = new DecodedAppearance(gender, race, tribe, string.Empty, null, "read-from-game");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to read appearance from game data");
            return false;
        }
    }

    private static byte? GetCustomizeValue(ReadOnlySpan<byte> customizeData, CustomizeIndex index)
    {
        var idx = (int)index;
        return idx < customizeData.Length ? customizeData[idx] : null;
    }

    private string? TryDecodeGlamourerState(string glamourerStateBase64, out byte? gender, out byte? race, out byte? clan, out string? decodeNotes)
    {
        gender = null;
        race = null;
        clan = null;
        decodeNotes = null;

        try
        {
            var rawBytes = Convert.FromBase64String(glamourerStateBase64);

            // Glamourer encodes its state as a GZip stream prefixed with a single version byte.
            // Strip the prefix if present so the gzip header (0x1F 0x8B) is the first byte before decoding to JSON.
            var gzipStart = Array.IndexOf(rawBytes, (byte)0x1F);
            if (gzipStart < 0 || gzipStart + 1 >= rawBytes.Length || rawBytes[gzipStart + 1] != 0x8B)
            {
                decodeNotes = "No gzip header found";
                return null;
            }

            using var memory = new MemoryStream(rawBytes, gzipStart, rawBytes.Length - gzipStart);
            using var gzip = new System.IO.Compression.GZipStream(memory, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var decoded = reader.ReadToEnd();

            decodeNotes = "decoded";
            TryParseAppearanceFromJson(decoded, out gender, out race, out clan, ref decodeNotes);

            return decoded;
        }
        catch (Exception ex)
        {
            decodeNotes = $"decode-failed: {ex.GetType().Name}";
            _logger.LogTrace(ex, "Failed to parse Glamourer state for appearance");
            return null;
        }
    }
    
    private void TryParseAppearanceFromJson(string decoded, out byte? gender, out byte? race, out byte? clan, ref string? decodeNotes)
    {
        gender = null;
        race = null;
        clan = null;

        try
        {
            using var document = JsonDocument.Parse(decoded);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                decodeNotes = "decode-ok:no-root-object";
                return;
            }

            if (!document.RootElement.TryGetProperty("Customize", out var customize) || customize.ValueKind != JsonValueKind.Object)
            {
                decodeNotes = "decode-ok:no-customize";
                return;
            }

            gender = ExtractByteFrom(customize, "Gender");
            race = ExtractByteFrom(customize, "Race");
            clan = ExtractByteFrom(customize, "Clan");

            decodeNotes = (gender, race, clan) switch
            {
                (null, null, null) => "decode-ok:customize-empty",
                _ => "decode-ok:customize-parsed"
            };
        }
        catch (Exception ex)
        {
            decodeNotes = $"decode-json-failed:{ex.GetType().Name}";
            _logger.LogTrace(ex, "Failed to parse Glamourer JSON");
        }
    }

    private static byte? ExtractByteFrom(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element)) return null;
        return ExtractByteValue(element);
    }

    private static byte? ExtractByteValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Value", out var valueElement))
            return ExtractByteValue(valueElement);

        if (element.ValueKind == JsonValueKind.Number && element.TryGetByte(out var numberValue))
            return numberValue;

        if (element.ValueKind == JsonValueKind.String && byte.TryParse(element.GetString(), out var parsedValue))
            return parsedValue;

        return null;
    }

    
    
    private void OnTargetPlayerChanged(TargetPlayerChangedMessage message)
    {
        if (message.Character is not IPlayerCharacter playerCharacter)
            return;

        _ = LogTargetAppearanceAsync(playerCharacter);
    }

    private async Task LogTargetAppearanceAsync(IPlayerCharacter playerCharacter)
    {
        var appearance = await ExtractAppearanceAsync(playerCharacter.Address).ConfigureAwait(false);

        var name = playerCharacter.Name.ToString();
        var worldId = playerCharacter.HomeWorld.RowId;

        if (appearance == null)
        {
            _logger.LogDebug($"Targeted {name}@{worldId}: appearance could not be read");
            return;
        }

        var genderDisplay = appearance.Gender?.ToString() ?? "unknown";
        var raceDisplay = appearance.Race?.ToString() ?? "unknown";
        var clanDisplay = appearance.Clan?.ToString() ?? "unknown";
        var decodeNote = appearance.DecodeNotes ?? "decoded";

        _logger.LogDebug(
            $"Targeted {name}@{worldId}: detected gender={genderDisplay}, race={raceDisplay}, clan={clanDisplay}, rawBase64={appearance.RawBase64}, decodedJson={appearance.DecodedJson ?? "(decode failed)"}, notes={decodeNote}");
    }
}

public readonly record struct RequesterDisplay(string? Name, ushort? WorldId)
{
    public string NameOrUid => Name ?? string.Empty;
}

public readonly record struct AvailabilityFilterSnapshot(IReadOnlyCollection<string> Accepted, int FilteredCount)
{
    public int AcceptedCount => Accepted.Count;
}