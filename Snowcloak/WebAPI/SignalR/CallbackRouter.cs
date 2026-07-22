using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.Manifest;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Dto.Session;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Roleplay;

namespace Snowcloak.WebAPI.SignalR;

internal static class CallbackRouter
{
    public static void Register(HubConnection hub, ApiController api)
    {
        SystemCallbacks.Register(hub, api);
        PairCallbacks.Register(hub, api);
        GroupCallbacks.Register(hub, api);
        ChatCallbacks.Register(hub, api);
        RoleplayCallbacks.Register(hub, api);
        GposeCallbacks.Register(hub, api);
    }

    public static void RegisterSession<T>(HubConnection hub, string method, ApiController api, Func<T, Task> handler)
        where T : ISequencedSessionEvent
    {
        hub.On<T>(method, payload => api.RouteSessionEvent(payload, handler));
    }
}

internal static class RoleplayCallbacks
{
    public static void Register(HubConnection hub, ApiController api)
    {
        hub.On<RpAvailabilityChangedDto>(nameof(ApiController.Client_RpAvailabilityChanged), api.Client_RpAvailabilityChanged);
        hub.On<RoomDto>(nameof(ApiController.Client_RpRoomUpdated), api.Client_RpRoomUpdated);
        hub.On<RoomInviteReceivedDto>(nameof(ApiController.Client_RpRoomInviteReceived), api.Client_RpRoomInviteReceived);
    }
}

internal static class ChatCallbacks
{
    public static void Register(HubConnection hub, ApiController api)
    {
        hub.On<UserChatMsgDto>(nameof(ApiController.Client_UserChatMsg), api.Client_UserChatMsg);
        hub.On<GroupChatMsgDto>(nameof(ApiController.Client_GroupChatMsg), api.Client_GroupChatMsg);
        hub.On<RoomChatMsgDto>(nameof(ApiController.Client_RoomChatMsg), api.Client_RoomChatMsg);
        CallbackRouter.RegisterSession<RoomMemberJoinedDto>(hub, nameof(ApiController.Client_RoomMemberJoined), api, api.Client_RoomMemberJoined);
        CallbackRouter.RegisterSession<RoomMemberLeftDto>(hub, nameof(ApiController.Client_RoomMemberLeft), api, api.Client_RoomMemberLeft);
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
        CallbackRouter.RegisterSession<UserDto>(hub, nameof(ApiController.Client_UserSendOffline), api, api.Client_UserSendOffline);
        CallbackRouter.RegisterSession<UserPairDto>(hub, nameof(ApiController.Client_UserAddClientPair), api, api.Client_UserAddClientPair);
        CallbackRouter.RegisterSession<ManifestNotificationDto>(hub, nameof(ApiController.Client_UserReceiveManifest), api, api.Client_UserReceiveManifest);
        CallbackRouter.RegisterSession<PairApplicationReceiptDto>(hub, nameof(ApiController.Client_UserReceiveApplicationReceipt), api, api.Client_UserReceiveApplicationReceipt);
        CallbackRouter.RegisterSession<UserDto>(hub, nameof(ApiController.Client_UserRemoveClientPair), api, api.Client_UserRemoveClientPair);
        CallbackRouter.RegisterSession<OnlineUserIdentDto>(hub, nameof(ApiController.Client_UserSendOnline), api, api.Client_UserSendOnline);
        CallbackRouter.RegisterSession<UserPermissionsDto>(hub, nameof(ApiController.Client_UserUpdateOtherPairPermissions), api, api.Client_UserUpdateOtherPairPermissions);
        CallbackRouter.RegisterSession<UserPermissionsDto>(hub, nameof(ApiController.Client_UserUpdateSelfPairPermissions), api, api.Client_UserUpdateSelfPairPermissions);
        hub.On<UserDto>(nameof(ApiController.Client_UserReceiveUploadStatus), api.Client_UserReceiveUploadStatus);
        CallbackRouter.RegisterSession<UserDto>(hub, nameof(ApiController.Client_UserUpdateProfile), api, api.Client_UserUpdateProfile);
        CallbackRouter.RegisterSession<CharacterProfileChangedDto>(hub, nameof(ApiController.Client_CharacterProfileChanged), api, api.Client_CharacterProfileChanged);
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
        CallbackRouter.RegisterSession<GroupPermissionDto>(hub, nameof(ApiController.Client_GroupChangePermissions), api, api.Client_GroupChangePermissions);
        CallbackRouter.RegisterSession<GroupDto>(hub, nameof(ApiController.Client_GroupDelete), api, api.Client_GroupDelete);
        CallbackRouter.RegisterSession<GroupMemberLabelsDto>(hub, nameof(ApiController.Client_GroupPairChangeLabels), api, api.Client_GroupPairChangeLabels);
        CallbackRouter.RegisterSession<GroupPairUserInfoDto>(hub, nameof(ApiController.Client_GroupPairChangeUserInfo), api, api.Client_GroupPairChangeUserInfo);
        CallbackRouter.RegisterSession<GroupPairFullInfoDto>(hub, nameof(ApiController.Client_GroupPairJoined), api, api.Client_GroupPairJoined);
        CallbackRouter.RegisterSession<GroupPairDto>(hub, nameof(ApiController.Client_GroupPairLeft), api, api.Client_GroupPairLeft);
        CallbackRouter.RegisterSession<GroupFullInfoDto>(hub, nameof(ApiController.Client_GroupSendFullInfo), api, api.Client_GroupSendFullInfo);
        CallbackRouter.RegisterSession<GroupInfoDto>(hub, nameof(ApiController.Client_GroupSendInfo), api, api.Client_GroupSendInfo);
        CallbackRouter.RegisterSession<GroupPairUserPermissionDto>(hub, nameof(ApiController.Client_GroupPairChangePermissions), api, api.Client_GroupPairChangePermissions);
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
