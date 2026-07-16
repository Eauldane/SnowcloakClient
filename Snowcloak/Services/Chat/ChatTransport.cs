using Snowcloak.API.Data;
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

    public async Task<ChatMessageDto> SendAsync(ConversationKey key, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = new ChatMessage
        {
            SenderName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false),
            SenderHomeWorldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false),
            PayloadContent = ChatMessageCodec.Encode(text),
        };

        return await (key.Kind switch
        {
            ConversationKind.Direct => _apiController.UserChatSendMsg(new UserDto(ResolveUser(key.Id)), message),
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
            ConversationKind.Direct => await _apiController.UserChatGetHistory(new UserDto(ResolveUser(key.Id))).ConfigureAwait(false),
            ConversationKind.Syncshell => await _apiController.GroupChatGetHistory(new GroupDto(ResolveGroup(key.Id))).ConfigureAwait(false),
            ConversationKind.Room => await _apiController.RoomChatGetHistory(new RoomDto(ResolveRoom(key.Id))).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown conversation kind"),
        };
    }

    private UserData ResolveUser(string uid)
        => _pairManager.GetPairByUID(uid)?.UserData ?? new UserData(uid);

    private GroupData ResolveGroup(string gid)
        => _pairManager.Groups.Keys.FirstOrDefault(group => string.Equals(group.GID, gid, StringComparison.Ordinal))
           ?? new GroupData(gid);

    private RoomData ResolveRoom(string roomId)
        => _rooms.TryGet(roomId, out var room)
            ? room
            : throw new InvalidOperationException("Room is no longer available");
}
