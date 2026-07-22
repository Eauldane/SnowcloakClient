using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.Roleplay;
using Snowcloak.API.Protocol;

namespace Snowcloak.WebAPI;

public sealed partial class ApiController
{
    public bool SupportsRpFeatures => IsConnected
        && _connectionContext.Dto?.ServerCapabilities.HasFlag(HubCapability.Phase6Roleplay) is true;

    public bool SupportsRoleplaySceneIdentity => IsConnected
        && _connectionContext.Dto?.ServerCapabilities.HasFlag(HubCapability.RoleplaySceneIdentity) is true;

    public Task<RpProfileDirectoryConsentDto> RpProfileDirectoryGetConsent() => Invoke<RpProfileDirectoryConsentDto>(nameof(RpProfileDirectoryGetConsent));
    public Task<RpProfileDirectoryConsentDto> RpProfileDirectorySetConsent(RpProfileDirectoryConsentDto dto) => Invoke<RpProfileDirectoryConsentDto>(nameof(RpProfileDirectorySetConsent), dto);
    public Task<RpProfileDirectoryListResponseDto> RpProfileDirectoryList(RpProfileDirectoryQueryDto query) => Invoke<RpProfileDirectoryListResponseDto>(nameof(RpProfileDirectoryList), query);
    public Task<RpAvailabilityCardDto?> RpAvailabilityGetOwn() => Invoke<RpAvailabilityCardDto?>(nameof(RpAvailabilityGetOwn));
    public Task<RpAvailabilityCardDto?> RpAvailabilitySet(RpAvailabilityCardUpdateDto dto) => Invoke<RpAvailabilityCardDto?>(nameof(RpAvailabilitySet), dto);
    public Task RpAvailabilityClear() => Invoke(nameof(RpAvailabilityClear));
    public Task<RpCurrentHooksDto> RpCurrentHooksGetOwn() => Invoke<RpCurrentHooksDto>(nameof(RpCurrentHooksGetOwn));
    public Task<RpCurrentHooksDto> RpCurrentHooksSet(RpCurrentHooksUpdateDto dto) => Invoke<RpCurrentHooksDto>(nameof(RpCurrentHooksSet), dto);
    public Task<RoomDirectoryListResponseDto> RpRoomDirectoryList(RoomDirectoryQueryDto query) => Invoke<RoomDirectoryListResponseDto>(nameof(RpRoomDirectoryList), query);
    public Task<RoomDto> RpRoomSetDiscovery(RoomDiscoveryUpdateDto dto) => Invoke<RoomDto>(nameof(RpRoomSetDiscovery), dto);
    public Task<RoomDto> RpRoomSetScene(RoomSceneMetadataUpdateDto dto) => Invoke<RoomDto>(nameof(RpRoomSetScene), dto);
    public Task<RoomMemberDto> RpRoomSetParticipantIdentity(RoomParticipantIdentityUpdateDto dto) => Invoke<RoomMemberDto>(nameof(RpRoomSetParticipantIdentity), dto);
    public Task<RoomSceneHistoryDto> RpRoomFinishScene(RoomDto room) => Invoke<RoomSceneHistoryDto>(nameof(RpRoomFinishScene), room);
    public Task<List<RoomSceneHistorySummaryDto>> RpRoomSceneHistoryList(RoomDto room) => Invoke<List<RoomSceneHistorySummaryDto>>(nameof(RpRoomSceneHistoryList), room);
    public Task<RoomSceneHistoryDto> RpRoomSceneHistoryGet(RoomSceneHistoryRequestDto dto) => Invoke<RoomSceneHistoryDto>(nameof(RpRoomSceneHistoryGet), dto);
    public Task<ChatMessageDto> RpRoomRollDice(RoomDiceRollRequestDto dto) => Invoke<ChatMessageDto>(nameof(RpRoomRollDice), dto);
    public Task<RoomDto> RpRoomSetTurnOrder(RoomTurnOrderUpdateDto dto) => Invoke<RoomDto>(nameof(RpRoomSetTurnOrder), dto);
    public Task<RoomDto> RpRoomAdvanceTurn(RoomTurnAdvanceDto dto) => Invoke<RoomDto>(nameof(RpRoomAdvanceTurn), dto);
    public Task<RpEventDirectoryListResponseDto> RpEventDirectoryList(RpEventDirectoryQueryDto query) => Invoke<RpEventDirectoryListResponseDto>(nameof(RpEventDirectoryList), query);
    public Task<GroupEventDigestConfigDto> RpEventDigestGet(GroupDto group) => Invoke<GroupEventDigestConfigDto>(nameof(RpEventDigestGet), group);
    public Task<GroupEventDigestConfigDto> RpEventDigestSet(GroupEventDigestConfigDto dto) => Invoke<GroupEventDigestConfigDto>(nameof(RpEventDigestSet), dto);
    public Task RpContentReport(RpContentReportDto dto) => Invoke(nameof(RpContentReport), dto);

    private Task Invoke(string method, params object?[] args)
    {
        CheckConnection();
        return _snowHub!.InvokeCoreAsync(method, args);
    }

    private Task<T> Invoke<T>(string method, params object?[] args)
    {
        CheckConnection();
        return _snowHub!.InvokeCoreAsync<T>(method, args);
    }
}
