using Snowcloak.API.Data;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI.SignalR.Utils;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public async Task GroupBanUser(GroupPairDto dto, string reason)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(GroupBanUser), dto, reason).ConfigureAwait(false);
    }

    public async Task GroupChangeGroupPermissionState(GroupPermissionDto dto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(GroupChangeGroupPermissionState), dto).ConfigureAwait(false);
    }

    public Task GroupChangeIndividualPermissionState(GroupPairUserPermissionDto dto)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync(nameof(GroupChangeIndividualPermissionState), dto);
    }

    public async Task GroupChangeOwnership(GroupPairDto groupPair)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(GroupChangeOwnership), groupPair).ConfigureAwait(false);
    }

    public async Task<bool> GroupChangePassword(GroupPasswordDto groupPassword)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<bool>(nameof(GroupChangePassword), groupPassword).ConfigureAwait(false);
    }

    public async Task GroupChatSendMsg(GroupDto group, ChatMessage message)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(GroupChatSendMsg), group, message).ConfigureAwait(false);
    }

    public Task<List<SignedChatMessage>> GroupChatGetHistory(GroupDto group)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<List<SignedChatMessage>>(nameof(GroupChatGetHistory), group);
    }

    public Task<List<GroupChatMemberStateDto>> GroupChatJoin(GroupDto group)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<List<GroupChatMemberStateDto>>(nameof(GroupChatJoin), group);
    }

    public Task GroupChatLeave(GroupDto group)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync(nameof(GroupChatLeave), group);
    }

    public Task<List<GroupChatMemberStateDto>> GroupChatGetMembers(GroupDto group)
    {
        CheckConnection();
        return _snowHub!.InvokeAsync<List<GroupChatMemberStateDto>>(nameof(GroupChatGetMembers), group);
    }

    public async Task GroupClear(GroupDto group)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(GroupClear), group).ConfigureAwait(false);
    }

    public async Task<GroupPasswordDto> GroupCreate()
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<GroupPasswordDto>(nameof(GroupCreate)).ConfigureAwait(false);
    }

    public async Task<List<string>> GroupCreateTempInvite(GroupDto group, int amount)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<List<string>>(nameof(GroupCreateTempInvite), group, amount).ConfigureAwait(false);
    }

    public async Task GroupDelete(GroupDto group)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(GroupDelete), group).ConfigureAwait(false);
    }

    public async Task<GroupAuditPageDto> GroupGetAuditLog(GroupAuditQueryDto query)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<GroupAuditPageDto>(nameof(GroupGetAuditLog), query).ConfigureAwait(false);
    }

    public async Task<List<BannedGroupUserDto>> GroupGetBannedUsers(GroupDto group)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<List<BannedGroupUserDto>>(nameof(GroupGetBannedUsers), group).ConfigureAwait(false);
    }

    public async Task<bool> GroupJoin(GroupPasswordDto passwordedGroup)
    {
        CheckConnection();

        return await _snowHub!.InvokeAsync<bool>(nameof(GroupJoin), passwordedGroup).ConfigureAwait(false);
    }
    
    public async Task GroupLeave(GroupDto group)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(GroupLeave), group).ConfigureAwait(false);
    }

    public async Task GroupRemoveUser(GroupPairDto groupPair)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(GroupRemoveUser), groupPair).ConfigureAwait(false);
    }

    public async Task<bool> GroupSetMemberLabels(GroupMemberLabelsDto groupPair)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<bool>(nameof(GroupSetMemberLabels), groupPair).ConfigureAwait(false);
    }

    public async Task GroupSetUserInfo(GroupPairUserInfoDto groupPair)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(GroupSetUserInfo), groupPair).ConfigureAwait(false);
    }

    public async Task<int> GroupPrune(GroupDto group, int days, bool execute)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<int>(nameof(GroupPrune), group, days, execute).ConfigureAwait(false);
    }

    public async Task<List<GroupFullInfoDto>> GroupsGetAll()
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<List<GroupFullInfoDto>>(nameof(GroupsGetAll)).ConfigureAwait(false);
    }

    public async Task<List<GroupPairFullInfoDto>> GroupsGetUsersInGroup(GroupDto group)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<List<GroupPairFullInfoDto>>(nameof(GroupsGetUsersInGroup), group).ConfigureAwait(false);
    }

    public async Task GroupUnbanUser(GroupPairDto groupPair)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(GroupUnbanUser), groupPair).ConfigureAwait(false);
    }

    private void CheckConnection()
    {
        if (ServerState is not (ServerState.Connected or ServerState.Connecting or ServerState.Reconnecting)) throw new InvalidDataException("Not connected");
    }
}
