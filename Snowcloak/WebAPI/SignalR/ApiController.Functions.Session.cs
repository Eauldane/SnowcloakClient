using MessagePack;
using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.Manifest;
using Snowcloak.API.Dto.Session;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Protocol;
using Snowcloak.WebAPI.SignalR;
using ConnectionServerState = Snowcloak.WebAPI.SignalR.Utils.ServerState;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public async Task<SessionResumeResponseDto> SessionResume(SessionResumeRequestDto dto)
    {
        var hub = _snowHub;
        if (hub == null)
        {
            return new SessionResumeResponseDto { Result = SessionResumeResult.ResyncRequired };
        }

        return await hub.InvokeAsync<SessionResumeResponseDto>(nameof(SessionResume), dto).ConfigureAwait(false);
    }

    public async Task SessionEnd()
    {
        var hub = _snowHub;
        if (hub == null || hub.State != HubConnectionState.Connected)
        {
            return;
        }

        await hub.InvokeAsync(nameof(SessionEnd)).ConfigureAwait(false);
    }

    internal Task RouteSessionEvent<T>(T payload, Func<T, Task> handler)
        where T : ISequencedSessionEvent
    {
        return _sessionResumeState.RouteAsync(payload, () => handler(payload));
    }

    private async Task<bool> TryResumeSessionAsync(ConnectionDto connection)
    {
        if (!connection.ServerCapabilities.HasFlag(HubCapability.ResumableSessions)
            || string.IsNullOrEmpty(connection.SessionId)
            || !string.Equals(_sessionResumeState.SessionId, connection.SessionId, StringComparison.Ordinal))
        {
            _sessionResumeState.AbandonBuffer();
            _sessionResumeState.Establish(connection.SessionId);
            return false;
        }

        _connectionLifecycle.MovePhase(ConnectionLifecyclePhase.Resuming);
        ServerState = ConnectionServerState.Resuming;
        var response = await SessionResume(_sessionResumeState.CreateRequest()).ConfigureAwait(false);
        if (response.Result != SessionResumeResult.Resumed)
        {
            ServerState = ConnectionServerState.Connected;
            _sessionResumeState.AbandonBuffer();
            _sessionResumeState.Establish(connection.SessionId);
            return false;
        }

        await _sessionResumeState.CompleteAsync(response, ApplyReplayEventAsync).ConfigureAwait(false);
        _connectionLifecycle.MovePhase(ConnectionLifecyclePhase.Resynced);
        ServerState = ConnectionServerState.Connected;
        return true;
    }

    private Task ApplyReplayEventAsync(SessionReplayEventDto entry)
    {
        return entry.Kind switch
        {
            SessionEventKind.UserSendOnline => Client_UserSendOnline(Deserialize<OnlineUserIdentDto>(entry)),
            SessionEventKind.UserSendOffline => Client_UserSendOffline(Deserialize<UserDto>(entry)),
            SessionEventKind.UserAddClientPair => Client_UserAddClientPair(Deserialize<UserPairDto>(entry)),
            SessionEventKind.UserRemoveClientPair => Client_UserRemoveClientPair(Deserialize<UserDto>(entry)),
            SessionEventKind.UserUpdateSelfPairPermissions => Client_UserUpdateSelfPairPermissions(Deserialize<UserPermissionsDto>(entry)),
            SessionEventKind.UserUpdateOtherPairPermissions => Client_UserUpdateOtherPairPermissions(Deserialize<UserPermissionsDto>(entry)),
            SessionEventKind.UserReceiveManifest => Client_UserReceiveManifest(Deserialize<ManifestNotificationDto>(entry)),
            SessionEventKind.UserUpdateProfile => Client_UserUpdateProfile(Deserialize<UserDto>(entry)),
            SessionEventKind.CharacterProfileChanged => Client_CharacterProfileChanged(Deserialize<CharacterProfileChangedDto>(entry)),
            SessionEventKind.GroupSendInfo => Client_GroupSendInfo(Deserialize<GroupInfoDto>(entry)),
            SessionEventKind.GroupSendFullInfo => Client_GroupSendFullInfo(Deserialize<GroupFullInfoDto>(entry)),
            SessionEventKind.GroupDelete => Client_GroupDelete(Deserialize<GroupDto>(entry)),
            SessionEventKind.GroupPairJoined => Client_GroupPairJoined(Deserialize<GroupPairFullInfoDto>(entry)),
            SessionEventKind.GroupPairLeft => Client_GroupPairLeft(Deserialize<GroupPairDto>(entry)),
            SessionEventKind.GroupChangePermissions => Client_GroupChangePermissions(Deserialize<GroupPermissionDto>(entry)),
            SessionEventKind.GroupPairChangePermissions => Client_GroupPairChangePermissions(Deserialize<GroupPairUserPermissionDto>(entry)),
            SessionEventKind.GroupPairChangeLabels => Client_GroupPairChangeLabels(Deserialize<GroupMemberLabelsDto>(entry)),
            SessionEventKind.GroupPairChangeUserInfo => Client_GroupPairChangeUserInfo(Deserialize<GroupPairUserInfoDto>(entry)),
            SessionEventKind.ChannelMemberJoined => Client_ChannelMemberJoined(Deserialize<ChannelMemberJoinedDto>(entry)),
            SessionEventKind.ChannelMemberLeft => Client_ChannelMemberLeft(Deserialize<ChannelMemberLeftDto>(entry)),
            SessionEventKind.GroupChatMemberState => Client_GroupChatMemberState(Deserialize<GroupChatMemberStateDto>(entry)),
            SessionEventKind.UserReceiveApplicationReceipt => Client_UserReceiveApplicationReceipt(Deserialize<PairApplicationReceiptDto>(entry)),
            _ => Task.CompletedTask,
        };
    }

    private static T Deserialize<T>(SessionReplayEventDto entry)
    {
        return MessagePackSerializer.Deserialize<T>(entry.Payload);
    }
}
