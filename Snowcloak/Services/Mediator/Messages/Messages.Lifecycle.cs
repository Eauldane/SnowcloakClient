using Snowcloak.API.Dto;
using Snowcloak.Services.Events;
using ElezenTools.Housing;

namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
public record SwitchToIntroUiMessage : MessageBase;
public record SwitchToMainUiMessage : MessageBase;
public record DalamudLoginMessage : MessageBase;
public record DalamudLogoutMessage : MessageBase;
public record ZoneSwitchStartMessage : MessageBase;
public record ZoneSwitchEndMessage : MessageBase;
public record HousingPlotEnteredMessage(HousingPlotLocation Location) : MessageBase;
public record HousingPlotLeftMessage(HousingPlotLocation Location) : MessageBase;
public record CutsceneStartMessage : MessageBase;

public record GposeStartMessage : SameThreadMessage;
public record GposeEndMessage : MessageBase;
public record CutsceneEndMessage : MessageBase;
public record ConnectedMessage(ConnectionDto Connection) : MessageBase;
public record FileServerInfoReceivedMessage(ConnectionDto Connection) : MessageBase;
public record ServerNewsMessage(string News) : MessageBase;

public record DisconnectedMessage : SameThreadMessage;

public record HubReconnectingMessage(Exception? Exception) : SameThreadMessage;
public record HubReconnectedMessage(string? Arg) : SameThreadMessage;
public record HubClosedMessage(Exception? Exception) : SameThreadMessage;

public record CombatOrPerformanceStartMessage : MessageBase;
public record CombatOrPerformanceEndMessage : MessageBase;
public record EventMessage(Event Event) : MessageBase;
public record CensusUpdateMessage(byte Gender, byte RaceId, byte TribeId) : MessageBase;
public record PluginChangeMessage(string InternalName, Version Version, bool IsLoaded) : KeyedMessage(InternalName);
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
