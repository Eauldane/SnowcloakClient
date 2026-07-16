using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.Configuration;
using Snowcloak.Core.Chat;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI;
using Microsoft.AspNetCore.SignalR;

namespace Snowcloak.Services.Chat;

public sealed class ChatClientService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ApiController _apiController;
    private readonly ChatPreferencesStore _chatPreferences;
    private readonly SnowcloakConfigService _configService;
    private readonly ChatIdentityResolver _identityResolver;
    private readonly PairManager _pairManager;
    private readonly ChatRoomRegistry _rooms;
    private readonly ServerRegistry _serverRegistry;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly Lock _activeRoomsLock = new();
    private readonly HashSet<string> _activeRooms = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _chatEnabled;

    public ChatClientService(ILogger<ChatClientService> logger, SnowMediator mediator, ApiController apiController,
        ChatStore store, ChatPreferencesStore chatPreferences, SnowcloakConfigService configService,
        ChatIdentityResolver identityResolver, PairManager pairManager, ChatRoomRegistry rooms,
        ServerRegistry serverRegistry) : base(logger, mediator)
    {
        ArgumentNullException.ThrowIfNull(configService);
        _apiController = apiController;
        Store = store;
        _chatPreferences = chatPreferences;
        _configService = configService;
        _identityResolver = identityResolver;
        _pairManager = pairManager;
        _rooms = rooms;
        _serverRegistry = serverRegistry;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _chatEnabled = configService.Current.ChatEnabled;
    }

    public ChatStore Store { get; }
    public bool CanSend => _configService.Current.ChatEnabled && _apiController.IsConnected;
    public IReadOnlyList<RoomData> ListRooms() => _rooms.ListRooms();
    public IReadOnlyDictionary<string, int> SnapshotRoomCounts() => _rooms.SnapshotCounts();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Mediator.Subscribe<ConnectedMessage>(this, _ =>
        {
            lock (_activeRoomsLock)
            {
                _activeRooms.Clear();
            }

            Store.InvalidateHistory();
            QueueRefresh();
        });
        Mediator.Subscribe<ChatMembershipChangedMessage>(this, _ => QueueRefresh());
        Mediator.Subscribe<UserChatMsgMessage>(this, message => Receive(
            new ConversationKey(ConversationKind.Direct, message.Dto.Message.Sender.UID), message.Dto.Message));
        Mediator.Subscribe<GroupChatMsgMessage>(this, message => Receive(
            new ConversationKey(ConversationKind.Syncshell, message.Dto.Group.Group.GID), message.Dto.Message));
        Mediator.Subscribe<RoomChatMsgMessage>(this, message =>
        {
            _rooms.Upsert(message.Dto.Room.Room);
            Receive(new ConversationKey(ConversationKind.Room, message.Dto.Room.Room.RoomId), message.Dto.Message);
        });
        Mediator.Subscribe<RoomMemberJoinedMessage>(this, message =>
        {
            var dto = message.Dto;
            _rooms.SetMember(dto.Room, dto.User, dto.Role);
            Store.SetMember(new ConversationKey(ConversationKind.Room, dto.Room.RoomId), dto.User.UID, dto.Role);
            if (string.Equals(dto.User.UID, _identityResolver.SelfUid, StringComparison.Ordinal))
            {
                SetRoomActive(dto.Room.RoomId, true);
            }
        });
        Mediator.Subscribe<RoomMemberLeftMessage>(this, message =>
        {
            var dto = message.Dto;
            _rooms.RemoveMember(dto.Room, dto.User.UID);
            Store.RemoveMember(new ConversationKey(ConversationKind.Room, dto.Room.RoomId), dto.User.UID);
            if (string.Equals(dto.User.UID, _identityResolver.SelfUid, StringComparison.Ordinal))
            {
                SetRoomActive(dto.Room.RoomId, false);
                _serverRegistry.CurrentServer.JoinedRooms.Remove(dto.Room.RoomId);
                _serverRegistry.Save();
                Store.RemoveConversation(new ConversationKey(ConversationKind.Room, dto.Room.RoomId));
            }
        });
        Mediator.Subscribe<ClearProfileDataMessage>(this, _ => Store.RefreshDisplays(_identityResolver.Resolve));
        Store.MessageSent += OnMessageSent;
        _chatPreferences.ConfigChanged += OnPreferencesChanged;
        _configService.ConfigChanged += OnGlobalConfigChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _chatPreferences.ConfigChanged -= OnPreferencesChanged;
        _configService.ConfigChanged -= OnGlobalConfigChanged;
        Store.MessageSent -= OnMessageSent;
        UnsubscribeAll();
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => RefreshInternalAsync(cancellationToken);

    public async Task<RoomData> CreateRoomAsync(string name, string? topic, bool isPrivate)
    {
        var room = await _apiController.RoomCreate(new RoomCreateDto(name, topic, isPrivate)).ConfigureAwait(false);
        _rooms.Upsert(room.Room);
        AddJoinedRoom(room.Room);
        SetRoomActive(room.Room.RoomId, true);
        await RefreshRoomMembersAsync(room.Room).ConfigureAwait(false);
        return room.Room;
    }

    public async Task<bool> JoinRoomAsync(RoomData room)
    {
        ArgumentNullException.ThrowIfNull(room);
        _rooms.Upsert(room);
        var member = await _apiController.RoomJoin(new RoomDto(room)).ConfigureAwait(false);
        if (member == null)
        {
            return false;
        }

        AddJoinedRoom(room);
        SetRoomActive(room.RoomId, true);
        await RefreshRoomMembersAsync(room).ConfigureAwait(false);
        await Store.EnsureHistory(new ConversationKey(ConversationKind.Room, room.RoomId)).ConfigureAwait(false);
        return true;
    }

    public async Task LeaveRoomAsync(RoomData room)
    {
        ArgumentNullException.ThrowIfNull(room);
        await _apiController.RoomLeave(new RoomDto(room)).ConfigureAwait(false);
        _serverRegistry.CurrentServer.JoinedRooms.Remove(room.RoomId);
        _serverRegistry.Save();
        SetRoomActive(room.RoomId, false);
        Store.RemoveConversation(new ConversationKey(ConversationKind.Room, room.RoomId));
    }

    public Task InviteAsync(RoomData room, UserData user)
        => _apiController.RoomInvite(new RoomInviteDto(room, user));

    public async Task SetTopicAsync(RoomData room, string? topic)
    {
        ArgumentNullException.ThrowIfNull(room);
        await _apiController.RoomSetTopic(new RoomTopicUpdateDto(room, topic)).ConfigureAwait(false);
        _rooms.Upsert(room with { Topic = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim() });
    }

    public Task SetRoleAsync(RoomData room, UserData user, RoomRole role)
        => _apiController.RoomSetRole(new RoomRoleUpdateDto(room, user, role));

    public Task KickAsync(RoomData room, UserData user, string? reason = null)
        => _apiController.RoomKick(new RoomKickDto(room, user, reason));

    public Task BanAsync(RoomData room, UserData user, string? reason = null)
        => _apiController.RoomBan(new RoomBanDto(room, user, reason));

    public Task UnbanAsync(RoomData room, UserData user)
        => _apiController.RoomUnban(new RoomUnbanDto(room, user));

    public IReadOnlyList<RoomMemberDto> GetRoomMembers(string roomId) => _rooms.GetMembers(roomId);

    public async Task RefreshRoomMembersAsync(RoomData room)
    {
        ArgumentNullException.ThrowIfNull(room);
        var members = await _apiController.RoomGetMembers(new RoomDto(room)).ConfigureAwait(false);
        _rooms.ReplaceMembers(room.RoomId, members);
        Store.ReplaceMembers(new ConversationKey(ConversationKind.Room, room.RoomId),
            members.Select(member => new KeyValuePair<string, RoomRole>(member.User.UID, member.Role)));
    }

    public void SetMuted(ConversationKey key, bool muted)
    {
        _chatPreferences.SetMuted(key, muted);
        Store.SetMuted(key, muted);
    }

    public void SetSound(ConversationKey key, Configuration.Models.ChatSoundOption sound)
        => _chatPreferences.SetSound(key, sound);

    public Configuration.Models.ChatSoundOption GetSound(ConversationKey key)
        => _chatPreferences.ResolveSound(key, _configService.Current.DefaultChatSound);

    private void QueueRefresh()
    {
        _ = _backgroundTasks.Run(() => RefreshInternalAsync(CancellationToken.None), nameof(RefreshInternalAsync));
    }

    private async Task RefreshInternalAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_configService.Current.ChatEnabled || !_apiController.IsConnected)
            {
                return;
            }

            var activeKeys = new HashSet<ConversationKey>();
            foreach (var pair in _pairManager.DirectPairs)
            {
                var key = new ConversationKey(ConversationKind.Direct, pair.UserData.UID);
                activeKeys.Add(key);
                ConfigureConversation(key, pair.UserData.AliasOrUID);
            }

            foreach (var group in _pairManager.Groups.Values)
            {
                var key = new ConversationKey(ConversationKind.Syncshell, group.GID);
                activeKeys.Add(key);
                ConfigureConversation(key, group.GroupAliasOrGID);
                Store.ReplaceMembers(key, ProjectSyncshellMembers(group));
                Store.ReplaceMemberLabels(key, ProjectSyncshellMemberLabels(group));
            }

            foreach (var conversation in Store.Snapshot.Conversations
                         .Where(conversation => conversation.Key.Kind != ConversationKind.Room && !activeKeys.Contains(conversation.Key)))
            {
                Store.RemoveConversation(conversation.Key);
            }

            foreach (var conversation in Store.Snapshot.Conversations.Where(conversation => conversation.Key.Kind != ConversationKind.Room))
            {
                await Store.EnsureHistory(conversation.Key, cancellationToken).ConfigureAwait(false);
            }

            var roomList = await _apiController.RoomList().ConfigureAwait(false);
            var counts = await _apiController.RoomListUserCounts().ConfigureAwait(false);
            _rooms.ReplaceRooms(roomList, counts);

            foreach (var roomId in _serverRegistry.CurrentServer.JoinedRooms.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var room = roomList.FirstOrDefault(candidate => string.Equals(candidate.Room.RoomId, roomId, StringComparison.Ordinal));
                if (room == null)
                {
                    _serverRegistry.CurrentServer.JoinedRooms.Remove(roomId);
                    SetRoomActive(roomId, false);
                    continue;
                }

                if (!IsRoomActive(roomId))
                {
                    RoomMemberDto? joined;
                    try
                    {
                        joined = await _apiController.RoomJoin(room).ConfigureAwait(false);
                    }
                    catch (HubException)
                    {
                        _serverRegistry.CurrentServer.JoinedRooms.Remove(roomId);
                        Store.RemoveConversation(new ConversationKey(ConversationKind.Room, roomId));
                        continue;
                    }
                    if (joined == null)
                    {
                        _serverRegistry.CurrentServer.JoinedRooms.Remove(roomId);
                        continue;
                    }

                    SetRoomActive(roomId, true);
                }

                ConfigureConversation(new ConversationKey(ConversationKind.Room, roomId), room.Room.Name);
                await RefreshRoomMembersAsync(room.Room).ConfigureAwait(false);
            }

            _serverRegistry.Save();
            foreach (var conversation in Store.Snapshot.Conversations.Where(conversation => conversation.Key.Kind == ConversationKind.Room))
            {
                await Store.EnsureHistory(conversation.Key, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void Receive(ConversationKey key, ChatMessageDto message)
    {
        if (!_configService.Current.ChatEnabled)
        {
            return;
        }

        EnsureIncomingMetadata(key, message);
        var entry = Store.AppendIncoming(key, message);
        if (entry != null)
        {
            Mediator.Publish(new ChatIncomingAppendedMessage(key, entry));
        }
    }

    private void EnsureIncomingMetadata(ConversationKey key, ChatMessageDto message)
    {
        var title = key.Kind switch
        {
            ConversationKind.Direct => message.Sender.AliasOrUID,
            ConversationKind.Syncshell => _pairManager.Groups.Values
                .FirstOrDefault(group => string.Equals(group.GID, key.Id, StringComparison.Ordinal))?.GroupAliasOrGID ?? key.Id,
            ConversationKind.Room when _rooms.TryGet(key.Id, out var room) => room.Name,
            _ => key.Id,
        };
        ConfigureConversation(key, title);
    }

    private void ConfigureConversation(ConversationKey key, string title)
    {
        var muted = _chatPreferences.ResolveMuted(key, title, _configService.Current.AutoMuteNewSyncshellChats);
        Store.SetConversationMetadata(key, title, muted);
    }

    private List<KeyValuePair<string, RoomRole>> ProjectSyncshellMembers(GroupFullInfoDto group)
    {
        var members = new List<KeyValuePair<string, RoomRole>>
        {
            new(_identityResolver.SelfUid, string.Equals(group.OwnerUID, _identityResolver.SelfUid, StringComparison.Ordinal)
                ? RoomRole.Owner
                : group.GroupUserInfo.IsModerator() ? RoomRole.Moderator : RoomRole.Member),
        };
        if (!_pairManager.GroupPairs.TryGetValue(group, out var pairs))
        {
            return members;
        }

        foreach (var pair in pairs)
        {
            var role = string.Equals(group.OwnerUID, pair.UserData.UID, StringComparison.Ordinal)
                ? RoomRole.Owner
                : pair.GroupPair.TryGetValue(group, out var membership) && membership.GroupPairStatusInfo.IsModerator()
                    ? RoomRole.Moderator
                    : RoomRole.Member;
            members.Add(new KeyValuePair<string, RoomRole>(pair.UserData.UID, role));
        }

        return members;
    }

    private List<KeyValuePair<string, IReadOnlyList<string>>> ProjectSyncshellMemberLabels(GroupFullInfoDto group)
    {
        var members = new List<KeyValuePair<string, IReadOnlyList<string>>>
        {
            new(_identityResolver.SelfUid, group.MemberLabels.ToArray()),
        };
        if (!_pairManager.GroupPairs.TryGetValue(group, out var pairs))
        {
            return members;
        }

        foreach (var pair in pairs)
        {
            var labels = pair.GroupPair.TryGetValue(group, out var membership)
                ? membership.MemberLabels.ToArray()
                : [];
            members.Add(new KeyValuePair<string, IReadOnlyList<string>>(pair.UserData.UID, labels));
        }

        return members;
    }

    private void AddJoinedRoom(RoomData room)
    {
        _serverRegistry.CurrentServer.JoinedRooms.Add(room.RoomId);
        _serverRegistry.Save();
        ConfigureConversation(new ConversationKey(ConversationKind.Room, room.RoomId), room.Name);
    }

    private bool IsRoomActive(string roomId)
    {
        lock (_activeRoomsLock)
        {
            return _activeRooms.Contains(roomId);
        }
    }

    private void SetRoomActive(string roomId, bool active)
    {
        lock (_activeRoomsLock)
        {
            if (active)
            {
                _activeRooms.Add(roomId);
            }
            else
            {
                _activeRooms.Remove(roomId);
            }
        }
    }

    private void OnPreferencesChanged(object? sender, EventArgs eventArgs)
    {
        foreach (var conversation in Store.Snapshot.Conversations)
        {
            ConfigureConversation(conversation.Key, conversation.Title);
        }
    }

    private void OnGlobalConfigChanged()
    {
        var enabled = _configService.Current.ChatEnabled;
        if (enabled && !_chatEnabled)
        {
            QueueRefresh();
        }

        _chatEnabled = enabled;
    }

    private void OnMessageSent(object? sender, ChatMessageSentEventArgs eventArgs)
    {
        Mediator.Publish(new ChatOutgoingStampedMessage(eventArgs.Key, eventArgs.Entry));
    }

    protected override void Dispose(bool disposing)
    {
        _chatPreferences.ConfigChanged -= OnPreferencesChanged;
        _configService.ConfigChanged -= OnGlobalConfigChanged;
        Store.MessageSent -= OnMessageSent;
        _refreshGate.Dispose();
        base.Dispose(disposing);
    }
}
