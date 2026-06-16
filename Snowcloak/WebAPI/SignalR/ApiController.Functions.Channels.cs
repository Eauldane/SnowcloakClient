using Snowcloak.API.Data;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.Chat;
using Microsoft.AspNetCore.SignalR.Client;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public async Task UserSetConnectionMode(ConnectionModeDto connectionMode)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(UserSetConnectionMode), connectionMode).ConfigureAwait(false);
    }

    public async Task<ChannelDto> ChannelCreate(ChannelCreateDto dto)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<ChannelDto>(nameof(ChannelCreate), dto).ConfigureAwait(false);
    }

    public async Task<ChannelMemberDto?> ChannelJoin(ChannelDto channel)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<ChannelMemberDto?>(nameof(ChannelJoin), channel).ConfigureAwait(false);
    }

    public async Task ChannelLeave(ChannelDto channel)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelLeave), channel).ConfigureAwait(false);
    }

    public async Task ChannelKick(ChannelKickDto dto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelKick), dto).ConfigureAwait(false);
    }

    public async Task ChannelBan(ChannelBanDto dto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelBan), dto).ConfigureAwait(false);
    }

    public async Task ChannelUnban(ChannelUnbanDto dto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelUnban), dto).ConfigureAwait(false);
    }

    public async Task ChannelSetMode(ChannelModeUpdateDto dto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelSetMode), dto).ConfigureAwait(false);
    }

    public async Task ChannelSetRole(ChannelRoleUpdateDto dto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelSetRole), dto).ConfigureAwait(false);
    }

    public async Task ChannelSetTopic(ChannelTopicUpdateDto dto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelSetTopic), dto).ConfigureAwait(false);
    }

    public async Task<List<ChannelDto>> ChannelList()
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<List<ChannelDto>>(nameof(ChannelList)).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, int>> ChannelListUserCounts()
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<Dictionary<string, int>>(nameof(ChannelListUserCounts)).ConfigureAwait(false);
    }

    public async Task<List<ChannelMemberDto>> ChannelGetMembers(ChannelDto channel)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<List<ChannelMemberDto>>(nameof(ChannelGetMembers), channel).ConfigureAwait(false);
    }

    public async Task ChannelChatSendMsg(ChannelDto channel, ChatMessage message)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelChatSendMsg), channel, message).ConfigureAwait(false);
    }
}
