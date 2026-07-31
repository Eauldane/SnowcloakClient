using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.Roleplay;
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
    private static readonly TimeSpan HistoryRestoreInterval = TimeSpan.FromMilliseconds(500);
    private static readonly Action<ILogger, ConversationKey, Exception?> LogHistoryRestoreFailed = LoggerMessage.Define<ConversationKey>(
        LogLevel.Debug,
        new EventId(1, nameof(EnsureHistoryAsync)),
        "Unable to restore chat history for {Conversation}");
    private readonly ApiController _apiController;
    private readonly ChatPreferencesStore _chatPreferences;
    private readonly ChatSyncStateStore _chatSyncState;
    private readonly SnowcloakConfigService _configService;
    private readonly ChatIdentityResolver _identityResolver;
    private readonly PairManager _pairManager;
    private readonly ChatRoomRegistry _rooms;
    private readonly ServerRegistry _serverRegistry;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly Lock _activeRoomsLock = new();
    private readonly Lock _roomTurnLock = new();
    private readonly HashSet<string> _activeRooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoomTurnStateDto?> _roomTurns = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _historyRestoreGate = new(1, 1);
    private bool _chatEnabled;

    public ChatClientService(ILogger<ChatClientService> logger, SnowMediator mediator, ApiController apiController,
        ChatStore store, ChatPreferencesStore chatPreferences, SnowcloakConfigService configService,
        ChatSyncStateStore chatSyncState, ChatIdentityResolver identityResolver, PairManager pairManager, ChatRoomRegistry rooms,
        ServerRegistry serverRegistry) : base(logger, mediator)
    {
        ArgumentNullException.ThrowIfNull(configService);
        _apiController = apiController;
        Store = store;
        _chatPreferences = chatPreferences;
        _chatSyncState = chatSyncState;
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

    public IReadOnlyList<ConversationSnapshot> GetOrderedConversations(bool includeOfflineDirect)
    {
        return Store.Snapshot.Conversations
            .Where(conversation => conversation.Key.Kind != ConversationKind.Direct
                || _pairManager.GetPairByUID(conversation.Key.Id)?.IsMutualDirectPair == true)
            .Where(conversation => includeOfflineDirect || conversation.Key.Kind != ConversationKind.Direct
                || _pairManager.GetPairByUID(conversation.Key.Id)?.IsOnline == true)
            .OrderBy(conversation => conversation.Key.Kind switch
            {
                ConversationKind.Room => 0,
                ConversationKind.Direct => 1,
                ConversationKind.Syncshell => 2,
                _ => 3,
            })
            .ThenBy(conversation => conversation.Key.Kind == ConversationKind.Direct
                && _pairManager.GetPairByUID(conversation.Key.Id)?.IsOnline != true)
            .ThenBy(conversation => conversation.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Mediator.Subscribe<ConnectedMessage>(this, _ => ResetConnectionState());
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
        Mediator.Subscribe<RpRoomUpdatedMessage>(this, message => ApplyRoomUpdate(message.Dto.Room));
        Mediator.Subscribe<RoomMemberJoinedMessage>(this, message =>
        {
            var dto = message.Dto;
            var key = new ConversationKey(ConversationKind.Room, dto.Room.RoomId);
            var wasActive = _rooms.GetMembers(dto.Room.RoomId)
                .Any(member => string.Equals(member.User.UID, dto.User.UID, StringComparison.Ordinal));
            _rooms.SetMember(dto);
            Store.SetMember(key, dto.User.UID, dto.Role);
            if (!wasActive && !string.Equals(dto.User.UID, _identityResolver.SelfUid, StringComparison.Ordinal))
            {
                Store.AppendMembershipEvent(key, MembershipEventId(dto.SessionSequence), dto.User, ChatEntryKind.MemberJoined);
            }
            if (string.Equals(dto.User.UID, _identityResolver.SelfUid, StringComparison.Ordinal))
            {
                SetRoomActive(dto.Room.RoomId, true);
            }
        });
        Mediator.Subscribe<RoomMemberLeftMessage>(this, message =>
        {
            var dto = message.Dto;
            var key = new ConversationKey(ConversationKind.Room, dto.Room.RoomId);
            var wasActive = _rooms.GetMembers(dto.Room.RoomId)
                .Any(member => string.Equals(member.User.UID, dto.User.UID, StringComparison.Ordinal));
            _rooms.RemoveMember(dto.Room, dto.User.UID);
            Store.RemoveMember(key, dto.User.UID);
            if (wasActive && !string.Equals(dto.User.UID, _identityResolver.SelfUid, StringComparison.Ordinal))
            {
                Store.AppendMembershipEvent(key, MembershipEventId(dto.SessionSequence), dto.User, ChatEntryKind.MemberLeft);
            }
            if (string.Equals(dto.User.UID, _identityResolver.SelfUid, StringComparison.Ordinal))
            {
                SetRoomActive(dto.Room.RoomId, false);
                _serverRegistry.CurrentServer.JoinedRooms.Remove(dto.Room.RoomId);
                _serverRegistry.Save();
                Store.RemoveConversation(key);
            }
        });
        Mediator.Subscribe<ClearProfileDataMessage>(this, _ => Store.RefreshDisplays(_identityResolver.Resolve));
        Store.MessageSent += OnMessageSent;
        _chatPreferences.ConfigChanged += OnPreferencesChanged;
        _configService.ConfigChanged += OnGlobalConfigChanged;
        if (_apiController.IsConnected)
        {
            ResetConnectionState();
        }
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

    public Task InviteAsync(RoomData room, UserData user, RpIntroSnapshotDto? intro = null)
        => _apiController.RoomInvite(new RoomInviteDto(room, user) { Intro = intro });

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

    public async Task<RoomData> SetDiscoveryAsync(RoomData room, RoomDiscoveryDto discovery)
    {
        var updated = await _apiController.RpRoomSetDiscovery(new RoomDiscoveryUpdateDto
        {
            Room = room,
            Discovery = discovery,
        }).ConfigureAwait(false);
        ApplyRoomUpdate(updated.Room);
        return updated.Room;
    }

    public async Task<RoomData> SetSceneAsync(RoomData room, RoomSceneMetadataDto scene)
    {
        var updated = await _apiController.RpRoomSetScene(new RoomSceneMetadataUpdateDto
        {
            Room = room,
            Scene = scene,
        }).ConfigureAwait(false);
        ApplyRoomUpdate(updated.Room);
        return updated.Room;
    }

    public async Task<RoomMemberDto> SetSceneIdentityAsync(RoomData room, string? nickname, uint? roleIconId, string? roleLabel)
    {
        ArgumentNullException.ThrowIfNull(room);
        var updated = await _apiController.RpRoomSetParticipantIdentity(new RoomParticipantIdentityUpdateDto
        {
            Room = room,
            Nickname = nickname,
            RoleIconId = roleIconId,
            RoleLabel = roleLabel,
        }).ConfigureAwait(false);
        _rooms.SetMember(updated);
        Store.SetMember(new ConversationKey(ConversationKind.Room, room.RoomId), updated.User.UID, updated.Role);
        return updated;
    }

    public async Task<RoomSceneHistoryDto> FinishSceneAsync(RoomData room)
    {
        ArgumentNullException.ThrowIfNull(room);
        var history = await _apiController.RpRoomFinishScene(new RoomDto(room)).ConfigureAwait(false);
        Store.ResetConversation(new ConversationKey(ConversationKind.Room, room.RoomId));
        return history;
    }

    public Task<List<RoomSceneHistorySummaryDto>> ListSceneHistoryAsync(RoomData room)
        => _apiController.RpRoomSceneHistoryList(new RoomDto(room));

    public Task<RoomSceneHistoryDto> GetSceneHistoryAsync(RoomData room, string historyId)
        => _apiController.RpRoomSceneHistoryGet(new RoomSceneHistoryRequestDto
        {
            Room = room,
            HistoryId = historyId,
        });

    public async Task RollDiceAsync(RoomData room, int count, int sides, int modifier, string? label)
    {
        ArgumentNullException.ThrowIfNull(room);
        var stamped = await _apiController.RpRoomRollDice(new RoomDiceRollRequestDto
        {
            Room = room,
            Count = count,
            Sides = sides,
            Modifier = modifier,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
        }).ConfigureAwait(false);
        var key = new ConversationKey(ConversationKind.Room, room.RoomId);
        var entry = Store.AppendServerStamped(key, stamped, countUnread: false);
        if (entry != null)
        {
            Mediator.Publish(new ChatOutgoingStampedMessage(key, entry));
        }
    }

    public async Task<RoomData> SetTurnOrderAsync(RoomData room, bool enabled, IReadOnlyCollection<string> userUids)
    {
        var updated = await _apiController.RpRoomSetTurnOrder(new RoomTurnOrderUpdateDto
        {
            Room = room,
            Enabled = enabled,
            UserUids = userUids.ToList(),
        }).ConfigureAwait(false);
        ApplyRoomUpdate(updated.Room);
        return updated.Room;
    }

    public async Task<RoomData> AdvanceTurnAsync(RoomData room)
    {
        ArgumentNullException.ThrowIfNull(room);
        var turn = room.Scene?.TurnState ?? throw new InvalidOperationException("This room has no active turn order.");
        var updated = await _apiController.RpRoomAdvanceTurn(new RoomTurnAdvanceDto
        {
            Room = room,
            ExpectedIndex = turn.CurrentIndex,
        }).ConfigureAwait(false);
        ApplyRoomUpdate(updated.Room);
        return updated.Room;
    }

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

    private void ResetConnectionState()
    {
        lock (_activeRoomsLock)
        {
            _activeRooms.Clear();
        }
        lock (_roomTurnLock)
        {
            _roomTurns.Clear();
        }

        Store.InvalidateHistory();
        QueueRefresh();
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
            foreach (var pair in _pairManager.DirectPairs.Where(static pair => pair.IsMutualDirectPair))
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

            var roomList = await _apiController.RoomList().ConfigureAwait(false);
            var counts = await _apiController.RoomListUserCounts().ConfigureAwait(false);
            foreach (var listedRoom in roomList)
            {
                SeedRoomTurn(listedRoom.Room);
            }
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
        }
        finally
        {
            _refreshGate.Release();
        }

        await RestoreChangedHistoriesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreChangedHistoriesAsync(CancellationToken cancellationToken)
    {
        await _historyRestoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_configService.Current.ChatEnabled || !_apiController.IsConnected)
            {
                return;
            }

            if (!_apiController.SupportsChatHistoryDigest)
            {
                await RestoreLegacyHistoriesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var digest = await _apiController.ChatGetHistoryDigest().ConfigureAwait(false);
            var snapshot = Store.Snapshot;
            var activeConversation = snapshot.ActiveConversation;
            var conversations = snapshot.Conversations.ToDictionary(conversation => conversation.Key);
            var storedHeads = new Dictionary<ConversationKey, string>(
                _chatSyncState.GetHistoryHeads(_serverRegistry.CurrentApiUrl));
            var digestHeads = new Dictionary<ConversationKey, ChatHistoryDigestEntryDto>();
            foreach (var entry in digest.Conversations)
            {
                if (TryMapDigestKey(entry, out var key) && conversations.ContainsKey(key))
                {
                    digestHeads[key] = entry;
                }
            }

            var candidates = digestHeads
                .Select(entry =>
                {
                    var conversation = conversations[entry.Key];
                    var localHead = LatestMessageId(conversation);
                    storedHeads.TryGetValue(entry.Key, out var storedHead);
                    var previousHead = storedHead ?? localHead;
                    return new HistoryRestoreCandidate(
                        entry.Key,
                        entry.Value,
                        previousHead,
                        conversation.Entries.Count > 0);
                })
                .Where(candidate => !string.Equals(candidate.Digest.MessageId,
                    storedHeads.GetValueOrDefault(candidate.Key) ?? LatestMessageId(conversations[candidate.Key]),
                    StringComparison.Ordinal))
                .OrderBy(candidate => candidate.Key.Kind switch
                {
                    ConversationKind.Direct when candidate.HasLocalMessages => 0,
                    ConversationKind.Direct => 1,
                    ConversationKind.Syncshell when candidate.HasLocalMessages => 2,
                    ConversationKind.Syncshell => 3,
                    _ => 4,
                })
                .ThenBy(candidate => candidate.Key == activeConversation ? 0 : 1)
                .ToArray();

            for (var index = 0; index < candidates.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_apiController.IsConnected)
                {
                    return;
                }

                if (index > 0)
                {
                    await Task.Delay(HistoryRestoreInterval, cancellationToken).ConfigureAwait(false);
                }

                var candidate = candidates[index];
                if (!await EnsureHistoryAsync(candidate.Key, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                SurfaceOfflineMessages(candidate);
                storedHeads[candidate.Key] = candidate.Digest.MessageId;
            }

            if (_apiController.IsConnected)
            {
                var updatedHeads = digestHeads.ToDictionary(entry => entry.Key, entry => entry.Value.MessageId);
                foreach (var candidate in candidates.Where(candidate =>
                             !storedHeads.TryGetValue(candidate.Key, out var head)
                             || !string.Equals(head, candidate.Digest.MessageId, StringComparison.Ordinal)))
                {
                    if (storedHeads.TryGetValue(candidate.Key, out var previousHead))
                    {
                        updatedHeads[candidate.Key] = previousHead;
                    }
                    else
                    {
                        updatedHeads.Remove(candidate.Key);
                    }
                }

                var currentConversations = Store.Snapshot.Conversations.ToDictionary(conversation => conversation.Key);
                foreach (var currentHead in _chatSyncState.GetHistoryHeads(_serverRegistry.CurrentApiUrl))
                {
                    if (!digestHeads.TryGetValue(currentHead.Key, out var digestHead)
                        || !currentConversations.TryGetValue(currentHead.Key, out var conversation))
                    {
                        continue;
                    }

                    var currentEntry = conversation.Entries.FirstOrDefault(entry =>
                        string.Equals(entry.MessageId, currentHead.Value, StringComparison.Ordinal));
                    if (currentEntry != null
                        && currentEntry.Timestamp.ToUnixTimeSeconds() >= digestHead.Timestamp)
                    {
                        updatedHeads[currentHead.Key] = currentHead.Value;
                    }
                }

                _chatSyncState.ReplaceHistoryHeads(_serverRegistry.CurrentApiUrl, updatedHeads);
            }
        }
        finally
        {
            _historyRestoreGate.Release();
        }
    }

    private async Task RestoreLegacyHistoriesAsync(CancellationToken cancellationToken)
    {
        var snapshot = Store.Snapshot;
        var activeConversation = snapshot.ActiveConversation;
        var candidates = snapshot.Conversations
            .Where(conversation => conversation.Key.Kind is ConversationKind.Direct or ConversationKind.Syncshell
                                   && !conversation.HistoryLoaded)
            .OrderBy(conversation => conversation.Key.Kind switch
            {
                ConversationKind.Direct when conversation.Entries.Count > 0 => 0,
                ConversationKind.Direct => 1,
                ConversationKind.Syncshell when conversation.Entries.Count > 0 => 2,
                ConversationKind.Syncshell => 3,
                _ => 4,
            })
            .ThenBy(conversation => conversation.Key == activeConversation ? 0 : 1)
            .Select(conversation => conversation.Key)
            .ToArray();
        for (var index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_apiController.IsConnected)
            {
                return;
            }

            if (index > 0)
            {
                await Task.Delay(HistoryRestoreInterval, cancellationToken).ConfigureAwait(false);
            }

            await EnsureHistoryAsync(candidates[index], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> EnsureHistoryAsync(ConversationKey key, CancellationToken cancellationToken)
    {
        try
        {
            await Store.EnsureHistory(key, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HubException ex)
        {
            LogHistoryRestoreFailed(Logger, key, ex);
            return false;
        }
    }

    private void SurfaceOfflineMessages(HistoryRestoreCandidate candidate)
    {
        if (string.IsNullOrEmpty(candidate.PreviousMessageId))
        {
            return;
        }

        var conversation = Store.Snapshot.Conversations.FirstOrDefault(entry => entry.Key == candidate.Key);
        if (conversation == null)
        {
            return;
        }

        var messages = conversation.Entries
            .Where(entry => entry.Kind == ChatEntryKind.Message && !string.IsNullOrEmpty(entry.MessageId))
            .ToArray();
        var previousIndex = Array.FindIndex(messages,
            entry => string.Equals(entry.MessageId, candidate.PreviousMessageId, StringComparison.Ordinal));
        var digestIndex = Array.FindIndex(messages,
            entry => string.Equals(entry.MessageId, candidate.Digest.MessageId, StringComparison.Ordinal));
        if (digestIndex < 0)
        {
            return;
        }

        var incoming = previousIndex >= 0 && previousIndex < digestIndex
            ? messages[(previousIndex + 1)..(digestIndex + 1)]
                .Where(entry => !string.Equals(entry.SenderUid, _identityResolver.SelfUid, StringComparison.Ordinal))
                .ToArray()
            : messages
                .Where((entry, index) => index == digestIndex
                                         && !string.Equals(entry.SenderUid, _identityResolver.SelfUid, StringComparison.Ordinal))
                .ToArray();
        if (incoming.Length == 0)
        {
            return;
        }

        Store.AddUnread(candidate.Key, incoming.Length);
        Mediator.Publish(new ChatIncomingAppendedMessage(candidate.Key, incoming[^1]));
    }

    private static bool TryMapDigestKey(ChatHistoryDigestEntryDto entry, out ConversationKey key)
    {
        key = entry.Kind switch
        {
            ChatHistoryConversationKind.Direct => new ConversationKey(ConversationKind.Direct, entry.Id),
            ChatHistoryConversationKind.Group => new ConversationKey(ConversationKind.Syncshell, entry.Id),
            _ => default,
        };
        return entry.Kind is ChatHistoryConversationKind.Direct or ChatHistoryConversationKind.Group;
    }

    private static string? LatestMessageId(ConversationSnapshot conversation)
        => conversation.Entries
            .Where(entry => entry.Kind == ChatEntryKind.Message && !string.IsNullOrEmpty(entry.MessageId))
            .OrderByDescending(entry => entry.Timestamp)
            .ThenByDescending(entry => entry.MessageId, StringComparer.Ordinal)
            .Select(entry => entry.MessageId)
            .FirstOrDefault();

    private void Receive(ConversationKey key, ChatMessageDto message)
    {
        if (!_configService.Current.ChatEnabled)
        {
            return;
        }

        EnsureIncomingMetadata(key, message);
        var entry = Store.AppendIncoming(key, message, suppressUnread: message.DiceRoll != null);
        if (entry != null)
        {
            TrackHistoryHead(key, entry.MessageId);
            Mediator.Publish(new ChatIncomingAppendedMessage(key, entry));
        }
    }

    private void ApplyRoomUpdate(RoomData room)
    {
        var wasScene = _rooms.TryGet(room.RoomId, out var previous) && previous.Scene?.IsScene == true;
        var isScene = room.Scene?.IsScene == true;
        RoomTurnStateDto? before;
        var after = CloneTurn(room.Scene?.TurnState);
        lock (_roomTurnLock)
        {
            _roomTurns.TryGetValue(room.RoomId, out before);
            _roomTurns[room.RoomId] = after;
        }
        _rooms.Upsert(room);
        if (wasScene != isScene)
        {
            Store.ResetConversation(new ConversationKey(ConversationKind.Room, room.RoomId));
            _ = _backgroundTasks.Run(() => RefreshRoomMembersAsync(room), nameof(RefreshRoomMembersAsync));
        }
        if (!TurnChanged(before, after))
        {
            return;
        }

        var text = DescribeTurn(room.RoomId, before, after);
        var key = new ConversationKey(ConversationKind.Room, room.RoomId);
        var entry = Store.AppendTurnEvent(key, $"room-turn:{Guid.NewGuid():N}", text, after ?? new RoomTurnStateDto());
        if (entry != null)
        {
            Mediator.Publish(new ChatIncomingAppendedMessage(key, entry));
        }
    }

    private void SeedRoomTurn(RoomData room)
    {
        lock (_roomTurnLock)
        {
            _roomTurns.TryAdd(room.RoomId, CloneTurn(room.Scene?.TurnState));
        }
    }

    private static RoomTurnStateDto? CloneTurn(RoomTurnStateDto? turn)
        => turn == null ? null : new RoomTurnStateDto
        {
            Enabled = turn.Enabled,
            CurrentIndex = turn.CurrentIndex,
            UserUids = [.. turn.UserUids],
        };

    private static bool TurnChanged(RoomTurnStateDto? before, RoomTurnStateDto? after)
    {
        if ((before?.Enabled ?? false) != (after?.Enabled ?? false))
        {
            return true;
        }

        if (after?.Enabled != true)
        {
            return false;
        }

        return before?.CurrentIndex != after.CurrentIndex
            || before.UserUids.Count != after.UserUids.Count
            || !before.UserUids.SequenceEqual(after.UserUids, StringComparer.Ordinal);
    }

    private string DescribeTurn(string roomId, RoomTurnStateDto? before, RoomTurnStateDto? after)
    {
        if (after?.Enabled != true || after.UserUids.Count == 0)
        {
            return "Turn order ended.";
        }

        var current = after.UserUids[Math.Clamp(after.CurrentIndex, 0, after.UserUids.Count - 1)];
        var member = _rooms.GetMembers(roomId).FirstOrDefault(candidate => string.Equals(candidate.User.UID, current, StringComparison.Ordinal));
        var name = member == null ? current : member.SceneNickname ?? _identityResolver.Resolve(member.User).Name;
        return before?.Enabled == true ? $"Turn advanced to {name}." : $"Turn order started. Current turn: {name}.";
    }

    private static string MembershipEventId(long sessionSequence)
        => sessionSequence > 0
            ? $"room-membership:{sessionSequence}"
            : $"room-membership:{Guid.NewGuid():N}";

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
        TrackHistoryHead(eventArgs.Key, eventArgs.Entry.MessageId);
        Mediator.Publish(new ChatOutgoingStampedMessage(eventArgs.Key, eventArgs.Entry));
    }

    private void TrackHistoryHead(ConversationKey key, string messageId)
    {
        if (key.Kind is ConversationKind.Direct or ConversationKind.Syncshell
            && !string.IsNullOrEmpty(messageId))
        {
            _chatSyncState.SetHistoryHead(_serverRegistry.CurrentApiUrl, key, messageId);
        }
    }

    protected override void Dispose(bool disposing)
    {
        _chatPreferences.ConfigChanged -= OnPreferencesChanged;
        _configService.ConfigChanged -= OnGlobalConfigChanged;
        Store.MessageSent -= OnMessageSent;
        _refreshGate.Dispose();
        _historyRestoreGate.Dispose();
        base.Dispose(disposing);
    }

    private sealed record HistoryRestoreCandidate(
        ConversationKey Key,
        ChatHistoryDigestEntryDto Digest,
        string? PreviousMessageId,
        bool HasLocalMessages);
}
