using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Dto.Venue;

namespace Snowcloak.API.SignalR;

public interface ISnowHub
{
    const int ApiVersion = 1032;
    const string Path = "/snow";

    Task<bool> CheckClientHealth();

    Task Client_DownloadReady(Guid requestId);

    Task Client_GroupChangePermissions(GroupPermissionDto groupPermission);

    Task Client_GroupChatMsg(GroupChatMsgDto groupChatMsgDto);
    Task Client_ChannelChatMsg(ChannelChatMsgDto channelChatMsgDto);
    Task Client_ChannelMemberJoined(ChannelMemberJoinedDto channelMemberJoinedDto);
    Task Client_ChannelMemberLeft(ChannelMemberLeftDto channelMemberLeftDto);

    Task Client_GroupDelete(GroupDto groupDto);

    Task Client_GroupPairChangePermissions(GroupPairUserPermissionDto permissionDto);

    Task Client_GroupPairChangeUserInfo(GroupPairUserInfoDto userInfo);

    Task Client_GroupPairJoined(GroupPairFullInfoDto groupPairInfoDto);

    Task Client_GroupPairLeft(GroupPairDto groupPairDto);

    Task Client_GroupSendFullInfo(GroupFullInfoDto groupInfo);

    Task Client_GroupSendInfo(GroupInfoDto groupInfo);

    Task Client_ReceiveServerMessage(MessageSeverity messageSeverity, string message);

    Task Client_UpdateSystemInfo(SystemInfoDto systemInfo);

    Task Client_UserAddClientPair(UserPairDto dto);

    Task Client_UserChatMsg(UserChatMsgDto chatMsgDto);

    Task Client_UserReceiveCharacterData(OnlineUserCharaDataDto dataDto);

    Task Client_UserReceiveUploadStatus(UserDto dto);

    Task Client_UserRemoveClientPair(UserDto dto);

    Task Client_UserSendOffline(UserDto dto);

    Task Client_UserSendOnline(OnlineUserIdentDto dto);

    Task Client_UserUpdateOtherPairPermissions(UserPermissionsDto dto);

    Task Client_UserUpdateProfile(UserDto dto);

    Task Client_UserUpdateSelfPairPermissions(UserPermissionsDto dto);
    Task Client_UserPairingAvailabilityDelta(PairingAvailabilityDeltaDto delta);

    Task Client_GposeLobbyJoin(UserData userData);
    Task Client_GposeLobbyLeave(UserData userData);
    Task Client_GposeLobbyPushCharacterData(CharaDataDownloadDto charaDownloadDto);
    Task Client_GposeLobbyPushPoseData(UserData userData, PoseData poseData);
    Task Client_GposeLobbyPushWorldData(UserData userData, WorldData worldData);

    Task<ConnectionDto> GetConnectionDto();
    Task UserSetConnectionMode(ConnectionModeDto mode);

    Task<ChannelDto> ChannelCreate(ChannelCreateDto createDto);
    Task<ChannelMemberDto?> ChannelJoin(ChannelDto channel);
    Task ChannelLeave(ChannelDto channel);
    Task ChannelKick(ChannelKickDto kickDto);
    Task ChannelBan(ChannelBanDto banDto);
    Task ChannelUnban(ChannelUnbanDto unbanDto);
    Task ChannelSetMode(ChannelModeUpdateDto modeUpdateDto);
    Task ChannelSetRole(ChannelRoleUpdateDto roleUpdateDto);
    Task ChannelSetTopic(ChannelTopicUpdateDto topicUpdateDto);
    Task<List<ChannelDto>> ChannelList();
    Task<List<ChannelMemberDto>> ChannelGetMembers(ChannelDto channel);

    Task GroupBanUser(GroupPairDto dto, string reason);

    Task GroupChangeGroupPermissionState(GroupPermissionDto dto);

    Task GroupChangeIndividualPermissionState(GroupPairUserPermissionDto dto);

    Task GroupChangeOwnership(GroupPairDto groupPair);

    Task<bool> GroupChangePassword(GroupPasswordDto groupPassword);

    Task GroupChatSendMsg(GroupDto group, ChatMessage message);

    Task GroupClear(GroupDto group);

    Task<GroupPasswordDto> GroupCreate();

    Task<List<string>> GroupCreateTempInvite(GroupDto group, int amount);

    Task GroupDelete(GroupDto group);

    Task<List<BannedGroupUserDto>> GroupGetBannedUsers(GroupDto group);

    Task<bool> GroupJoin(GroupPasswordDto passwordedGroup);

    Task GroupLeave(GroupDto group);

    Task GroupRemoveUser(GroupPairDto groupPair);

    Task GroupSetUserInfo(GroupPairUserInfoDto groupPair);

    Task<List<GroupFullInfoDto>> GroupsGetAll();

    Task<List<GroupPairFullInfoDto>> GroupsGetUsersInGroup(GroupDto group);

    Task GroupUnbanUser(GroupPairDto groupPair);
    Task<int> GroupPrune(GroupDto group, int days, bool execute);

    Task UserAddPair(UserDto user);

    Task UserChatSendMsg(UserDto user, ChatMessage message);

    Task ChannelChatSendMsg(ChannelDto channel, ChatMessage message);

    Task UserDelete();

    Task<List<OnlineUserIdentDto>> UserGetOnlinePairs();

    Task<List<UserPairDto>> UserGetPairedClients();

    Task<UserProfileDto> UserGetProfile(UserProfileRequestDto dto);
    Task UserPushData(UserCharaDataMessageDto dto);

    Task UserRemovePair(UserDto userDto);

    Task UserReportProfile(UserProfileReportDto userDto);

    Task UserSetPairPermissions(UserPermissionsDto userPermissions);

    Task UserSetProfile(UserProfileDto userDescription);
    Task<bool> UserSetVanityId(UserVanityIdDto vanityId);
    Task UserSetTextureCompressionPreference(TextureCompressionPreferenceDto preference);
    Task UserSetTextureCompressionMapping(TextureCompressionMappingBatchDto mapping);
    Task<bool> UserSubscribePairingAvailability(PairingAvailabilitySubscriptionDto request);

    Task UserUnsubscribePairingAvailability();


    Task<CharaDataFullDto?> CharaDataCreate();
    Task<CharaDataFullDto?> CharaDataUpdate(CharaDataUpdateDto updateDto);
    Task<bool> CharaDataDelete(string id);
    Task<CharaDataMetaInfoDto?> CharaDataGetMetainfo(string id);
    Task<CharaDataDownloadDto?> CharaDataDownload(string id);
    Task<List<CharaDataFullDto>> CharaDataGetOwn();
    Task<List<CharaDataMetaInfoDto>> CharaDataGetShared();
    Task<CharaDataFullDto?> CharaDataAttemptRestore(string id);

    Task<string> GposeLobbyCreate();
    Task<List<UserData>> GposeLobbyJoin(string lobbyId);
    Task<bool> GposeLobbyLeave();
    Task GposeLobbyPushCharacterData(CharaDataDownloadDto charaDownloadDto);
    Task GposeLobbyPushPoseData(PoseData poseData);
    Task GposeLobbyPushWorldData(WorldData worldData);
    
    Task<VenueInfoResponseDto> VenueGetInfoForPlot(VenueInfoRequestDto request);
    Task<VenueRegistrationResponseDto> VenueRegister(VenueRegistrationRequestDto request);
    Task<VenueRegistryGetResponseDto> VenueRegistryGet(VenueRegistryGetRequestDto request);
    Task<VenueRegistryListResponseDto> VenueRegistryList(VenueRegistryListRequestDto request);
    Task<VenueRegistryListResponseDto> VenueRegistryListOwned(VenueRegistryListOwnedRequestDto request);
    Task<VenueRegistryUpsertResponseDto> VenueRegistryUpsert(VenueRegistryUpsertRequestDto request);
    Task<VenueAdvertisementUpsertResponseDto> VenueAdvertisementUpsert(VenueAdvertisementUpsertRequestDto request);
    Task<VenueAdvertisementDeleteResponseDto> VenueAdvertisementDelete(VenueAdvertisementDeleteRequestDto request);
    Task Client_UserPairingAvailability(List<PairingAvailabilityDto> availability);
    Task Client_RequestPairingAvailabilitySubscription(PairingAvailabilityResumeRequestDto resumeRequest);
    Task Client_UserPairingRequest(PairingRequestDto dto);
    Task UserSetPairingOptIn(PairingOptInDto dto);
    Task<bool> UserGetPairingOptIn();
    Task UserQueryPairingAvailability(PairingAvailabilityQueryDto query);
    Task UserSendPairRequest(PairingRequestTargetDto dto);

    Task UserRespondToPairRequest(PairingRequestDecisionDto dto);
    
    Task<List<OnlineUserIdentDto>> UserGetPairsInRange(List<string> idents);
}
