using Snowcloak.API.Data;
using Snowcloak.API.Data.Comparer;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Factories;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Snowcloak.API.Dto.TemporaryAppearance;

namespace Snowcloak.PlayerData.Pairs;

public sealed class PairManager : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private const int MaxPendingCharacterDataEntries = 256;
    private const int MaxPendingStateEntries = 2048;
    private const string PanicHoldSource = "Panic";
    private static readonly TimeSpan PendingStateLifetime = TimeSpan.FromMinutes(2);
    private static readonly Action<ILogger, string, Exception?> LogTemporaryPairDisposalFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1, nameof(QueuePairDisposal)),
        "Failed to dispose removed temporary appearance pair {Uid}");
    private readonly ConcurrentDictionary<string, Pair> _allClientPairs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<GroupData, GroupFullInfoDto> _allGroups = new(GroupDataComparer.Instance);
    private readonly ConcurrentDictionary<(string Gid, string Uid), PendingGroupPair> _pendingGroupPairs = [];
    private readonly ConcurrentDictionary<string, PendingOnlineUser> _pendingOnlineUsers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCharacterData> _pendingCharacterData = new(StringComparer.Ordinal);
    private readonly SnowcloakConfigService _configurationService;
    private readonly PairFactory _pairFactory;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly Lock _projectionLock = new();
    private List<Pair> _directPairsCache = [];
    private Dictionary<GroupFullInfoDto, List<Pair>> _groupPairsCache = new();
    private volatile bool _projectionsDirty = true;
    private volatile bool _panicModeEnabled;
    private int _disposed;

    public PairManager(ILogger<PairManager> logger, PairFactory pairFactory,
                SnowcloakConfigService configurationService, SnowMediator mediator) : base(logger, mediator)
    {
        _pairFactory = pairFactory;
        _configurationService = configurationService;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => ClearPairs());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => ReapplyPairData());
        Mediator.Subscribe<LocalCharacterDataPushedMessage>(this, MarkLocalCharacterDataPushed);
    }

    public List<Pair> DirectPairs
    {
        get { RebuildProjectionsIfDirty(); return _directPairsCache; }
    }

    public Dictionary<GroupFullInfoDto, List<Pair>> GroupPairs
    {
        get { RebuildProjectionsIfDirty(); return _groupPairsCache; }
    }
    public Dictionary<GroupData, GroupFullInfoDto> Groups => _allGroups.ToDictionary(k => k.Key, k => k.Value, GroupDataComparer.Instance);
    public Pair? LastAddedUser { get; internal set; }
    public bool PanicModeEnabled => _panicModeEnabled;
    private readonly ConcurrentDictionary<string, byte> _suppressedNotePairs =
        new(StringComparer.Ordinal);
    

    public void AddGroup(GroupFullInfoDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        _allGroups[dto.Group] = dto;
        InvalidateProjections();

        foreach (var pending in _pendingGroupPairs
                     .Where(entry => string.Equals(entry.Key.Gid, dto.Group.GID, StringComparison.Ordinal))
                     .ToList())
        {
            if (_pendingGroupPairs.TryRemove(pending.Key, out var queued)
                && !IsExpired(queued.ReceivedUtc))
            {
                AddGroupPair(queued.Dto);
            }
        }
    }

    public void AddGroupPair(GroupPairFullInfoDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (!_allGroups.TryGetValue(dto.Group, out var group))
        {
            BufferGroupPair(dto);
            return;
        }

        if (!_allClientPairs.ContainsKey(dto.User.UID))
            _allClientPairs[dto.User.UID] = _pairFactory.Create(dto.User);

        _allClientPairs[dto.User.UID].GroupPair[group] = dto;
        ApplyPanicHold(_allClientPairs[dto.User.UID]);
        InvalidateProjections();
        DrainPendingPairState(_allClientPairs[dto.User.UID]);
    }

    public Pair? GetPairByUID(string uid)
    {
        return _allClientPairs.TryGetValue(uid, out var pair) ? pair : null;
    }
    
    public Pair GetOrCreateTransientPair(UserData userData)
    {
        return GetPairByUID(userData.UID) ?? _pairFactory.Create(userData);
    }
    
    public void SuppressNextNotePopupForUid(string uid)
    {
        if (!string.IsNullOrEmpty(uid))
        {
            _suppressedNotePairs[uid] = 0;
        }
    }

    public void AddUserPair(UserPairDto dto, bool addToLastAddedUser = true)
    {
        if (!_allClientPairs.ContainsKey(dto.User.UID))
        {
            _allClientPairs[dto.User.UID] = _pairFactory.Create(dto.User);
        }
        else
        {
            addToLastAddedUser = false;
        }

        _allClientPairs[dto.User.UID].UserPair = dto;
        var suppressNotePopup = _suppressedNotePairs.TryRemove(dto.User.UID, out _);
        ApplyPanicHold(_allClientPairs[dto.User.UID]);

        if (addToLastAddedUser && !suppressNotePopup)
        {
            LastAddedUser = _allClientPairs[dto.User.UID];
        }
        _allClientPairs[dto.User.UID].ApplyLastReceivedData();
        InvalidateProjections();
        DrainPendingPairState(_allClientPairs[dto.User.UID]);
    }

    public void UpdateUserProfile(UserDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User.UID, out var pair))
        {
            return;
        }
        
        var previous = pair.UserData;
        var hexChanged = !string.Equals(previous.HexString, dto.User.HexString, StringComparison.Ordinal);
        var glowHexChanged = !string.Equals(previous.GlowHexString, dto.User.GlowHexString, StringComparison.Ordinal);

        pair.UpdateUserData(dto.User);

        if (hexChanged || glowHexChanged)
        {
            Mediator.Publish(new NameplateRedrawMessage());
        }

        if (pair.UserPair != null)
        {
            pair.UserPair = pair.UserPair with { User = dto.User };
        }

        foreach (var groupEntry in pair.GroupPair.ToList())
        {
            pair.GroupPair[groupEntry.Key] = groupEntry.Value with { User = dto.User };
        }
    }
    
    public void ClearPairs()
    {
        Logger.LogDebug("Clearing all Pairs");
        DisposePairs();
        _allClientPairs.Clear();
        _allGroups.Clear();
        _pendingGroupPairs.Clear();
        _pendingOnlineUsers.Clear();
        _pendingCharacterData.Clear();
        InvalidateProjections();
    }

    internal void ReconcileServerState(IReadOnlyCollection<UserPairDto> userPairs,
        IReadOnlyCollection<GroupFullInfoDto> groups,
        IReadOnlyCollection<GroupPairFullInfoDto> groupPairs,
        IReadOnlyCollection<OnlineUserIdentDto> onlineUsers)
    {
        var desiredGroups = groups.Select(group => group.Group.GID).ToHashSet(StringComparer.Ordinal);
        foreach (var group in _allGroups.Keys.Where(group => !desiredGroups.Contains(group.GID)).ToList())
        {
            RemoveGroup(group);
        }

        foreach (var group in groups)
        {
            AddGroup(group);
        }

        var desiredDirect = userPairs.Select(pair => pair.User.UID).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in _allClientPairs.Values.Where(pair => pair.UserPair != null && !desiredDirect.Contains(pair.UserData.UID)).ToList())
        {
            RemoveUserPair(new UserDto(pair.UserData));
        }

        foreach (var pair in userPairs)
        {
            AddUserPair(pair, addToLastAddedUser: false);
        }

        var desiredGroupPairs = groupPairs
            .Select(pair => (pair.Group.GID, pair.User.UID))
            .ToHashSet();
        foreach (var pair in _allClientPairs.Values.ToList())
        {
            foreach (var groupPair in pair.GroupPair.Values
                         .Where(entry => !desiredGroupPairs.Contains((entry.Group.GID, entry.User.UID)))
                         .ToList())
            {
                RemoveGroupPair(new GroupPairDto(groupPair.Group, groupPair.User));
            }
        }

        foreach (var pair in groupPairs)
        {
            AddGroupPair(pair);
        }

        ReconcileOnlineState(onlineUsers);
    }

    internal void ReconcileOnlineState(IReadOnlyCollection<OnlineUserIdentDto> onlineUsers)
    {
        var online = onlineUsers.ToDictionary(entry => entry.User.UID, StringComparer.Ordinal);
        foreach (var pair in _allClientPairs.Values.ToList())
        {
            if (online.TryGetValue(pair.UserData.UID, out var dto))
            {
                MarkPairOnline(dto, sendNotif: false);
            }
            else if (pair.HasCachedPlayer)
            {
                MarkPairOffline(pair.UserData);
            }
        }
    }

    public List<Pair> GetOnlineUserPairs() => _allClientPairs.Where(p => !string.IsNullOrEmpty(p.Value.GetPlayerNameHash())).Select(p => p.Value).ToList();

    public int GetVisibleUserCount() => _allClientPairs.Count(p => p.Value.IsVisible);

    public List<UserData> GetVisibleUsers() => _allClientPairs.Where(p => p.Value.IsVisible).Select(p => p.Value.UserData).ToList();
    
    public List<Pair> GetVisiblePairs() => _allClientPairs.Values.Where(p => p.IsVisible).ToList();

    public IReadOnlyList<Pair> GetPairsSnapshot() => _allClientPairs.Values.ToArray();

    public int TemporaryAppearancePeerCount => _allClientPairs.Values.Count(pair => pair.IsTemporaryAppearance);

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Removed pairs are disposed asynchronously")]
    public void ReconcileTemporaryAppearance(TemporaryAppearanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var desired = snapshot.Peers
            .Where(peer => !string.IsNullOrWhiteSpace(peer.User.UID)
                && !string.IsNullOrWhiteSpace(peer.CharacterIdent)
                && peer.Grants.Any(grant => grant.RemainingMs > 0
                    && (grant.ReceiveCategories & AppearanceCategoryMask.PlayerVisual) != 0))
            .ToDictionary(peer => peer.User.UID, StringComparer.Ordinal);

        foreach (var peer in desired.Values)
        {
            var pair = _allClientPairs.GetOrAdd(peer.User.UID, _ => _pairFactory.Create(peer.User));
            pair.UpdateUserData(peer.User);
            pair.TemporaryAppearance = peer;
            MarkPairOnline(pair, new OnlineUserIdentDto(peer.User, peer.CharacterIdent), sendNotif: false);
            ApplyPendingCharacterData(pair);
        }

        foreach (var pair in _allClientPairs.Values.Where(pair => pair.TemporaryAppearance != null
                     && !desired.ContainsKey(pair.UserData.UID)).ToList())
        {
            pair.TemporaryAppearance = null;
            if (pair.HasDurableConnection)
            {
                pair.ApplyLastReceivedData(forced: true);
            }
            else if (_allClientPairs.TryRemove(pair.UserData.UID, out var removed))
            {
                Mediator.Publish(new ClearProfileDataMessage(removed.UserData));
                // Roster reconciliation can run on the framework tick. Revert/redraw disposal must
                // not synchronously wait for that same framework to advance drawing state.
                QueuePairDisposal(removed);
            }
        }
        InvalidateProjections();
    }

    public IReadOnlyList<UserData> GetTemporaryAppearanceRecipients()
        => _allClientPairs.Values.Where(pair => pair.IsTemporaryAppearance && pair.IsVisible)
            .Select(pair => pair.UserData).ToArray();

    public IReadOnlyList<Pair> GetTemporaryAppearancePairs()
        => _allClientPairs.Values.Where(pair => pair.IsTemporaryAppearance)
            .OrderBy(pair => pair.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase).ToArray();

    public PanicModeResult TogglePanicMode()
    {
        return SetPanicMode(!_panicModeEnabled);
    }

    public PanicModeResult SetPanicMode(bool enabled)
    {
        _panicModeEnabled = enabled;
        var pairs = _allClientPairs.Values.ToList();

        foreach (var pair in pairs)
        {
            if (enabled)
            {
                ApplyPanicHold(pair);
            }
            else
            {
                pair.UnholdApplication(PanicHoldSource);
            }
        }

        return new PanicModeResult(enabled, pairs.Count);
    }

    private void ApplyPanicHold(Pair pair)
    {
        if (!_panicModeEnabled) return;

        if (!pair.HoldApplication(PanicHoldSource, maxValue: 1))
        {
            pair.UndoApplication();
        }
    }
    
    public Pair? GetPairByObjectId(uint objectId)
    {
        return _allClientPairs.Values.FirstOrDefault(pair => pair.PlayerCharacterId == objectId);
    }
    
    public void MarkPairOffline(UserData user)
    {
        _pendingOnlineUsers.TryRemove(user.UID, out _);
        _pendingCharacterData.TryRemove(user.UID, out _);

        if (_allClientPairs.TryGetValue(user.UID, out var pair))
        {
            Mediator.Publish(new ClearProfileDataMessage(pair.UserData));
            pair.MarkOffline();
            Mediator.Publish(new PairOnlineStateChangedMessage(pair.UserData.UID, false));
        }

        InvalidateProjections();
    }

    public void MarkPairOnline(OnlineUserIdentDto dto, bool sendNotif = true)
    {
        if (!_allClientPairs.TryGetValue(dto.User.UID, out var pair))
        {
            BufferOnlineUser(dto, sendNotif);
            return;
        }

        MarkPairOnline(pair, dto, sendNotif);
        ApplyPendingCharacterData(pair);
    }

    private void MarkPairOnline(Pair pair, OnlineUserIdentDto dto, bool sendNotif)
    {

        Mediator.Publish(new ClearProfileDataMessage(dto.User));

        var hadCachedPlayer = pair.HasCachedPlayer;

        if (!hadCachedPlayer && sendNotif && _configurationService.Current.ShowOnlineNotifications
            && (_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs && pair.UserPair != null
            || !_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs)
            && (_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs && !string.IsNullOrEmpty(pair.GetNote())
            || !_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs))
        {
            string? note = pair.GetNoteOrName();
            var msg = !string.IsNullOrEmpty(note)
                ? $"{note} ({pair.UserData.AliasOrUID}) is now online"
                : $"{pair.UserData.AliasOrUID} is now online";
            Mediator.Publish(new NotificationMessage("User online", msg, NotificationType.Info, TimeSpan.FromSeconds(5)));
        }

        pair.CreateCachedPlayer(dto);
        Mediator.Publish(new PairOnlineStateChangedMessage(pair.UserData.UID, true));

        InvalidateProjections();
    }

    public void ReceiveCharaData(OnlineUserCharaDataDto dto, string? manifestHash = null)
    {
        if (!_allClientPairs.TryGetValue(dto.User.UID, out var pair) || !pair.IsOnline)
        {
            BufferCharacterData(dto, manifestHash);

            // Close the check-then-buffer race with online player creation. Pair membership and
            // online identity callbacks also drain the buffer, so exactly one side applies it.
            if (_allClientPairs.TryGetValue(dto.User.UID, out pair) && pair.IsOnline)
            {
                ApplyPendingCharacterData(pair);
            }
            return;
        }

        ApplyCharacterData(pair, dto, manifestHash);
    }

    private void ApplyCharacterData(Pair pair, OnlineUserCharaDataDto dto, string? manifestHash)
    {
        Mediator.Publish(new EventMessage(new Event(pair.UserData, nameof(PairManager), EventSeverity.Informational, "Received Character Data")));
        pair.ApplyData(dto, manifestHash);
        Mediator.Publish(new PairDataReceivedMessage(pair.UserData.UID, dto.CharaData));
    }

    public void RemoveGroup(GroupData data)
    {
        _allGroups.TryRemove(data, out _);
        foreach (var pending in _pendingGroupPairs.Keys.Where(key => string.Equals(key.Gid, data.GID, StringComparison.Ordinal)).ToList())
        {
            _pendingGroupPairs.TryRemove(pending, out _);
        }

        foreach (var item in _allClientPairs.ToList())
        {
            foreach (var grpPair in item.Value.GroupPair.Select(k => k.Key).Where(grpPair => GroupDataComparer.Instance.Equals(grpPair.Group, data)).ToList())
            {
                item.Value.GroupPair.TryRemove(grpPair, out _);
            }

            if (!item.Value.HasAnyConnection() && _allClientPairs.TryRemove(item.Key, out var pair))
            {
                Mediator.Publish(new ClearProfileDataMessage(pair.UserData));
                pair.Dispose();
            }
        }

        InvalidateProjections();
    }

    public void RemoveGroupPair(GroupPairDto dto)
    {
        _pendingGroupPairs.TryRemove((dto.Group.GID, dto.User.UID), out _);
        if (_allClientPairs.TryGetValue(dto.User.UID, out var pair))
        {
            if (_allGroups.TryGetValue(dto.Group, out var group))
                pair.GroupPair.TryRemove(group, out _);
            else
                Logger.LogWarning("RemoveGroupPair: no group found for {dto}", dto);

            if (!pair.HasAnyConnection() && _allClientPairs.TryRemove(dto.User.UID, out var removedPair))
            {
                Mediator.Publish(new ClearProfileDataMessage(removedPair.UserData));
                removedPair.Dispose();
            }
        }

        InvalidateProjections();
    }

    public void RemoveUserPair(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User.UID, out var pair))
        {
            pair.UserPair = null;

            if (!pair.HasAnyConnection() && _allClientPairs.TryRemove(dto.User.UID, out var removedPair))
            {
                Mediator.Publish(new ClearProfileDataMessage(removedPair.UserData));
                removedPair.Dispose();
            }
        }

        InvalidateProjections();
    }

    public void SetGroupInfo(GroupInfoDto dto)
    {
        if (!_allGroups.TryRemove(dto.Group, out var group))
            return;

        group.Group = dto.Group;
        group.Owner = dto.Owner;
        group.GroupPermissions = dto.GroupPermissions;
        _allGroups[dto.Group] = group;

        InvalidateProjections();
    }

    public bool UpdatePairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User.UID, out var pair))
        {
            Logger.LogWarning("UpdatePairPermissions: no such pair for {dto}", dto);
            return false;
        }

        if (pair.UserPair == null)
        {
            Logger.LogWarning("UpdatePairPermissions: no direct pair for {dto}", dto);
            return false;
        }

        var pairingChanged = pair.UserPair.OtherPermissions.IsPaired() != dto.Permissions.IsPaired();
        if (pair.UserPair.OtherPermissions.IsPaused() != dto.Permissions.IsPaused()
            || pairingChanged)
        {
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        }

        pair.UserPair.OtherPermissions = dto.Permissions;

        Logger.LogTrace("Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}",
            pair.UserPair.OtherPermissions.IsPaused(),
            pair.UserPair.OtherPermissions.IsDisableAnimations(),
            pair.UserPair.OtherPermissions.IsDisableSounds(),
            pair.UserPair.OtherPermissions.IsDisableVFX());

        if (!pair.IsPaused)
            pair.ApplyLastReceivedData();

        InvalidateProjections();
        return pairingChanged;
    }

    public void UpdateSelfPairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User.UID, out var pair))
        {
            Logger.LogWarning("UpdateSelfPairPermissions: no such pair for {dto}", dto);
            return;
        }

        if (pair.UserPair == null)
        {
            Logger.LogWarning("UpdateSelfPairPermissions: no direct pair for {dto}", dto);
            return;
        }

        if (pair.UserPair.OwnPermissions.IsPaused() != dto.Permissions.IsPaused()
            || pair.UserPair.OwnPermissions.IsPaired() != dto.Permissions.IsPaired())
        {
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        }

        pair.UserPair.OwnPermissions = dto.Permissions;

        Logger.LogTrace("Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}",
            pair.UserPair.OwnPermissions.IsPaused(),
            pair.UserPair.OwnPermissions.IsDisableAnimations(),
            pair.UserPair.OwnPermissions.IsDisableSounds(),
            pair.UserPair.OwnPermissions.IsDisableVFX());

        if (!pair.IsPaused)
            pair.ApplyLastReceivedData();

        InvalidateProjections();
    }

    internal void ReceiveUploadStatus(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User.UID, out var existingPair) && existingPair.IsVisible)
        {
            existingPair.SetIsUploading();
        }
    }

    internal void ReceiveApplicationReceipt(PairApplicationReceiptDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.Receiver.UID, out var existingPair))
        {
            existingPair.ApplyApplicationReceipt(dto);
        }
    }

    private void MarkLocalCharacterDataPushed(LocalCharacterDataPushedMessage message)
    {
        foreach (var recipient in message.Recipients)
        {
            if (_allClientPairs.TryGetValue(recipient.UID, out var pair))
            {
                pair.MarkLocalDataPushed(message.DataHash);
            }
        }
    }

    private void BufferGroupPair(GroupPairFullInfoDto dto)
    {
        PruneExpiredPendingState();
        if (_pendingGroupPairs.Count >= MaxPendingStateEntries)
        {
            Logger.LogWarning("Discarding out-of-order group membership because the pending-state limit was reached");
            return;
        }

        _pendingGroupPairs[(dto.Group.GID, dto.User.UID)] = new(dto, DateTime.UtcNow);
        Logger.LogDebug("Queued group membership until its group state is available");
    }

    private void BufferOnlineUser(OnlineUserIdentDto dto, bool sendNotif)
    {
        PruneExpiredPendingState();
        if (_pendingOnlineUsers.Count >= MaxPendingStateEntries)
        {
            Logger.LogWarning("Discarding out-of-order online state because the pending-state limit was reached");
            return;
        }

        _pendingOnlineUsers[dto.User.UID] = new(dto, sendNotif, DateTime.UtcNow);
        Logger.LogDebug("Queued online state until its pair state is available");
    }

    private void BufferCharacterData(OnlineUserCharaDataDto dto, string? manifestHash)
    {
        PruneExpiredPendingState();
        if (_pendingCharacterData.Count >= MaxPendingCharacterDataEntries
            && !_pendingCharacterData.ContainsKey(dto.User.UID))
        {
            Logger.LogWarning("Discarding out-of-order character data because the pending-state limit was reached");
            return;
        }

        var incoming = new PendingCharacterData(dto, manifestHash, DateTime.UtcNow);
        _pendingCharacterData.AddOrUpdate(dto.User.UID, incoming, (_, current) => SelectNewest(current, incoming));
        Logger.LogDebug("Queued character data until its pair state is available");
    }

    private void DrainPendingPairState(Pair pair)
    {
        if (_pendingOnlineUsers.TryRemove(pair.UserData.UID, out var online)
            && !IsExpired(online.ReceivedUtc))
        {
            MarkPairOnline(pair, online.Dto, online.SendNotification);
        }

        ApplyPendingCharacterData(pair);
    }

    private void ApplyPendingCharacterData(Pair pair)
    {
        if (!pair.IsOnline)
        {
            return;
        }

        if (_pendingCharacterData.TryRemove(pair.UserData.UID, out var pending)
            && !IsExpired(pending.ReceivedUtc))
        {
            ApplyCharacterData(pair, pending.Dto, pending.ManifestHash);
        }
    }

    private void PruneExpiredPendingState()
    {
        foreach (var entry in _pendingGroupPairs.Where(entry => IsExpired(entry.Value.ReceivedUtc)).ToList())
        {
            _pendingGroupPairs.TryRemove(entry.Key, out _);
        }

        foreach (var entry in _pendingOnlineUsers.Where(entry => IsExpired(entry.Value.ReceivedUtc)).ToList())
        {
            _pendingOnlineUsers.TryRemove(entry.Key, out _);
        }

        foreach (var entry in _pendingCharacterData.Where(entry => IsExpired(entry.Value.ReceivedUtc)).ToList())
        {
            _pendingCharacterData.TryRemove(entry.Key, out _);
        }
    }

    private static PendingCharacterData SelectNewest(PendingCharacterData current, PendingCharacterData incoming)
    {
        if (current.Dto.DataVersion > 0 && incoming.Dto.DataVersion > 0)
        {
            return incoming.Dto.DataVersion >= current.Dto.DataVersion ? incoming : current;
        }

        return incoming;
    }

    private static bool IsExpired(DateTime receivedUtc) => DateTime.UtcNow - receivedUtc > PendingStateLifetime;
    
    private bool TryGetGroupPairInfo(GroupData groupData, UserData user,
        [NotNullWhen(true)] out Pair? pair, [NotNullWhen(true)] out GroupPairFullInfoDto? info,
        [CallerMemberName] string? caller = null)
    {
        info = null;
        if (!_allGroups.TryGetValue(groupData, out var group))
        {
            Logger.LogWarning("{caller}: no group found for {group}", caller, groupData);
            pair = null;
            return false;
        }
        if (!_allClientPairs.TryGetValue(user.UID, out pair))
        {
            Logger.LogWarning("{caller}: no user found for {user}", caller, user);
            return false;
        }
        if (!pair.GroupPair.TryGetValue(group, out info))
        {
            Logger.LogWarning("{caller}: no group-pair membership for {user} in {group}", caller, user, groupData);
            return false;
        }
        return true;
    }

    internal void SetGroupPairStatusInfo(GroupPairUserInfoDto dto)
    {
        if (!TryGetGroupPairInfo(dto.Group, dto.User, out _, out var groupPairInfo)) return;
        groupPairInfo.GroupPairStatusInfo = dto.GroupUserInfo;
        InvalidateProjections();
    }

    internal void SetGroupPairMemberLabels(GroupMemberLabelsDto dto)
    {
        if (!TryGetGroupPairInfo(dto.Group, dto.User, out _, out var groupPairInfo)) return;
        groupPairInfo.MemberLabels = dto.Labels;
        InvalidateProjections();
    }

    internal void SetGroupPairUserPermissions(GroupPairUserPermissionDto dto)
    {
        if (!TryGetGroupPairInfo(dto.Group, dto.User, out var pair, out var groupPairInfo)) return;
        var prevOwnPermissions = groupPairInfo.OwnGroupUserPermissions;
        var prevOtherPermissions = groupPairInfo.OtherGroupUserPermissions;
        groupPairInfo.GroupUserPermissions = dto.GroupPairPermissions;
        groupPairInfo.OwnGroupUserPermissions = dto.OwnGroupPairPermissions;
        groupPairInfo.OtherGroupUserPermissions = dto.OtherGroupPairPermissions;
        if (prevOwnPermissions.IsDisableAnimations() != dto.OwnGroupPairPermissions.IsDisableAnimations()
            || prevOwnPermissions.IsDisableSounds() != dto.OwnGroupPairPermissions.IsDisableSounds()
            || prevOwnPermissions.IsDisableVFX() != dto.OwnGroupPairPermissions.IsDisableVFX()
            || prevOtherPermissions.IsDisableAnimations() != dto.OtherGroupPairPermissions.IsDisableAnimations()
            || prevOtherPermissions.IsDisableSounds() != dto.OtherGroupPairPermissions.IsDisableSounds()
            || prevOtherPermissions.IsDisableVFX() != dto.OtherGroupPairPermissions.IsDisableVFX())
        {
            pair.ApplyLastReceivedData();
        }
        InvalidateProjections();
    }

    internal void SetGroupPermissions(GroupPermissionDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var group))
        {
            Logger.LogWarning("SetGroupPermissions: no group found for {dto}", dto);
            return;
        }

        var prevPermissions = group.GroupPermissions;
        group.GroupPermissions = dto.Permissions;
        if (prevPermissions.IsDisableAnimations() != dto.Permissions.IsDisableAnimations()
            || prevPermissions.IsDisableSounds() != dto.Permissions.IsDisableSounds()
            || prevPermissions.IsDisableVFX() != dto.Permissions.IsDisableVFX())
        {
            InvalidateProjections();
            GroupPairs[group].ForEach(p => p.ApplyLastReceivedData());
        }
        InvalidateProjections();
    }

    internal void SetGroupStatusInfo(GroupPairUserInfoDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var group))
        {
            Logger.LogWarning("SetGroupStatusInfo: no group found for {dto}", dto);
            return;
        }
        group.GroupUserInfo = dto.GroupUserInfo;
        InvalidateProjections();
    }

    internal void SetGroupMemberLabels(GroupMemberLabelsDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var group))
        {
            Logger.LogWarning("SetGroupMemberLabels: no group found for {dto}", dto);
            return;
        }
        group.MemberLabels = dto.Labels;
        InvalidateProjections();
    }

    internal void SetGroupUserPermissions(GroupPairUserPermissionDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var group))
        {
            Logger.LogWarning("SetGroupUserPermissions: no group found for {dto}", dto);
            return;
        }

        var prevPermissions = group.GroupUserPermissions;
        group.GroupUserPermissions = dto.GroupPairPermissions;
        if (prevPermissions.IsDisableAnimations() != dto.GroupPairPermissions.IsDisableAnimations()
            || prevPermissions.IsDisableSounds() != dto.GroupPairPermissions.IsDisableSounds()
            || prevPermissions.IsDisableVFX() != dto.GroupPairPermissions.IsDisableVFX())
        {
            InvalidateProjections();
            GroupPairs[group].ForEach(p => p.ApplyLastReceivedData());
        }
        InvalidateProjections();
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing);

        _backgroundTasks.StopAccepting();
        _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(5), nameof(PairManager));
        DisposePairs();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing: true);

        _backgroundTasks.StopAccepting();
        await _backgroundTasks.StopAsync().ConfigureAwait(false);
        await DisposePairsAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Detached disposal is a terminal cleanup boundary and every failure must be observed.")]
    private void QueuePairDisposal(Pair pair)
    {
        var disposal = Task.Run(async () =>
        {
            try
            {
                await pair.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogTemporaryPairDisposalFailed(Logger, pair.UserData.UID, ex);
            }
        });
        _ = _backgroundTasks.Track(disposal, nameof(QueuePairDisposal));
    }

    private void DisposePairs()
    {
        Logger.LogDebug("Disposing all Pairs");
        Parallel.ForEach(_allClientPairs, item =>
        {
            item.Value.Dispose();
        });

        InvalidateProjections();
    }

    private async Task DisposePairsAsync()
    {
        Logger.LogDebug("Disposing all Pairs asynchronously");
        var pairs = _allClientPairs.Values.ToArray();
        foreach (var pair in pairs)
        {
            await pair.DisposeAsync().ConfigureAwait(false);
        }

        InvalidateProjections();
    }

    private void ReapplyPairData()
    {
        foreach (var pair in _allClientPairs.Select(k => k.Value))
        {
            pair.ApplyLastReceivedData(forced: true);
        }
    }

    private void InvalidateProjections() => _projectionsDirty = true;

    private void RebuildProjectionsIfDirty()
    {
        if (!_projectionsDirty) return;
        lock (_projectionLock)
        {
            if (!_projectionsDirty) return;

            _directPairsCache = _allClientPairs.Select(k => k.Value).Where(k => k.UserPair != null).ToList();

            var groupDict = new Dictionary<GroupFullInfoDto, List<Pair>>();
            foreach (var group in _allGroups)
            {
                groupDict[group.Value] = _allClientPairs.Select(p => p.Value)
                    .Where(p => p.GroupPair.Any(g => GroupDataComparer.Instance.Equals(group.Key, g.Key.Group))).ToList();
            }
            _groupPairsCache = groupDict;

            _projectionsDirty = false;
        }
    }

    private sealed record PendingCharacterData(OnlineUserCharaDataDto Dto, string? ManifestHash, DateTime ReceivedUtc);
    private sealed record PendingGroupPair(GroupPairFullInfoDto Dto, DateTime ReceivedUtc);
    private sealed record PendingOnlineUser(OnlineUserIdentDto Dto, bool SendNotification, DateTime ReceivedUtc);
}

public readonly record struct PanicModeResult(bool Enabled, int AffectedPairs);
