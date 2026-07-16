using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public Task<ChatMessageDto> UserChatSendMsg(UserDto user, ChatMessage message)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<ChatMessageDto>(nameof(UserChatSendMsg), user, message);
    }

    public Task<List<ChatMessageDto>> UserChatGetHistory(UserDto user)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<List<ChatMessageDto>>(nameof(UserChatGetHistory), user);
    }

    public Task<ChatMessageDto> GroupChatSendMsg(GroupDto group, ChatMessage message)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<ChatMessageDto>(nameof(GroupChatSendMsg), group, message);
    }

    public Task<List<ChatMessageDto>> GroupChatGetHistory(GroupDto group)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<List<ChatMessageDto>>(nameof(GroupChatGetHistory), group);
    }

    public Task<RoomDto> RoomCreate(RoomCreateDto dto)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<RoomDto>(nameof(RoomCreate), dto);
    }

    public Task<List<RoomDto>> RoomList()
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<List<RoomDto>>(nameof(RoomList));
    }

    public Task<Dictionary<string, int>> RoomListUserCounts()
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<Dictionary<string, int>>(nameof(RoomListUserCounts));
    }

    public Task<RoomMemberDto?> RoomJoin(RoomDto room)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<RoomMemberDto?>(nameof(RoomJoin), room);
    }

    public Task RoomLeave(RoomDto room)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync(nameof(RoomLeave), room);
    }

    public Task RoomInvite(RoomInviteDto dto)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync(nameof(RoomInvite), dto);
    }

    public Task RoomSetTopic(RoomTopicUpdateDto dto)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync(nameof(RoomSetTopic), dto);
    }

    public Task RoomKick(RoomKickDto dto)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync(nameof(RoomKick), dto);
    }

    public Task RoomBan(RoomBanDto dto)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync(nameof(RoomBan), dto);
    }

    public Task RoomUnban(RoomUnbanDto dto)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync(nameof(RoomUnban), dto);
    }

    public Task RoomSetRole(RoomRoleUpdateDto dto)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync(nameof(RoomSetRole), dto);
    }

    public Task<ChatMessageDto> RoomChatSendMsg(RoomDto room, ChatMessage message)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<ChatMessageDto>(nameof(RoomChatSendMsg), room, message);
    }

    public Task<List<RoomMemberDto>> RoomGetMembers(RoomDto room)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<List<RoomMemberDto>>(nameof(RoomGetMembers), room);
    }

    public Task<List<ChatMessageDto>> RoomChatGetHistory(RoomDto room)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<List<ChatMessageDto>>(nameof(RoomChatGetHistory), room);
    }
}
