using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;
using Snowcloak.Core.Chat;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.WebAPI;

namespace Snowcloak.Services.Chat;

public sealed class ChatTransport : IChatTransport
{
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly PairManager _pairManager;
    private readonly ChatRoomRegistry _rooms;

    public ChatTransport(ApiController apiController, DalamudUtilService dalamudUtil, PairManager pairManager,
        ChatRoomRegistry rooms)
    {
        _apiController = apiController;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _rooms = rooms;
    }

    public async Task<ChatMessageDto> SendAsync(ConversationKey key, string text, RpChatMode rpMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = new ChatMessage
        {
            SenderName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false),
            SenderHomeWorldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false),
            PayloadContent = ChatMessageCodec.Encode(text),
            RpMode = rpMode,
        };

        return await (key.Kind switch
        {
            ConversationKind.Direct => _apiController.UserChatSendMsg(new UserDto(ResolveMutualUser(key.Id)), message),
            ConversationKind.Syncshell => _apiController.GroupChatSendMsg(new GroupDto(ResolveGroup(key.Id)), message),
            ConversationKind.Room => _apiController.RoomChatSendMsg(new RoomDto(ResolveRoom(key.Id)), message),
            _ => throw new InvalidOperationException("Unknown conversation kind"),
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatMessageDto>> GetHistoryAsync(ConversationKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return key.Kind switch
        {
            ConversationKind.Direct when TryResolveMutualUser(key.Id, out var user)
                => await _apiController.UserChatGetHistory(new UserDto(user)).ConfigureAwait(false),
            ConversationKind.Direct => [],
            ConversationKind.Syncshell when TryResolveGroup(key.Id, out var group)
                => await _apiController.GroupChatGetHistory(new GroupDto(group)).ConfigureAwait(false),
            ConversationKind.Syncshell => [],
            ConversationKind.Room => await _apiController.RoomChatGetHistory(new RoomDto(ResolveRoom(key.Id))).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown conversation kind"),
        };
    }

    private UserData ResolveMutualUser(string uid)
        => TryResolveMutualUser(uid, out var user)
            ? user
            : throw new InvalidOperationException("Direct messages require a mutual pairing.");

    private bool TryResolveMutualUser(string uid, out UserData user)
    {
        var pair = _pairManager.GetPairByUID(uid);
        user = pair?.UserData ?? new UserData(uid);
        return pair?.IsMutualDirectPair == true;
    }

    private GroupData ResolveGroup(string gid)
        => TryResolveGroup(gid, out var group)
            ? group
            : throw new InvalidOperationException("Syncshell is no longer available.");

    private bool TryResolveGroup(string gid, out GroupData group)
    {
        var resolved = _pairManager.Groups.Keys.FirstOrDefault(candidate => string.Equals(candidate.GID, gid, StringComparison.Ordinal));
        group = resolved ?? new GroupData(gid);
        return resolved != null;
    }

    private RoomData ResolveRoom(string roomId)
        => _rooms.TryGet(roomId, out var room)
            ? room
            : throw new InvalidOperationException("Room is no longer available");
}
