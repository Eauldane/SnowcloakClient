using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Group;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Pairs;
using System.Numerics;

namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
public record OpenSettingsUiMessage : SameThreadMessage;
public record NotificationMessage
    (string Title, string Message, NotificationType Type, TimeSpan? TimeShownOnScreen = null) : MessageBase;
public record UiToggleMessage(Type UiType) : SameThreadMessage;
public record ProfilePopoutToggle(Pair? Pair) : SameThreadMessage;
public record CompactUiChange(Vector2 Size, Vector2 Position) : MessageBase;
public record ProfileOpenStandaloneMessage(UserData UserData, Pair? Pair = null, ProfileVisibility? RequestedVisibility = null,
    string? Ident = null, string? FallbackName = null) : MessageBase;
public record RemoveWindowMessage(WindowMediatorSubscriberBase Window) : MessageBase;
public record OpenReportPopupMessage(Pair PairToReport, string Ident, ProfileVisibility Visibility, long Revision) : SameThreadMessage;
public record OpenBanUserPopupMessage(Pair PairToBan, GroupFullInfoDto GroupFullInfoDto) : SameThreadMessage;
public record OpenSyncshellAdminPanel(GroupFullInfoDto GroupInfo) : MessageBase;
public record OpenSyncshellEventsWindow(GroupFullInfoDto GroupInfo) : MessageBase;
public record OpenPermissionWindow(Pair Pair) : MessageBase;
public record OpenPairAnalysisWindow(Pair Pair) : MessageBase;
public record OpenSyncTroubleshootingWindow(Pair Pair) : MessageBase;
public record OpenBbCodeLinkPopupMessage(string Url) : SameThreadMessage;
public record OpenFrostbrandUiMessage : SameThreadMessage;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
