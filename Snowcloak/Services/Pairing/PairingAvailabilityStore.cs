using System.Globalization;
using System.Numerics;
using Dalamud.Game.Player;
using ElezenTools.Data;
using ElezenTools.Services;
using ElezenTools.UI.Mvu;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Configurations;
using ElezenTools.Core.Async;
using Snowcloak.Core.Pairing;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using ElezenPlayerCharacterData = ElezenTools.Data.Classes.PlayerCharacterData;

namespace Snowcloak.Services.Pairing;

internal delegate void CacheCharacterSnapshot(string ident, string? name, uint homeWorldId,
    uint classJobId, short level, Sex sex, uint raceId, uint tribeId);

internal sealed class PairingAvailabilityStore : Store<AvailabilityViewState>, IDisposable
{
    private static readonly Action<ILogger, Exception?> LogAvailabilityFilterRebuildCancelled =
        LoggerMessage.Define(LogLevel.Trace, new EventId(1, nameof(LogAvailabilityFilterRebuildCancelled)),
            "Availability filter rebuild cancelled");
    private static readonly Action<ILogger, Exception?> LogAvailabilityStateRefreshCancelled =
        LoggerMessage.Define(LogLevel.Trace, new EventId(2, nameof(LogAvailabilityStateRefreshCancelled)),
            "Availability state refresh cancelled");
    private static readonly Vector4 MutedGrey = new(0.7f, 0.7f, 0.7f, 1f);

    private readonly ILogger _logger;
    private readonly SnowcloakConfigService _configService;
    private readonly SnowProfileManager _profileManager;
    private readonly SnowMediator _mediator;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly CacheCharacterSnapshot _cacheSnapshot;
    private readonly Func<string, bool, Task<PairRequestFilterResult>> _filter;
    private readonly PairingAvailabilitySet _availability = new();
    private readonly Lock _availabilityLock = new();
    private readonly Lock _filterLock = new();
    private readonly Lock _pendingLock = new();
    private readonly SingleFlightCts _filterRebuild = new();
    private readonly SingleFlightCts _stateRefresh = new();
    private HashSet<string> _filteredAvailableIdents = new(StringComparer.Ordinal);
    private HashSet<string> _unfilteredAvailableIdents = new(StringComparer.Ordinal);
    private List<PendingPairRequestRow> _pendingRequests = [];
    private string _localPlayerIdent = string.Empty;
    private bool _availabilityChannelActive;
    private bool _locked;
    private AvailabilityViewState? _lockedState;
    private int _disposed;

    public PairingAvailabilityStore(ILogger logger, SnowcloakConfigService configService,
        SnowProfileManager profileManager, SnowMediator mediator, BackgroundTaskTracker backgroundTasks,
        DalamudUtilService dalamudUtil, CacheCharacterSnapshot cacheSnapshot,
        Func<string, bool, Task<PairRequestFilterResult>> filter)
        : base(AvailabilityViewState.Empty)
    {
        _logger = logger;
        _configService = configService;
        _profileManager = profileManager;
        _mediator = mediator;
        _backgroundTasks = backgroundTasks;
        _dalamudUtil = dalamudUtil;
        _cacheSnapshot = cacheSnapshot;
        _filter = filter;
    }

    public IReadOnlyCollection<string> AvailableIdents
    {
        get
        {
            lock (_availabilityLock)
            {
                return _availability.ToSnapshot();
            }
        }
    }

    public void SetLocalPlayerIdent(string ident)
    {
        var normalized = string.IsNullOrWhiteSpace(ident) ? string.Empty : ident;
        if (string.Equals(_localPlayerIdent, normalized, StringComparison.Ordinal))
            return;

        _localPlayerIdent = normalized;
        RequestStateRefresh();
    }

    public bool Contains(string ident)
    {
        lock (_availabilityLock)
        {
            return _availability.Contains(ident);
        }
    }

    public AvailabilityFilterSnapshot GetFilterSnapshot()
    {
        lock (_filterLock)
        {
            return new AvailabilityFilterSnapshot(
                new List<string>(_unfilteredAvailableIdents),
                _filteredAvailableIdents.Count);
        }
    }

    public void UpdateFromProfiles(IEnumerable<PairingAvailabilityDto> available,
        IReadOnlyCollection<string>? authoritativeScope, IReadOnlyCollection<string> nearbySnapshot,
        IEnumerable<Pair> directPairs, bool publishImmediately)
    {
        var entries = available?.ToList() ?? [];
        _profileManager.UpdateSummaries(entries);
        var incoming = entries.Select(dto => dto.Ident)
            .Where(ident => !string.IsNullOrWhiteSpace(ident))
            .ToHashSet(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(_localPlayerIdent))
            incoming.Remove(_localPlayerIdent);

        incoming.ExceptWith(directPairs
            .Where(pair => !string.IsNullOrEmpty(pair.GetPlayerNameHash()))
            .Select(pair => pair.Ident)
            .Where(ident => !string.IsNullOrEmpty(ident)));

        if (nearbySnapshot.Count > 0)
            incoming.IntersectWith(nearbySnapshot);

        var unavailable = authoritativeScope != null
            ? authoritativeScope.Where(ident => !incoming.Contains(ident))
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        ApplyDelta(incoming, unavailable, publishImmediately);
    }

    public void ApplyProfileDelta(IEnumerable<PairingAvailabilityDto> availableProfiles,
        IReadOnlyCollection<string>? unavailableIdents, bool publishImmediately)
    {
        var profiles = availableProfiles?.ToList() ?? [];
        _profileManager.UpdateSummaries(profiles);
        ApplyDelta(profiles.Select(profile => profile.Ident), unavailableIdents, publishImmediately);
    }

    public void ApplyDelta(IEnumerable<string>? availableIdents,
        IEnumerable<string>? unavailableIdents = null, bool publishImmediately = true)
    {
        if (!_configService.Current.PairingSystemEnabled)
        {
            Clear(publishImmediately);
            return;
        }

        var removals = unavailableIdents?
            .Where(ident => !string.IsNullOrWhiteSpace(ident))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        foreach (var ident in removals)
        {
            _profileManager.ClearSummary(ident);
        }

        bool changed;
        lock (_availabilityLock)
        {
            changed = _availability.ApplyDelta(availableIdents, removals, _localPlayerIdent);
        }

        if (!changed)
            return;

        _ = RebuildFiltersAsync(publishImmediately);
    }

    public void Clear(bool publishImmediately = true)
    {
        bool changed;
        lock (_availabilityLock)
        {
            changed = _availability.Clear();
        }

        lock (_filterLock)
        {
            changed |= _filteredAvailableIdents.Count > 0 || _unfilteredAvailableIdents.Count > 0;
            _filteredAvailableIdents.Clear();
            _unfilteredAvailableIdents.Clear();
        }

        if (changed)
            RequestStateRefresh(publishImmediately);
    }

    public Task RefreshProfileSummaryAsync(string ident)
    {
        if (string.IsNullOrWhiteSpace(ident))
            return Task.CompletedTask;

        if (!Contains(ident))
            return Task.CompletedTask;

        return _backgroundTasks.Run(async () =>
        {
            await _profileManager.RefreshSummaryAsync(ident).ConfigureAwait(false);
            _ = RebuildFiltersAsync(publishImmediately: true);
        }, nameof(RefreshProfileSummaryAsync));
    }

    public Task RebuildFiltersAsync(bool publishImmediately = true)
    {
        IReadOnlyCollection<string> existing;
        lock (_availabilityLock)
        {
            existing = _availability.ToSnapshot();
        }

        if (Volatile.Read(ref _disposed) != 0)
            return Task.CompletedTask;

        var scope = _filterRebuild.Begin();
        return _backgroundTasks.Run(async () =>
        {
            using (scope)
            {
                try
                {
                    var filtered = new HashSet<string>(StringComparer.Ordinal);
                    var accepted = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var ident in existing)
                    {
                        if (scope.Token.IsCancellationRequested)
                            return;

                        var result = await _filter(ident, false).ConfigureAwait(false);
                        if (result.ShouldReject)
                            filtered.Add(ident);
                        else
                            accepted.Add(ident);
                    }

                    lock (_filterLock)
                    {
                        if (scope.Token.IsCancellationRequested)
                            return;

                        _filteredAvailableIdents = filtered;
                        _unfilteredAvailableIdents = accepted;
                    }

                    RequestStateRefresh(publishImmediately);
                }
                catch (OperationCanceledException) when (scope.Token.IsCancellationRequested)
                {
                    LogAvailabilityFilterRebuildCancelled(_logger, null);
                }
            }
        }, nameof(RebuildFiltersAsync));
    }
    
    public void SetLocked(bool locked)
    {
        if (_locked == locked)
            return;

        _locked = locked;
        if (!locked)
            _lockedState = null;

        RequestStateRefresh(captureLock: locked);
    }

    public void RecaptureLockIfLocked()
    {
        if (!_locked)
            return;

        RequestStateRefresh(captureLock: true);
    }

    public void SetSearchQuery(string? query) => UpdateFilterConfig(c => c.FrostbrandProfileSearch = query ?? string.Empty);

    public void SetTagQuery(string? query) => UpdateFilterConfig(c => c.FrostbrandRequiredTag = query ?? string.Empty);

    public void SetOnlyWithProfiles(bool value) => UpdateFilterConfig(c => c.FrostbrandOnlyWithProfiles = value);

    public void SetUseProfileCards(bool value) => UpdateFilterConfig(c => c.FrostbrandUseProfileCards = value);

    public void SetPendingRequests(IReadOnlyList<PendingPairRequestRow> pendingRequests)
    {
        ArgumentNullException.ThrowIfNull(pendingRequests);

        lock (_pendingLock)
        {
            if (_pendingRequests.SequenceEqual(pendingRequests))
                return;

            _pendingRequests = [.. pendingRequests];
        }

        RequestStateRefresh(publishImmediately: true);
    }

    public void SetAvailabilityChannelActive(bool active)
    {
        lock (_pendingLock)
        {
            if (_availabilityChannelActive == active)
                return;

            _availabilityChannelActive = active;
        }

        RequestStateRefresh();
    }

    private void UpdateFilterConfig(Action<SnowcloakConfig> mutate)
    {
        _configService.Update(mutate);
        RequestStateRefresh();
    }

    public void RefreshState() => RequestStateRefresh();

    private void RequestStateRefresh(bool publishImmediately = false, bool captureLock = false)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var scope = _stateRefresh.Begin();
        _ = _backgroundTasks.Run(async () =>
        {
            using (scope)
            {
                try
                {
                    var state = await BuildStateAsync(captureLock, scope.Token).ConfigureAwait(false);
                    if (scope.Token.IsCancellationRequested)
                        return;

                    SetState(state);
                    if (publishImmediately)
                        _mediator.Publish(new PairingAvailabilityChangedMessage());
                }
                catch (OperationCanceledException) when (scope.Token.IsCancellationRequested)
                {
                    LogAvailabilityStateRefreshCancelled(_logger, null);
                }
            }
        }, nameof(RequestStateRefresh));
    }

    private async Task<AvailabilityViewState> BuildStateAsync(bool captureLock, CancellationToken token)
    {
        if (_locked && _lockedState != null && !captureLock)
            return ApplyLiveOverlay(_lockedState);

        var fresh = await Service.RunOnFrameworkAsync(BuildState).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        if (captureLock && _locked)
            _lockedState = fresh;

        return _locked && _lockedState != null ? ApplyLiveOverlay(_lockedState) : fresh;
    }

    private AvailabilityViewState BuildState()
    {
        var snapshot = GetFilterSnapshot();
        var viewerTags = GetViewerProfileTags();
        var pending = GetPendingSnapshot();

        var rows = new List<AvailabilityRow>(snapshot.Accepted.Count);
        foreach (var ident in snapshot.Accepted)
        {
            var pc = _dalamudUtil.FindPlayerByNameHash(ident);
            if (pc.EntityId == 0 || pc.Address == IntPtr.Zero)
                continue;

            _cacheSnapshot(ident, pc.Name, pc.HomeWorldId, pc.ClassJobId, pc.Level, pc.Sex, pc.RaceId, pc.TribeId);
            rows.Add(BuildRow(ident, pc, viewerTags));
        }

        rows.Sort(static (left, right) => string.CompareOrdinal(left.CharacterName, right.CharacterName));

        var config = _configService.Current;
        var visible = AvailabilityFilter.Apply(rows, config.FrostbrandOnlyWithProfiles,
            config.FrostbrandProfileSearch, config.FrostbrandRequiredTag);

        return new AvailabilityViewState(
            VisibleRows: visible,
            TotalCount: rows.Count,
            AutoRejectedCount: snapshot.FilteredCount,
            Locked: _locked,
            PairingEnabled: config.PairingSystemEnabled,
            AvailabilityChannelActive: pending.ChannelActive,
            UseProfileCards: config.FrostbrandUseProfileCards,
            OnlyWithProfiles: config.FrostbrandOnlyWithProfiles,
            SearchQuery: config.FrostbrandProfileSearch ?? string.Empty,
            TagQuery: config.FrostbrandRequiredTag ?? string.Empty,
            PendingRequests: pending.Requests);
    }

    private AvailabilityViewState ApplyLiveOverlay(AvailabilityViewState state)
    {
        var pending = GetPendingSnapshot();
        var config = _configService.Current;
        return state with
        {
            PairingEnabled = config.PairingSystemEnabled,
            AvailabilityChannelActive = pending.ChannelActive,
            UseProfileCards = config.FrostbrandUseProfileCards,
            OnlyWithProfiles = config.FrostbrandOnlyWithProfiles,
            SearchQuery = config.FrostbrandProfileSearch ?? string.Empty,
            TagQuery = config.FrostbrandRequiredTag ?? string.Empty,
            PendingRequests = pending.Requests,
        };
    }

    private (IReadOnlyList<PendingPairRequestRow> Requests, bool ChannelActive) GetPendingSnapshot()
    {
        lock (_pendingLock)
        {
            return ([.. _pendingRequests], _availabilityChannelActive);
        }
    }

    private AvailabilityRow BuildRow(string ident, ElezenPlayerCharacterData pc,
        IReadOnlyList<UserProfileTagDto> viewerTags)
    {
        var profile = _profileManager.GetSummary(ident);
        var characterName = string.IsNullOrWhiteSpace(pc.Name) ? "Unnamed character" : pc.Name;
        var displayName = string.IsNullOrWhiteSpace(profile?.CharacterName) ? characterName : profile.CharacterName;
        var homeWorldId = pc.HomeWorldId is > 0 and <= ushort.MaxValue ? (ushort?)pc.HomeWorldId : null;

        return new AvailabilityRow(
            Ident: ident,
            DisplayName: displayName,
            CharacterName: characterName,
            Status: string.IsNullOrWhiteSpace(profile?.RpStatus) ? "Open to pairing" : profile.RpStatus,
            GenderText: GenderText(pc.Sex),
            TribeName: ResolveTribeName(pc.TribeId),
            RaceName: RaceName(pc.TribeId),
            ClassName: ClassName(pc.ClassJobId),
            ClassColor: ElezenData.Jobs.GetById(pc.ClassJobId)?.ClassColour ?? MutedGrey,
            Level: pc.Level,
            LevelText: pc.Level > 0 ? pc.Level.ToString(CultureInfo.InvariantCulture) : "-",
            HomeWorldId: homeWorldId,
            HomeWorldName: ResolveWorldName(homeWorldId),
            Profile: profile,
            VisibleTags: ProfileTagUtilities.GetVisibleTagsForViewer(profile?.Tags, viewerTags));
    }

    private IReadOnlyList<UserProfileTagDto> GetViewerProfileTags()
    {
        var ownProfile = _profileManager.GetOwnProfile(ProfileVisibility.Private);
        return ownProfile.Revision > 0 ? ownProfile.Tags : [];
    }

    private string ResolveWorldName(ushort? homeWorldId)
        => homeWorldId.HasValue && _dalamudUtil.WorldData.TryGetValue(homeWorldId.Value, out var world)
            ? world
            : "-";

    private string ResolveTribeName(uint tribeId)
        => tribeId is > 0 and <= byte.MaxValue && _dalamudUtil.TribeNames.TryGetValue((byte)tribeId, out var tribe)
            ? tribe
            : "-";

    private static string GenderText(Sex sex) => sex switch
    {
        Sex.Male => "Male",
        Sex.Female => "Female",
        _ => "-",
    };

    private static string RaceName(uint tribeId) => tribeId switch
    {
        1 or 2 => "Hyur",
        3 or 4 => "Elezen",
        5 or 6 => "Lalafell",
        7 or 8 => "Miqo'te",
        9 or 10 => "Roegadyn",
        11 or 12 => "Au Ra",
        13 or 14 => "Hrothgar",
        15 or 16 => "Viera",
        _ => "-",
    };

    private static string ClassName(uint classJobId) => classJobId switch
    {
        1 => "GLA", 2 => "PGL", 3 => "MRD", 4 => "LNC", 5 => "ARC", 6 => "CNJ", 7 => "THM",
        8 => "CRP", 9 => "BSM", 10 => "ARM", 11 => "GSM", 12 => "LTW", 13 => "WVR", 14 => "ALC",
        15 => "CUL", 16 => "MIN", 17 => "BTN", 18 => "FSH", 19 => "PLD", 20 => "MNK", 21 => "WAR",
        22 => "DRG", 23 => "BRD", 24 => "WHM", 25 => "BLM", 26 => "ACN", 27 => "SMN", 28 => "SCH",
        29 => "ROG", 30 => "NIN", 31 => "MCH", 32 => "DRK", 33 => "AST", 34 => "SAM", 35 => "RDM",
        36 => "BLU", 37 => "GNB", 38 => "DNC", 39 => "RPR", 40 => "SGE", 41 => "VPR", 42 => "PCT",
        _ => "-",
    };

    public void Cancel() => _filterRebuild.Cancel();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _filterRebuild.Dispose();
        _stateRefresh.Dispose();
    }
}
