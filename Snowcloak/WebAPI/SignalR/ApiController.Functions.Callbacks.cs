using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.Mediator;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    private string? _lastPublishedNews;

    public Task Client_GroupChangePermissions(GroupPermissionDto groupPermission)
    {
        Logger.LogTrace("Client_GroupChangePermissions: {perm}", groupPermission);
        ExecuteSafely(() => _pairManager.SetGroupPermissions(groupPermission));
        return Task.CompletedTask;
    }

    public Task Client_GroupChatMsg(GroupChatMsgDto groupChatMsgDto)
    {
        Logger.LogDebug("Client_GroupChatMsg: {msg}", groupChatMsgDto.Message);
        Mediator.Publish(new GroupChatMsgMessage(groupChatMsgDto.Group, groupChatMsgDto.Message));
        return Task.CompletedTask;
    }

    public Task Client_GroupChatMemberState(GroupChatMemberStateDto dto)
    {
        Logger.LogDebug("Client_GroupChatMemberState: {dto}", dto);
        Mediator.Publish(new GroupChatMemberStateMessage(dto));
        return Task.CompletedTask;
    }

    public Task Client_ChannelChatMsg(ChannelChatMsgDto channelChatMsgDto)
    {
        Logger.LogDebug("Client_ChannelChatMsg: {msg}", channelChatMsgDto.Message);
        Mediator.Publish(new ChannelChatMsgMessage(channelChatMsgDto.Channel, channelChatMsgDto.Message));
        return Task.CompletedTask;
    }

    public Task Client_ChannelMemberJoined(ChannelMemberJoinedDto dto)
    {
        Logger.LogDebug("Client_ChannelMemberJoined: {member}", dto.User);
        Mediator.Publish(new ChannelMemberJoinedMessage(dto));
        return Task.CompletedTask;
    }

    public Task Client_ChannelMemberLeft(ChannelMemberLeftDto dto)
    {
        Logger.LogDebug("Client_ChannelMemberLeft: {member}", dto.User);
        Mediator.Publish(new ChannelMemberLeftMessage(dto));
        return Task.CompletedTask;
    }

    public Task Client_GroupPairChangePermissions(GroupPairUserPermissionDto permissionDto)
    {
        Logger.LogTrace("Client_GroupPairChangePermissions: {dto}", permissionDto);
        ExecuteSafely(() =>
        {
            if (string.Equals(permissionDto.UID, UID, StringComparison.Ordinal)) _pairManager.SetGroupUserPermissions(permissionDto);
            else _pairManager.SetGroupPairUserPermissions(permissionDto);
        });
        return Task.CompletedTask;
    }

    public Task Client_GroupPairChangeLabels(GroupMemberLabelsDto labelsDto)
    {
        Logger.LogTrace("Client_GroupPairChangeLabels: {dto}", labelsDto);
        ExecuteSafely(() =>
        {
            if (string.Equals(labelsDto.UID, UID, StringComparison.Ordinal)) _pairManager.SetGroupMemberLabels(labelsDto);
            else _pairManager.SetGroupPairMemberLabels(labelsDto);
        });
        return Task.CompletedTask;
    }

    public Task Client_GroupDelete(GroupDto groupDto)
    {
        Logger.LogTrace("Client_GroupDelete: {dto}", groupDto);
        ExecuteSafely(() => _pairManager.RemoveGroup(groupDto.Group));
        return Task.CompletedTask;
    }

    public Task Client_GroupPairChangeUserInfo(GroupPairUserInfoDto userInfo)
    {
        Logger.LogTrace("Client_GroupPairChangeUserInfo: {dto}", userInfo);
        ExecuteSafely(() =>
        {
            if (string.Equals(userInfo.UID, UID, StringComparison.Ordinal)) _pairManager.SetGroupStatusInfo(userInfo);
            else _pairManager.SetGroupPairStatusInfo(userInfo);
        });
        return Task.CompletedTask;
    }

    public Task Client_GroupPairJoined(GroupPairFullInfoDto groupPairInfoDto)
    {
        Logger.LogTrace("Client_GroupPairJoined: {dto}", groupPairInfoDto);
        ExecuteSafely(() =>
        {
            _pairManager.AddGroupPair(groupPairInfoDto);

            if (string.Equals(groupPairInfoDto.User.UID, UID, StringComparison.Ordinal))
            {
                if (!_shellConfigStore.HasShellConfigForGid(groupPairInfoDto.Group.GID))
                {
                    var shellConfig = _shellConfigStore.GetShellConfigForGid(groupPairInfoDto.Group.GID);
                    shellConfig.Enabled = false;
                    _shellConfigStore.SaveShellConfigForGid(groupPairInfoDto.Group.GID, shellConfig);
                }

                var config = _shellConfigStore.GetShellConfigForGid(groupPairInfoDto.Group.GID);
                if (!config.Enabled)
                {
                    _ = GroupChatLeave(new GroupDto(groupPairInfoDto.Group));
                }
            }
        });
        return Task.CompletedTask;
    }

    public Task Client_GroupPairLeft(GroupPairDto groupPairDto)
    {
        Logger.LogTrace("Client_GroupPairLeft: {dto}", groupPairDto);
        ExecuteSafely(() => _pairManager.RemoveGroupPair(groupPairDto));
        return Task.CompletedTask;
    }

    public Task Client_GroupSendFullInfo(GroupFullInfoDto groupInfo)
    {
        Logger.LogTrace("Client_GroupSendFullInfo: {dto}", groupInfo);
        ExecuteSafely(() => _pairManager.AddGroup(groupInfo));
        return Task.CompletedTask;
    }

    public Task Client_GroupSendInfo(GroupInfoDto groupInfo)
    {
        Logger.LogTrace("Client_GroupSendInfo: {dto}", groupInfo);
        ExecuteSafely(() => _pairManager.SetGroupInfo(groupInfo));
        return Task.CompletedTask;
    }

    public Task Client_ReceiveServerMessage(MessageSeverity messageSeverity, string message)
    {
        switch (messageSeverity)
        {
            case MessageSeverity.Error:
                Mediator.Publish(new NotificationMessage("Warning from " + _serverManager.CurrentServer!.ServerName, message, NotificationType.Error, TimeSpan.FromSeconds(7.5)));
                break;

            case MessageSeverity.Warning:
                Mediator.Publish(new NotificationMessage("Warning from " + _serverManager.CurrentServer!.ServerName, message, NotificationType.Warning, TimeSpan.FromSeconds(7.5)));
                break;

            case MessageSeverity.Information:
                if (_doNotNotifyOnNextInfo)
                {
                    _doNotNotifyOnNextInfo = false;
                    break;
                }
                Mediator.Publish(new NotificationMessage("Info from " + _serverManager.CurrentServer!.ServerName, message, NotificationType.Info, TimeSpan.FromSeconds(5)));
                break;
        }

        return Task.CompletedTask;
    }

    public Task Client_ReceiveNews(string? news)
    {
        var normalizedNews = NormalizeNews(news);
        if (!string.IsNullOrEmpty(normalizedNews))
        {
            SystemInfoDto = SystemInfoDto with { News = normalizedNews };
            PublishNewsIfChanged(normalizedNews);
        }

        return Task.CompletedTask;
    }

    public Task Client_UpdateSystemInfo(SystemInfoDto systemInfo)
    {
        SystemInfoDto = systemInfo;
        PublishNewsIfChanged(systemInfo.News);
        return Task.CompletedTask;
    }

    private void PublishNewsIfChanged(string? news)
    {
        var normalizedNews = NormalizeNews(news);
        if (string.IsNullOrEmpty(normalizedNews))
        {
            return;
        }

        if (string.Equals(_lastPublishedNews, normalizedNews, StringComparison.Ordinal))
        {
            return;
        }

        _lastPublishedNews = normalizedNews;
        Mediator.Publish(new ServerNewsMessage(normalizedNews));
    }

    private static string? NormalizeNews(string? news)
    {
        return string.IsNullOrWhiteSpace(news) ? null : news.Trim();
    }

    public Task Client_UserAddClientPair(UserPairDto dto)
    {
        Logger.LogDebug("Client_UserAddClientPair: {dto}", dto);
        ExecuteSafely(() =>
        {
            _pairManager.SuppressNextNotePopupForUid(dto.User.UID);
            _pairManager.AddUserPair(dto, addToLastAddedUser: true);
        });
        return Task.CompletedTask;
    }

    public Task Client_UserPairingAvailability(List<PairingAvailabilityDto> availability)
    {
        Logger.LogTrace("Client_UserPairingAvailability: {count}", availability.Count);
        ExecuteSafely(() => _pairRequestService.UpdateAvailability(availability, publishImmediately: true));
        return Task.CompletedTask;
    }

    public Task Client_RequestPairingAvailabilitySubscription(PairingAvailabilityResumeRequestDto resumeRequest)
    {
        ExecuteSafely(() => _ = _pairRequestService.ResumePairingAvailabilitySubscriptionAsync(resumeRequest));
        return Task.CompletedTask;
    }

    public Task Client_UserPairingAvailabilityDelta(PairingAvailabilityDeltaDto delta)
    {
        Logger.LogTrace("Client_UserPairingAvailabilityDelta: +{added}/-{removed}",
            delta.AddedProfiles?.Count ?? 0,
            delta.RemovedIdents?.Count ?? 0);
        ExecuteSafely(() => _pairRequestService.ApplyAvailabilityDelta(
            delta.AddedProfiles ?? Array.Empty<PairingAvailabilityDto>(),
            delta.RemovedIdents ?? Array.Empty<string>()));
        return Task.CompletedTask;
    }

    public Task Client_UserPairingRequest(PairingRequestDto dto)
    {
        Logger.LogDebug("Client_UserPairingRequest: {uid}", dto.Requester.UID);
        ExecuteSafely(() => _pairRequestService.ReceiveRequest(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserChatMsg(UserChatMsgDto chatMsgDto)
    {
        Logger.LogDebug("Client_UserChatMsg: {msg}", chatMsgDto.Message);
        Mediator.Publish(new UserChatMsgMessage(chatMsgDto.Message));
        return Task.CompletedTask;
    }

    public Task Client_UserReceiveCharacterData(OnlineUserCharaDataDto dataDto)
    {
        Logger.LogTrace("Client_UserReceiveCharacterData: {user}", dataDto.User);
        Logger.LogDebug(
            "Client_UserReceiveCharacterData for {user}: reportedTriangles={triangles}, reportedVramBytes={vramBytes}, fileCount={fileCount}",
            dataDto.User,
            dataDto.ReportedTriangles?.ToString() ?? "<null>",
            dataDto.ReportedVramBytes?.ToString() ?? "<null>",
            dataDto.CharaData?.FileReplacements.Count ?? 0);

        ExecuteSafely(() => _pairManager.ReceiveCharaData(dataDto));
        return Task.CompletedTask;
    }

    public Task Client_UserReceiveApplicationReceipt(PairApplicationReceiptDto dto)
    {
        Logger.LogTrace("Client_UserReceiveApplicationReceipt: {dto}", dto);
        ExecuteSafely(() => _pairManager.ReceiveApplicationReceipt(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserReceiveUploadStatus(UserDto dto)
    {
        Logger.LogTrace("Client_UserReceiveUploadStatus: {dto}", dto);
        ExecuteSafely(() => _pairManager.ReceiveUploadStatus(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserRemoveClientPair(UserDto dto)
    {
        Logger.LogDebug("Client_UserRemoveClientPair: {dto}", dto);
        ExecuteSafely(() => _pairManager.RemoveUserPair(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserSendOffline(UserDto dto)
    {
        Logger.LogDebug("Client_UserSendOffline: {dto}", dto);
        ExecuteSafely(() => _pairManager.MarkPairOffline(dto.User));
        return Task.CompletedTask;
    }

    public Task Client_UserSendOnline(OnlineUserIdentDto dto)
    {
        Logger.LogDebug("Client_UserSendOnline: {dto}", dto);
        ExecuteSafely(() => _pairManager.MarkPairOnline(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserUpdateOtherPairPermissions(UserPermissionsDto dto)
    {
        Logger.LogDebug("Client_UserUpdateOtherPairPermissions: {dto}", dto);
        ExecuteSafely(() => _pairManager.UpdatePairPermissions(dto));
        return Task.CompletedTask;
    }

    public Task Client_UserUpdateProfile(UserDto dto)
    {
        Logger.LogDebug("Client_UserUpdateProfile: {dto}", dto);
        ExecuteSafely(() =>
        {
            _pairManager.UpdateUserProfile(dto);
            var connectionDto = _connectionContext.Dto;
            if (connectionDto != null && string.Equals(connectionDto.User.UID, dto.User.UID, StringComparison.Ordinal))
            {
                var selfHexChanged = !string.Equals(connectionDto.User.HexString, dto.User.HexString, StringComparison.Ordinal);
                var selfGlowHexChanged = !string.Equals(connectionDto.User.GlowHexString, dto.User.GlowHexString, StringComparison.Ordinal);
                _connectionContext = _connectionContext.WithUser(dto.User);
                if (selfHexChanged || selfGlowHexChanged)
                {
                    Mediator.Publish(new NameplateRedrawMessage());
                }
            }
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        });
        return Task.CompletedTask;
    }

    public Task Client_CharacterProfileChanged(CharacterProfileChangedDto dto)
    {
        Logger.LogDebug("Client_CharacterProfileChanged: {dto}", dto);
        ExecuteSafely(() =>
        {
            Mediator.Publish(new ClearCharacterProfileDataMessage(dto.Ident, PreserveSummary: true));
            _pairRequestService.RefreshAvailableProfileSummary(dto.Ident);
        });
        return Task.CompletedTask;
    }

    public Task Client_UserUpdateSelfPairPermissions(UserPermissionsDto dto)
    {
        Logger.LogDebug("Client_UserUpdateSelfPairPermissions: {dto}", dto);
        ExecuteSafely(() => _pairManager.UpdateSelfPairPermissions(dto));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyJoin(UserData userData)
    {
        Logger.LogDebug("Client_GposeLobbyJoin: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GposeLobbyUserJoin(userData)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyLeave(UserData userData)
    {
        Logger.LogDebug("Client_GposeLobbyLeave: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyUserLeave(userData)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyPushCharacterData(CharaDataDownloadDto charaDownloadDto)
    {
        Logger.LogDebug("Client_GposeLobbyPushCharacterData: {dto}", charaDownloadDto.Uploader);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyReceiveCharaData(charaDownloadDto)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyPushPoseData(UserData userData, PoseData poseData)
    {
        Logger.LogDebug("Client_GposeLobbyPushPoseData: {dto}", userData);
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyReceivePoseData(userData, poseData)));
        return Task.CompletedTask;
    }

    public Task Client_GposeLobbyPushWorldData(UserData userData, WorldData worldData)
    {
        ExecuteSafely(() => Mediator.Publish(new GPoseLobbyReceiveWorldData(userData, worldData)));
        return Task.CompletedTask;
    }

    private void ExecuteSafely(Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Error on executing safely");
        }
    }
}
