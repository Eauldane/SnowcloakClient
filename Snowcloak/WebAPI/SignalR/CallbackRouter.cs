using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;

namespace Snowcloak.WebAPI.SignalR;

internal static class CallbackRouter
{
    public static void Register(HubConnection hub, ApiController api)
    {
        SystemCallbacks.Register(hub, api);
        PairCallbacks.Register(hub, api);
        GroupCallbacks.Register(hub, api);
        ChatCallbacks.Register(hub, api);
        GposeCallbacks.Register(hub, api);
    }
}

internal static class SystemCallbacks
{
    public static void Register(HubConnection hub, ApiController api)
    {
        hub.On<MessageSeverity, string>(nameof(ApiController.Client_ReceiveServerMessage), api.Client_ReceiveServerMessage);
        hub.On<string?>(nameof(ApiController.Client_ReceiveNews), api.Client_ReceiveNews);
        hub.On<SystemInfoDto>(nameof(ApiController.Client_UpdateSystemInfo), api.Client_UpdateSystemInfo);
    }
}

internal static class PairCallbacks
{
    public static void Register(HubConnection hub, ApiController api)
    {
        hub.On<UserDto>(nameof(ApiController.Client_UserSendOffline), api.Client_UserSendOffline);
        hub.On<UserPairDto>(nameof(ApiController.Client_UserAddClientPair), api.Client_UserAddClientPair);
        hub.On<OnlineUserCharaDataDto>(nameof(ApiController.Client_UserReceiveCharacterData), api.Client_UserReceiveCharacterData);
        hub.On<PairApplicationReceiptDto>(nameof(ApiController.Client_UserReceiveApplicationReceipt), api.Client_UserReceiveApplicationReceipt);
        hub.On<UserDto>(nameof(ApiController.Client_UserRemoveClientPair), api.Client_UserRemoveClientPair);
        hub.On<OnlineUserIdentDto>(nameof(ApiController.Client_UserSendOnline), api.Client_UserSendOnline);
        hub.On<UserPermissionsDto>(nameof(ApiController.Client_UserUpdateOtherPairPermissions), api.Client_UserUpdateOtherPairPermissions);
        hub.On<UserPermissionsDto>(nameof(ApiController.Client_UserUpdateSelfPairPermissions), api.Client_UserUpdateSelfPairPermissions);
        hub.On<UserDto>(nameof(ApiController.Client_UserReceiveUploadStatus), api.Client_UserReceiveUploadStatus);
        hub.On<UserDto>(nameof(ApiController.Client_UserUpdateProfile), api.Client_UserUpdateProfile);
        hub.On<CharacterProfileChangedDto>(nameof(ApiController.Client_CharacterProfileChanged), api.Client_CharacterProfileChanged);
        hub.On<List<PairingAvailabilityDto>>(nameof(ApiController.Client_UserPairingAvailability), api.Client_UserPairingAvailability);
        hub.On<PairingRequestDto>(nameof(ApiController.Client_UserPairingRequest), api.Client_UserPairingRequest);
        hub.On<PairingAvailabilityResumeRequestDto>(nameof(ApiController.Client_RequestPairingAvailabilitySubscription), api.Client_RequestPairingAvailabilitySubscription);
        hub.On<PairingAvailabilityDeltaDto>(nameof(ApiController.Client_UserPairingAvailabilityDelta), api.Client_UserPairingAvailabilityDelta);
    }
}

internal static class GroupCallbacks
{
    public static void Register(HubConnection hub, ApiController api)
    {
        hub.On<GroupPermissionDto>(nameof(ApiController.Client_GroupChangePermissions), api.Client_GroupChangePermissions);
        hub.On<GroupDto>(nameof(ApiController.Client_GroupDelete), api.Client_GroupDelete);
        hub.On<GroupMemberLabelsDto>(nameof(ApiController.Client_GroupPairChangeLabels), api.Client_GroupPairChangeLabels);
        hub.On<GroupPairUserInfoDto>(nameof(ApiController.Client_GroupPairChangeUserInfo), api.Client_GroupPairChangeUserInfo);
        hub.On<GroupPairFullInfoDto>(nameof(ApiController.Client_GroupPairJoined), api.Client_GroupPairJoined);
        hub.On<GroupPairDto>(nameof(ApiController.Client_GroupPairLeft), api.Client_GroupPairLeft);
        hub.On<GroupFullInfoDto>(nameof(ApiController.Client_GroupSendFullInfo), api.Client_GroupSendFullInfo);
        hub.On<GroupInfoDto>(nameof(ApiController.Client_GroupSendInfo), api.Client_GroupSendInfo);
        hub.On<GroupPairUserPermissionDto>(nameof(ApiController.Client_GroupPairChangePermissions), api.Client_GroupPairChangePermissions);
    }
}

internal static class ChatCallbacks
{
    public static void Register(HubConnection hub, ApiController api)
    {
        hub.On<UserChatMsgDto>(nameof(ApiController.Client_UserChatMsg), api.Client_UserChatMsg);
        hub.On<GroupChatMsgDto>(nameof(ApiController.Client_GroupChatMsg), api.Client_GroupChatMsg);
        hub.On<GroupChatMemberStateDto>(nameof(ApiController.Client_GroupChatMemberState), api.Client_GroupChatMemberState);
        hub.On<ChannelChatMsgDto>(nameof(ApiController.Client_ChannelChatMsg), api.Client_ChannelChatMsg);
        hub.On<ChannelMemberJoinedDto>(nameof(ApiController.Client_ChannelMemberJoined), api.Client_ChannelMemberJoined);
        hub.On<ChannelMemberLeftDto>(nameof(ApiController.Client_ChannelMemberLeft), api.Client_ChannelMemberLeft);
    }
}

internal static class GposeCallbacks
{
    public static void Register(HubConnection hub, ApiController api)
    {
        hub.On<UserData>(nameof(ApiController.Client_GposeLobbyJoin), api.Client_GposeLobbyJoin);
        hub.On<UserData>(nameof(ApiController.Client_GposeLobbyLeave), api.Client_GposeLobbyLeave);
        hub.On<CharaDataDownloadDto>(nameof(ApiController.Client_GposeLobbyPushCharacterData), api.Client_GposeLobbyPushCharacterData);
        hub.On<UserData, PoseData>(nameof(ApiController.Client_GposeLobbyPushPoseData), api.Client_GposeLobbyPushPoseData);
        hub.On<UserData, WorldData>(nameof(ApiController.Client_GposeLobbyPushWorldData), api.Client_GposeLobbyPushWorldData);
    }
}
