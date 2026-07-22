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
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Snowcloak.PlayerData.Pairs;

public sealed class PairManager : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private const string PanicHoldSource = "Panic";
    private readonly ConcurrentDictionary<string, Pair> _allClientPairs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<GroupData, GroupFullInfoDto> _allGroups = new(GroupDataComparer.Instance);
    private readonly SnowcloakConfigService _configurationService;
    private readonly PairFactory _pairFactory;
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
        _allGroups[dto.Group] = dto;
        InvalidateProjections();
    }

    public void AddGroupPair(GroupPairFullInfoDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var group))
        {
            Logger.LogWarning("AddGroupPair: no group found for {dto}", dto);
            return;
        }

        if (!_allClientPairs.ContainsKey(dto.User.UID))
            _allClientPairs[dto.User.UID] = _pairFactory.Create(dto.User);

        _allClientPairs[dto.User.UID].GroupPair[group] = dto;
        ApplyPanicHold(_allClientPairs[dto.User.UID]);
        InvalidateProjections();
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
            Logger.LogWarning("MarkPairOnline: no user found for {dto}", dto);
            return;
        }

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
        if (!_allClientPairs.TryGetValue(dto.User.UID, out var pair))
        {
            Logger.LogWarning("ReceiveCharaData: no user found for {dto}", dto.User);
            return;
        }

        Mediator.Publish(new EventMessage(new Event(pair.UserData, nameof(PairManager), EventSeverity.Informational, "Received Character Data")));
        pair.ApplyData(dto, manifestHash);
        Mediator.Publish(new PairDataReceivedMessage(pair.UserData.UID, dto.CharaData));
    }

    public void RemoveGroup(GroupData data)
    {
        _allGroups.TryRemove(data, out _);

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

    public void UpdatePairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User.UID, out var pair))
        {
            Logger.LogWarning("UpdatePairPermissions: no such pair for {dto}", dto);
            return;
        }

        if (pair.UserPair == null)
        {
            Logger.LogWarning("UpdatePairPermissions: no direct pair for {dto}", dto);
            return;
        }

        if (pair.UserPair.OtherPermissions.IsPaused() != dto.Permissions.IsPaused()
            || pair.UserPair.OtherPermissions.IsPaired() != dto.Permissions.IsPaired())
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

        DisposePairs();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing: true);

        await DisposePairsAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
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
}

public readonly record struct PanicModeResult(bool Enabled, int AffectedPairs);
