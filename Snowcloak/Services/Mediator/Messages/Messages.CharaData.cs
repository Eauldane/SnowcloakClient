using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;

namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094

public record HaltCharaDataCreation(bool Resume = false) : SameThreadMessage;

public record OpenCharaDataHubWithFilterMessage(UserData UserData) : MessageBase;
public record GposeLobbyUserJoin(UserData UserData) : MessageBase;
public record GPoseLobbyUserLeave(UserData UserData) : MessageBase;
public record GPoseLobbyReceiveCharaData(CharaDataDownloadDto CharaDataDownloadDto) : MessageBase;
public record GPoseLobbyReceivePoseData(UserData UserData, PoseData PoseData) : MessageBase;
public record GPoseLobbyReceiveWorldData(UserData UserData, WorldData WorldData) : MessageBase;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
