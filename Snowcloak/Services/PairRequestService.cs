using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Player;
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
using Snowcloak.Core.Pairing;
using Snowcloak.Services.Pairing;
using Snowcloak.Utils;
using System.Globalization;

namespace Snowcloak.Services;

public class PairRequestService : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private readonly ILogger<PairRequestService> _logger;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly SnowcloakConfigService _configService;
    private readonly PairingFilterConfigService _filterConfigService;
    private readonly Lazy<ApiController> _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IpcManager _ipcManager;
    private readonly PairManager _pairManager;
    private readonly SnowProfileManager _snowProfileManager;
    private readonly ConcurrentDictionary<string, PairRequesterCharacterSnapshot> _requesterCharacterSnapshots = new(StringComparer.Ordinal);
    private readonly IContextMenu _contextMenu;
    private readonly NotesStore _notesStore;
    private readonly PairingAvailabilityStore _availabilityStore;
    private readonly AvailabilitySubscriptionClient _availabilitySubscription;
    private readonly NearbyPresenceScanner _nearbyPresenceScanner;
    private readonly PairRequestInbox _requestInbox;
    private bool _advertisingPairing;
    private int _disposed;
    private bool _lastPairingEnabled;

    public PairRequestService(ILogger<PairRequestService> logger, SnowcloakConfigService configService,
        PairingFilterConfigService filterConfigService,
        SnowMediator mediator, DalamudUtilService dalamudUtilService,
        IpcManager ipcManager, IToastGui toastGui, IChatGui chatGui, IContextMenu contextMenu, IServiceProvider serviceProvider,
        NotesStore notesStore, PairManager pairManager, SnowProfileManager snowProfileManager)
        : base(logger, mediator)
    {
        _logger = logger;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _configService = configService;
        _filterConfigService = filterConfigService;
        _apiController = new Lazy<ApiController>(() => serviceProvider.GetRequiredService<ApiController>());
        _dalamudUtilService = dalamudUtilService;
        _ipcManager = ipcManager;
        _contextMenu = contextMenu;
        _pairManager = pairManager;
        _snowProfileManager = snowProfileManager;
        _notesStore = notesStore;
        _availabilityStore = new PairingAvailabilityStore(logger, configService, snowProfileManager, mediator,
            _backgroundTasks, dalamudUtilService, CacheRequesterCharacterSnapshot, ShouldAutoRejectAsync);
        _availabilitySubscription = new AvailabilitySubscriptionClient(logger, _apiController);
        _requestInbox = new PairRequestInbox(logger, _backgroundTasks, _apiController, mediator, toastGui, chatGui,
            ShouldAutoRejectAsync, GetRequesterDisplay, CacheRequesterSnapshotForInbox, ApplyAutoNote);
        _nearbyPresenceScanner = new NearbyPresenceScanner(logger, configService, _apiController, dalamudUtilService,
            pairManager, _availabilityStore, _availabilitySubscription, _requestInbox.EvaluateAsync);
        _lastPairingEnabled = _configService.Current.PairingSystemEnabled;
        _contextMenu.OnMenuOpened += ContextMenuOnMenuOpened;
        Mediator.Subscribe<TargetPlayerChangedMessage>(this, OnTargetPlayerChanged);
        _configService.ConfigChanged += OnConfigSave;
        
        
        Mediator.Subscribe<DalamudLoginMessage>(this, OnPlayerLoggedIn);
        Mediator.Subscribe<DalamudLogoutMessage>(this, OnPlayerLoggedOut);
        Mediator.Subscribe<ZoneSwitchEndMessage>(this, OnZoneChanged);
        Mediator.Subscribe<ConnectedMessage>(this, OnConnected);
        Mediator.Subscribe<HubReconnectedMessage>(this, OnHubReconnected);
        Mediator.Subscribe<DisconnectedMessage>(this, _ => HandleDisconnect());
        Mediator.Subscribe<PairingRequestListChangedMessage>(this, OnPairingRequestListChanged);
        _ = _backgroundTasks.Run(_nearbyPresenceScanner.RunAsync, nameof(NearbyPresenceScanner.RunAsync));
        RefreshPendingRequestRows();
    }

    private void OnConnected(ConnectedMessage message)
    {
        _ = _backgroundTasks.Run(OnConnectedAsync, nameof(OnConnectedAsync));
    }
    
    private void OnHubReconnected(HubReconnectedMessage message) => _ = _backgroundTasks.Run(OnConnectedAsync, nameof(OnConnectedAsync));
    
    public Task ResumePairingAvailabilitySubscriptionAsync(PairingAvailabilityResumeRequestDto resumeRequest)
    {
        ArgumentNullException.ThrowIfNull(resumeRequest);
        return _nearbyPresenceScanner.ResumeAsync(resumeRequest);
    }

    private void OnPlayerLoggedIn(DalamudLoginMessage message) => _ = _backgroundTasks.Run(OnPlayerLoggedInAsync, nameof(OnPlayerLoggedInAsync));
    private Task OnPlayerLoggedInAsync()
    {
        _nearbyPresenceScanner.ResetLocation();
        _requestInbox.Clear();
        return _nearbyPresenceScanner.RefreshWithRetriesAsync();
    }
    
    private void OnPlayerLoggedOut(DalamudLogoutMessage message) => _ = _backgroundTasks.Run(OnPlayerLoggedOutAsync, nameof(OnPlayerLoggedOutAsync));
    private async Task OnPlayerLoggedOutAsync()
    {
        await _availabilitySubscription.StopAsync().ConfigureAwait(false);
        _availabilityStore.SetAvailabilityChannelActive(false);
        _nearbyPresenceScanner.ClearNearbySnapshot();
        _requestInbox.Clear();
        _availabilityStore.Clear();
    }
    
    private void HandleDisconnect()
    {
        _nearbyPresenceScanner.ResetConnection();
        var unavailable = _availabilityStore.AvailableIdents.ToHashSet(StringComparer.Ordinal);

        if (unavailable.Count > 0)
            _availabilityStore.ApplyDelta(Array.Empty<string>(), unavailable, publishImmediately: true);
    }
    
    private void OnZoneChanged(ZoneSwitchEndMessage message) => _ = _backgroundTasks.Run(OnZoneChangedAsync, nameof(OnZoneChangedAsync));
    private Task OnZoneChangedAsync()
    {
        _nearbyPresenceScanner.ResetLocation();
        return _nearbyPresenceScanner.RefreshAsync(force: true);
    }


    private async Task OnConnectedAsync()
    {
        _nearbyPresenceScanner.ResetConnection();

        await RefreshPairingOptInFromServerAsync().ConfigureAwait(false);
        await SyncAdvertisingAsync(force: true).ConfigureAwait(false);
        await _nearbyPresenceScanner.RefreshWithRetriesAsync().ConfigureAwait(false);
    }
    
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing);
        _contextMenu.OnMenuOpened -= ContextMenuOnMenuOpened;
        _configService.ConfigChanged -= OnConfigSave;
        
        CancelBackgroundWork();
        try
        {
            _availabilitySubscription.StopAsync().Wait(TimeSpan.FromSeconds(1));
            _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(2), nameof(PairRequestService));
        }
        catch (AggregateException ex)
        {
            Logger.LogTrace(ex, "Failed to stop pair request background tasks during synchronous disposal");
        }
        DisposeOwnedResources();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing: true);
        _contextMenu.OnMenuOpened -= ContextMenuOnMenuOpened;
        _configService.ConfigChanged -= OnConfigSave;

        CancelBackgroundWork();
        await _availabilitySubscription.StopAsync().ConfigureAwait(false);
        await _backgroundTasks.StopAsync().ConfigureAwait(false);
        DisposeOwnedResources();
        GC.SuppressFinalize(this);
    }

    public IReadOnlyCollection<string> AvailableIdents
        => _availabilityStore.AvailableIdents;
    
    public AvailabilityFilterSnapshot GetAvailabilityFilterSnapshot()
        => _availabilityStore.GetFilterSnapshot();

    internal PairingAvailabilityStore AvailabilityStore => _availabilityStore;

    public void RefreshAvailableProfileSummary(string ident)
        => _ = _availabilityStore.RefreshProfileSummaryAsync(ident);

    public IReadOnlyCollection<PairingRequestDto> PendingRequests
        => _requestInbox.GetPendingRequests();

    private void OnPairingRequestListChanged(PairingRequestListChangedMessage message)
        => RefreshPendingRequestRows();

    private void RefreshPendingRequestRows()
    {
        var pending = _requestInbox.GetPendingRequests()
            .Where(request => !IsMalformed(request))
            .OrderBy(request => request.RequestedAt)
            .Select(BuildPendingRequestRow)
            .ToList();

        _availabilityStore.SetPendingRequests(pending);
    }

    public PairRequesterCharacterSnapshot? GetRequesterCharacterSnapshot(PairingRequestDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (TryCacheRequesterCharacterSnapshot(dto.RequesterIdent, out var snapshot))
            return snapshot;

        return _requesterCharacterSnapshots.TryGetValue(dto.RequesterIdent, out snapshot)
            ? snapshot
            : null;
    }

    public void CacheRequesterCharacterSnapshot(string ident, string? name, uint homeWorldId, uint classJobId, short level, Sex sex, uint raceId, uint tribeId)
    {
        if (string.IsNullOrWhiteSpace(ident))
            return;

        _requesterCharacterSnapshots[ident] = new PairRequesterCharacterSnapshot(
            ident,
            string.IsNullOrWhiteSpace(name) ? null : name,
            homeWorldId is > 0 and <= ushort.MaxValue ? (ushort)homeWorldId : null,
            classJobId,
            level,
            sex,
            raceId,
            tribeId,
            DateTimeOffset.UtcNow);
    }
    
    public bool IsAvailabilityChannelActive => _availabilitySubscription.IsChannelActive;
    
    private void ContextMenuOnMenuOpened(IMenuOpenedArgs args)
    {
        if (!_configService.Current.EnableRightClickMenus) return;
        if (!_configService.Current.PairingSystemEnabled) return;
        if (args.MenuType == ContextMenuType.Inventory) return;
        if (!PlayerInteractionService.TryGetIdentFromMenuTarget(args, out var ident)) return;
        if (!_availabilityStore.Contains(ident)) return;
        if (_configService.Current.PairRequestFriendsOnly && !_dalamudUtilService.IsFriendByIdent(ident))
            return;

        var connectedPair = GetConnectedPairForIdent(ident);
        
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

        if (connectedPair is { IsVisible: true, IsPaused: false } && connectedPair.HasAnyConnection())
        {
            if (connectedPair.UserPair == null)
                Add("Send Snowcloak Pair Request", _ => RunContextMenuAction(() => SendPairRequestAsync(ident), nameof(SendPairRequestAsync)));
            return;
        }

        Add("Send Snowcloak Pair Request", _ => RunContextMenuAction(() => SendPairRequestAsync(ident), nameof(SendPairRequestAsync)));
        Add("View Snowcloak Profile", _ => RunContextMenuAction(() => RequestProfileAsync(ident), nameof(RequestProfileAsync)));
    }

    private void RunContextMenuAction(Func<Task> action, string operationName)
    {
        _ = _backgroundTasks.Run(action, operationName);
    }

    private Pair? GetConnectedPairForIdent(string ident)
    {
        if (string.IsNullOrWhiteSpace(ident))
            return null;

        return _pairManager.GetOnlineUserPairs()
            .FirstOrDefault(pair => pair.HasAnyConnection()
                && (string.Equals(pair.Ident, ident, StringComparison.Ordinal)
                    || string.Equals(pair.GetPlayerNameHash(), ident, StringComparison.Ordinal)));
    }

    public async Task RequestProfileAsync(string ident)
    {
        try
        {
            var connectedPair = GetConnectedPairForIdent(ident);
            if (connectedPair != null)
            {
                Mediator.Publish(new ProfileOpenStandaloneMessage(connectedPair.UserData, connectedPair, FallbackName: connectedPair.PlayerName));
                return;
            }

            var profile = await _snowProfileManager.GetSnowProfileAsync(ident, ProfileVisibility.Public, forceRefresh: true).ConfigureAwait(false);
            var nearbyPlayer = _dalamudUtilService.FindPlayerByNameHash(ident);
            var fallbackName = string.IsNullOrWhiteSpace(nearbyPlayer.Name) ? null : nearbyPlayer.Name;
            var userData = profile.User ?? new UserData(string.Empty);
            var pair = profile.User == null ? null : _pairManager.GetOrCreateTransientPair(userData);
            Mediator.Publish(new ProfileOpenStandaloneMessage(userData, pair, profile.Visibility, ident, fallbackName));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to request profile for ident {Ident}", ident);
            Mediator.Publish(new NotificationMessage("Profile request failed", "Could not retrieve that profile right now.", NotificationType.Warning, TimeSpan.FromSeconds(5)));
        }
        
    }

    public async Task SyncAdvertisingAsync(bool force = false)
    {
        var advertise = _configService.Current.PairingSystemEnabled;

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

    private async Task RefreshPairingOptInFromServerAsync()
    {
        if (!_apiController.Value.IsConnected)
            return;

        try
        {
            var optIn = await _apiController.Value.UserGetPairingOptIn().ConfigureAwait(false);
            if (_configService.Current.PairingSystemEnabled == optIn)
                return;

            _configService.Update(c => c.PairingSystemEnabled = optIn);

            if (!optIn)
                ClearAvailability();
            else
                _availabilityStore.RefreshState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query pairing opt-in status");
        }
    }

    public void SetPairingSystemEnabled(bool enabled)
    {
        if (_configService.Current.PairingSystemEnabled == enabled)
            return;

        _configService.Update(c => c.PairingSystemEnabled = enabled);
        if (!enabled)
            ClearAvailability();
        else
            _availabilityStore.RefreshState();

        Mediator.Publish(new PairingAvailabilityChangedMessage());
        _ = _backgroundTasks.Run(() => SyncAdvertisingAsync(), nameof(SyncAdvertisingAsync));
    }

    public void UpdateAvailability(IEnumerable<PairingAvailabilityDto> available,
        IReadOnlyCollection<string>? authoritativeScope = null, bool publishImmediately = true)
        => _availabilityStore.UpdateFromProfiles(available, authoritativeScope,
            _nearbyPresenceScanner.GetLastNearbySnapshot(), _pairManager.DirectPairs, publishImmediately);

    public void ApplyAvailabilityDelta(IEnumerable<string> availableIdents,
        IReadOnlyCollection<string>? unavailableIdents = null, bool publishImmediately = true)
        => _availabilityStore.ApplyDelta(availableIdents, unavailableIdents, publishImmediately);

    public void ApplyAvailabilityDelta(IEnumerable<PairingAvailabilityDto> availableProfiles,
        IReadOnlyCollection<string>? unavailableIdents = null, bool publishImmediately = true)
        => _availabilityStore.ApplyProfileDelta(availableProfiles, unavailableIdents, publishImmediately);

    private void ClearAvailability()
        => _availabilityStore.Clear();

    public Task RefreshNearbyAvailabilityAsync(bool force = false)
        => _nearbyPresenceScanner.RefreshAsync(force);

    
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
            _logger.LogWarning(ex, "Failed to send pair request to {Ident}", ident);
        }
    }

    public Task RespondAsync(PairingRequestDto request, bool accepted, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _requestInbox.RespondAsync(request, accepted, reason);
    }

    public Task RespondAsync(Guid requestId, bool accepted, string? reason = null)
        => _requestInbox.RespondAsync(requestId, accepted, reason);

    public Task DeclineAllPendingRequestsAsync(string? reason = null)
        => _requestInbox.DeclineAllPendingRequestsAsync(reason);

    public void ReceiveRequest(PairingRequestDto dto)
        => _requestInbox.Receive(dto);

    public RequesterDisplay GetRequesterDisplay(PairingRequestDto dto, bool setNoteFromNearby = false)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var resolved = TryResolveRequester(dto, setNoteFromNearby);
        return new RequesterDisplay(resolved.Name ?? "Unknown character", resolved.WorldId);
    }

    private PendingPairRequestRow BuildPendingRequestRow(PairingRequestDto dto)
    {
        var requesterData = dto.Requester ?? new UserData(string.Empty);
        var requester = GetRequesterDisplay(dto);
        string? worldName = null;
        var hasWorld = requester.WorldId.HasValue
                       && _dalamudUtilService.WorldData.TryGetValue(requester.WorldId.Value, out worldName);
        var hasIdentName = !string.IsNullOrWhiteSpace(requester.Name)
                           && !string.Equals(requester.Name, requesterData.UID, StringComparison.Ordinal);
        var requesterName = hasIdentName ? requester.Name : null;

        if (hasIdentName && hasWorld && !string.IsNullOrWhiteSpace(worldName))
        {
            requesterName += $" @ {worldName}";
        }

        var requesterUid = !string.IsNullOrWhiteSpace(requesterData.UID)
            ? requesterData.UID
            : dto.RequesterIdent;

        var note = !string.IsNullOrWhiteSpace(requesterUid)
            ? _notesStore.GetNoteForUid(requesterUid!)
            : null;

        var aliasOrUid = !string.IsNullOrWhiteSpace(requesterData.AliasOrUID)
            ? requesterData.AliasOrUID
            : !string.IsNullOrWhiteSpace(requesterUid)
                ? requesterUid
                : dto.RequestId.ToString();

        var displayName = !string.IsNullOrWhiteSpace(note)
            ? note!
            : requesterName ?? aliasOrUid;

        var showAlias = string.IsNullOrWhiteSpace(note)
                        && requesterName != null
                        && !string.Equals(aliasOrUid, displayName, StringComparison.Ordinal);

        var characterSnapshot = GetRequesterCharacterSnapshot(dto);
        return new PendingPairRequestRow(dto.RequestId, dto.RequestedAt, displayName, aliasOrUid, showAlias,
            characterSnapshot, BuildMetadataText(characterSnapshot));
    }

    private static bool IsMalformed(PairingRequestDto dto)
    {
        var uid = dto.Requester?.UID;
        return string.IsNullOrWhiteSpace(dto.RequesterIdent) && string.IsNullOrWhiteSpace(uid);
    }

    private string BuildMetadataText(PairRequesterCharacterSnapshot? snapshot)
    {
        if (snapshot is not { } value)
            return string.Empty;

        var segments = new List<string>();
        if (value.Level > 0)
            segments.Add($"Lv. {value.Level}");

        var gender = GetGenderName(value.Sex);
        var race = GetRaceName(value.RaceId);
        var appearance = string.Join(" ", new[] { gender, race }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (!string.IsNullOrWhiteSpace(appearance))
            segments.Add(appearance);

        if (value.HomeWorldId.HasValue && _dalamudUtilService.WorldData.TryGetValue(value.HomeWorldId.Value, out var world))
            segments.Add(world);

        return string.Join("  |  ", segments);
    }

    private static string GetGenderName(Sex sex)
    {
        return sex switch
        {
            Sex.Male => "Male",
            Sex.Female => "Female",
            _ => string.Empty,
        };
    }

    private static string GetRaceName(uint raceId)
    {
        return raceId switch
        {
            1 => "Hyur",
            2 => "Elezen",
            3 => "Lalafell",
            4 => "Miqo'te",
            5 => "Roegadyn",
            6 => "Au Ra",
            7 => "Hrothgar",
            8 => "Viera",
            _ => string.Empty,
        };
    }
    
    public string GetRequesterDisplayName(PairingRequestDto dto, bool setNoteFromNearby = false)
    {
        return GetRequesterDisplay(dto, setNoteFromNearby).NameOrUid;
    }

    private RequesterDisplay TryResolveRequester(PairingRequestDto dto, bool setNoteFromNearby)
    {
        var pc = _dalamudUtilService.FindPlayerByNameHash(dto.RequesterIdent);
        if (pc.EntityId != 0 && pc.Address != IntPtr.Zero && !string.IsNullOrWhiteSpace(pc.Name))
        {
            CacheRequesterCharacterSnapshot(dto.RequesterIdent, pc.Name, pc.HomeWorldId, pc.ClassJobId, pc.Level, pc.Sex, pc.RaceId, pc.TribeId);
            var name = pc.Name;
            var world = (ushort?)pc.HomeWorldId;
            var requesterUid = string.IsNullOrWhiteSpace(dto.Requester.UID)
                ? dto.RequesterIdent
                : dto.Requester.UID;

            if (!string.IsNullOrWhiteSpace(requesterUid))
            {
                _notesStore.SetNameForUid(requesterUid!, name);
                if (setNoteFromNearby && string.IsNullOrWhiteSpace(_notesStore.GetNoteForUid(requesterUid!)))
                {
                    _notesStore.SetNameForUid(requesterUid!, name);
                    if (setNoteFromNearby && string.IsNullOrWhiteSpace(_notesStore.GetNoteForUid(requesterUid!)))
                    {
                        _notesStore.SetNoteForUid(requesterUid!, name);
                    }
                }
            }

            return new RequesterDisplay(name, world);
        }

        if (_requesterCharacterSnapshots.TryGetValue(dto.RequesterIdent, out var cached))
            return new RequesterDisplay(cached.Name, cached.HomeWorldId);

        var cachedUid = dto.Requester?.UID;
        if (!string.IsNullOrWhiteSpace(cachedUid))
        {
            var cachedName = _notesStore.GetNameForUid(cachedUid);
            if (!string.IsNullOrWhiteSpace(cachedName))
                return new RequesterDisplay(cachedName, null);
        }

        return new RequesterDisplay(null, null);
        
        
    }

    private bool TryCacheRequesterCharacterSnapshot(string ident, out PairRequesterCharacterSnapshot snapshot)
    {
        snapshot = default;
        if (string.IsNullOrWhiteSpace(ident))
            return false;

        var pc = _dalamudUtilService.FindPlayerByNameHash(ident);
        if (pc.EntityId == 0 || pc.Address == IntPtr.Zero)
            return _requesterCharacterSnapshots.TryGetValue(ident, out snapshot);

        CacheRequesterCharacterSnapshot(ident, pc.Name, pc.HomeWorldId, pc.ClassJobId, pc.Level, pc.Sex, pc.RaceId, pc.TribeId);
        return _requesterCharacterSnapshots.TryGetValue(ident, out snapshot);
    }

    private bool CacheRequesterSnapshotForInbox(string ident)
        => TryCacheRequesterCharacterSnapshot(ident, out _);

    private void OnConfigSave()
    {
        var enabled = _configService.Current.PairingSystemEnabled;
        if (_lastPairingEnabled && !enabled)
            _ = _backgroundTasks.Run(() => DeclineAllPendingRequestsAsync(), nameof(DeclineAllPendingRequestsAsync));

        _lastPairingEnabled = enabled;
        _ = _availabilityStore.RebuildFiltersAsync();
    }
    
    private void ApplyAutoNote(PairingRequestDto request, string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;

        if (_notesStore.GetNoteForUid(request.Requester.UID) != null)
            return;

        _notesStore.SetNoteForUid(request.Requester.UID, note);
    }

    private async Task<PairRequestFilterResult> ShouldAutoRejectAsync(string ident, bool deferIfUnavailable = true)
    {
        var settings = GetPairRequestFilterSettings();
        if (!settings.PairingEnabled || !settings.HasAnyFilter)
            return PairRequestFilterResult.Accept;
        
        var pc = _dalamudUtilService.FindPlayerByNameHash(ident);
        if (pc.EntityId == 0 || pc.Address == IntPtr.Zero)
            return PairRequestAutoRejectPolicy.Evaluate(settings, PairRequestFilterCandidate.Unavailable, deferIfUnavailable);

        var isFriend = !settings.FriendsOnly || await _dalamudUtilService.IsFriendByIdentAsync(ident).ConfigureAwait(false);
        var homeWorldId = pc.HomeWorldId is > 0 and <= ushort.MaxValue
            ? (ushort)pc.HomeWorldId
            : (ushort?)null;
        var homeWorldName = homeWorldId.HasValue
            ? _dalamudUtilService.WorldData.GetValueOrDefault(homeWorldId.Value, homeWorldId.Value.ToString(CultureInfo.InvariantCulture))
            : null;
        var appearance = await GetPairRequestAppearanceAsync(settings, pc.Address).ConfigureAwait(false);
        var candidate = new PairRequestFilterCandidate(
            true,
            isFriend,
            pc.Level > 0 ? pc.Level : null,
            homeWorldId,
            homeWorldName,
            appearance);

        return PairRequestAutoRejectPolicy.Evaluate(settings, candidate, deferIfUnavailable);
    }

    private PairRequestFilterSettings GetPairRequestFilterSettings()
    {
        var config = _configService.Current;
        var filterConfig = _filterConfigService.Current;
        return new PairRequestFilterSettings(
            config.PairingSystemEnabled,
            config.PairRequestFriendsOnly,
            config.PairRequestMinimumLevel,
            filterConfig.PairRequestRejectedHomeworlds.ToHashSet(),
            filterConfig.AutoRejectCombos
                .Select(combo => new PairRequestAppearanceFilter(combo.Race, combo.Clan, combo.Gender))
                .ToHashSet());
    }

    private async Task<PairRequestAppearance?> GetPairRequestAppearanceAsync(PairRequestFilterSettings settings, IntPtr characterAddress)
    {
        if (!settings.HasAppearanceFilters)
            return null;

        var appearance = await ExtractAppearanceAsync(characterAddress).ConfigureAwait(false);
        return appearance == null
            ? null
            : new PairRequestAppearance(appearance.Race, appearance.Clan, appearance.Gender);
    }

    private sealed record DecodedAppearance(byte? Gender, byte? Race, byte? Clan, string RawBase64, string? DecodedJson, string? DecodeNotes);
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

        if (element.ValueKind == JsonValueKind.String
            && byte.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
            return parsedValue;

        return null;
    }

    
    
    private void OnTargetPlayerChanged(TargetPlayerChangedMessage message)
    {
        if (message.Character is not IPlayerCharacter playerCharacter)
            return;

        _ = _backgroundTasks.Run(() => LogTargetAppearanceAsync(playerCharacter), nameof(LogTargetAppearanceAsync));
    }

    private async Task LogTargetAppearanceAsync(IPlayerCharacter playerCharacter)
    {
        var appearance = await ExtractAppearanceAsync(playerCharacter.Address).ConfigureAwait(false);

        var name = playerCharacter.Name.ToString();
        var worldId = playerCharacter.HomeWorld.RowId;

        if (appearance == null)
        {
            _logger.LogDebug("Targeted {Name}@{WorldId}: appearance could not be read", name, worldId);
            return;
        }

        var genderDisplay = appearance.Gender?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var raceDisplay = appearance.Race?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var clanDisplay = appearance.Clan?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var decodeNote = appearance.DecodeNotes ?? "decoded";

        _logger.LogDebug(
            "Targeted {Name}@{WorldId}: detected gender={GenderDisplay}, race={RaceDisplay}, clan={ClanDisplay}, rawBase64={AppearanceRawBase64}, decodedJson={AppearanceDecodedJson}, notes={DecodeNote}", name, worldId, genderDisplay, raceDisplay, clanDisplay, appearance.RawBase64, appearance.DecodedJson ?? "(decode failed)", decodeNote);
    }

    private void CancelBackgroundWork()
    {
        _backgroundTasks.StopAccepting();
        _nearbyPresenceScanner.Cancel();
        _availabilityStore.Cancel();
    }

    private void DisposeOwnedResources()
    {
        _nearbyPresenceScanner.Dispose();
        _availabilitySubscription.Dispose();
        _availabilityStore.Dispose();
    }
}

public readonly record struct RequesterDisplay(string? Name, ushort? WorldId)
{
    public string NameOrUid => Name ?? string.Empty;
}

public readonly record struct PairRequesterCharacterSnapshot(
    string Ident,
    string? Name,
    ushort? HomeWorldId,
    uint ClassJobId,
    short Level,
    Sex Sex,
    uint RaceId,
    uint TribeId,
    DateTimeOffset CapturedAt);

public readonly record struct AvailabilityFilterSnapshot(IReadOnlyCollection<string> Accepted, int FilteredCount)
{
    public int AcceptedCount => Accepted.Count;
}
