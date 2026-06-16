using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public void OnGroupChangePermissions(Action<GroupPermissionDto> act) => RegisterCallback(nameof(Client_GroupChangePermissions), act);

    public void OnGroupChatMsg(Action<GroupChatMsgDto> groupChatMsgDto) => RegisterCallback(nameof(Client_GroupChatMsg), groupChatMsgDto);

    public void OnGroupChatMemberState(Action<GroupChatMemberStateDto> act) => RegisterCallback(nameof(Client_GroupChatMemberState), act);

    public void OnChannelChatMsg(Action<ChannelChatMsgDto> act) => RegisterCallback(nameof(Client_ChannelChatMsg), act);

    public void OnGroupPairChangePermissions(Action<GroupPairUserPermissionDto> act) => RegisterCallback(nameof(Client_GroupPairChangePermissions), act);

    public void OnGroupPairChangeLabels(Action<GroupMemberLabelsDto> act) => RegisterCallback(nameof(Client_GroupPairChangeLabels), act);

    public void OnGroupDelete(Action<GroupDto> act) => RegisterCallback(nameof(Client_GroupDelete), act);

    public void OnGroupPairChangeUserInfo(Action<GroupPairUserInfoDto> act) => RegisterCallback(nameof(Client_GroupPairChangeUserInfo), act);

    public void OnGroupPairJoined(Action<GroupPairFullInfoDto> act) => RegisterCallback(nameof(Client_GroupPairJoined), act);

    public void OnGroupPairLeft(Action<GroupPairDto> act) => RegisterCallback(nameof(Client_GroupPairLeft), act);

    public void OnGroupSendFullInfo(Action<GroupFullInfoDto> act) => RegisterCallback(nameof(Client_GroupSendFullInfo), act);

    public void OnGroupSendInfo(Action<GroupInfoDto> act) => RegisterCallback(nameof(Client_GroupSendInfo), act);

    public void OnReceiveServerMessage(Action<MessageSeverity, string> act) => RegisterCallback(nameof(Client_ReceiveServerMessage), act);

    public void OnReceiveNews(Action<string?> act) => RegisterCallback(nameof(Client_ReceiveNews), act);

    public void OnUpdateSystemInfo(Action<SystemInfoDto> act) => RegisterCallback(nameof(Client_UpdateSystemInfo), act);

    public void OnUserAddClientPair(Action<UserPairDto> act) => RegisterCallback(nameof(Client_UserAddClientPair), act);

    public void OnUserChatMsg(Action<UserChatMsgDto> chatMsgDto) => RegisterCallback(nameof(Client_UserChatMsg), chatMsgDto);

    public void OnUserReceiveCharacterData(Action<OnlineUserCharaDataDto> act) => RegisterCallback(nameof(Client_UserReceiveCharacterData), act);

    public void OnUserReceiveApplicationReceipt(Action<PairApplicationReceiptDto> act) => RegisterCallback(nameof(Client_UserReceiveApplicationReceipt), act);

    public void OnUserReceiveUploadStatus(Action<UserDto> act) => RegisterCallback(nameof(Client_UserReceiveUploadStatus), act);

    public void OnUserPairingAvailability(Action<List<PairingAvailabilityDto>> act) => RegisterCallback(nameof(Client_UserPairingAvailability), act);

    public void OnUserPairingAvailabilityDelta(Action<PairingAvailabilityDeltaDto> act) => RegisterCallback(nameof(Client_UserPairingAvailabilityDelta), act);

    public void OnRequestPairingAvailabilitySubscription(Action<PairingAvailabilityResumeRequestDto> act) => RegisterCallback(nameof(Client_RequestPairingAvailabilitySubscription), act);

    public void OnUserPairingRequest(Action<PairingRequestDto> act) => RegisterCallback(nameof(Client_UserPairingRequest), act);

    public void OnUserRemoveClientPair(Action<UserDto> act) => RegisterCallback(nameof(Client_UserRemoveClientPair), act);

    public void OnUserSendOffline(Action<UserDto> act) => RegisterCallback(nameof(Client_UserSendOffline), act);

    public void OnUserSendOnline(Action<OnlineUserIdentDto> act) => RegisterCallback(nameof(Client_UserSendOnline), act);

    public void OnUserUpdateOtherPairPermissions(Action<UserPermissionsDto> act) => RegisterCallback(nameof(Client_UserUpdateOtherPairPermissions), act);

    public void OnUserUpdateProfile(Action<UserDto> act) => RegisterCallback(nameof(Client_UserUpdateProfile), act);

    public void OnCharacterProfileChanged(Action<CharacterProfileChangedDto> act) => RegisterCallback(nameof(Client_CharacterProfileChanged), act);

    public void OnUserUpdateSelfPairPermissions(Action<UserPermissionsDto> act) => RegisterCallback(nameof(Client_UserUpdateSelfPairPermissions), act);

    public void OnGposeLobbyJoin(Action<UserData> act) => RegisterCallback(nameof(Client_GposeLobbyJoin), act);

    public void OnGposeLobbyLeave(Action<UserData> act) => RegisterCallback(nameof(Client_GposeLobbyLeave), act);

    public void OnGposeLobbyPushCharacterData(Action<CharaDataDownloadDto> act) => RegisterCallback(nameof(Client_GposeLobbyPushCharacterData), act);

    public void OnGposeLobbyPushPoseData(Action<UserData, PoseData> act) => RegisterCallback(nameof(Client_GposeLobbyPushPoseData), act);

    public void OnGposeLobbyPushWorldData(Action<UserData, WorldData> act) => RegisterCallback(nameof(Client_GposeLobbyPushWorldData), act);

    private void RegisterCallback<T>(string methodName, Action<T> action)
    {
        if (_connectionLifecycle.HooksRegistered)
        {
            return;
        }

        _snowHub!.On(methodName, action);
    }

    private void RegisterCallback<T1, T2>(string methodName, Action<T1, T2> action)
    {
        if (_connectionLifecycle.HooksRegistered)
        {
            return;
        }

        _snowHub!.On(methodName, action);
    }
}
