using Snowcloak.API.Data;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.Chat;
using Microsoft.AspNetCore.SignalR.Client;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public async Task UserSetConnectionMode(ConnectionModeDto mode)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(UserSetConnectionMode), mode).ConfigureAwait(false);
    }

    public async Task<ChannelDto> ChannelCreate(ChannelCreateDto createDto)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<ChannelDto>(nameof(ChannelCreate), createDto).ConfigureAwait(false);
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

    public async Task ChannelKick(ChannelKickDto kickDto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelKick), kickDto).ConfigureAwait(false);
    }

    public async Task ChannelBan(ChannelBanDto banDto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelBan), banDto).ConfigureAwait(false);
    }

    public async Task ChannelUnban(ChannelUnbanDto unbanDto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelUnban), unbanDto).ConfigureAwait(false);
    }

    public async Task ChannelSetMode(ChannelModeUpdateDto modeUpdateDto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelSetMode), modeUpdateDto).ConfigureAwait(false);
    }

    public async Task ChannelSetRole(ChannelRoleUpdateDto roleUpdateDto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelSetRole), roleUpdateDto).ConfigureAwait(false);
    }

    public async Task ChannelSetTopic(ChannelTopicUpdateDto topicUpdateDto)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(ChannelSetTopic), topicUpdateDto).ConfigureAwait(false);
    }

    public async Task<List<ChannelDto>> ChannelList()
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<List<ChannelDto>>(nameof(ChannelList)).ConfigureAwait(false);
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
