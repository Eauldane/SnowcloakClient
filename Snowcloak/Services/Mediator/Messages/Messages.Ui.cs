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
public record OpenPluginIntegrationsSettingsMessage : SameThreadMessage;
public record OpenPairRequestConfirmationMessage(string Ident, string CharacterName, string PluginName) : SameThreadMessage;
public record NotificationMessage
    (string Title, string Message, NotificationType Type, TimeSpan? TimeShownOnScreen = null) : MessageBase;
public record UiToggleMessage(Type UiType) : SameThreadMessage;
public record ProfilePopoutToggle(Pair? Pair) : SameThreadMessage;
public record CompactUiChange(Vector2 Size, Vector2 Position) : MessageBase;
public record ProfileOpenStandaloneMessage(UserData UserData, Pair? Pair = null, ProfileVisibility? RequestedVisibility = null,
    string? Ident = null, string? FallbackName = null) : SameThreadMessage;
public record RemoveWindowMessage(WindowMediatorSubscriberBase Window) : SameThreadMessage;
public record OpenReportPopupMessage(UserData User, string Ident, ProfileVisibility Visibility, long Revision,
    ProfileReportSurface Surface = ProfileReportSurface.Profile) : SameThreadMessage;
public record OpenBanUserPopupMessage(Pair PairToBan, GroupFullInfoDto GroupFullInfoDto) : SameThreadMessage;
public record OpenSyncshellAdminPanel(GroupFullInfoDto GroupInfo) : SameThreadMessage;
public record OpenSyncshellEventsWindow(GroupFullInfoDto GroupInfo) : SameThreadMessage;
public record GroupCommunityUpdatedMessage(GroupCommunityDto Community) : MessageBase;
public record OpenPermissionWindow(Pair Pair) : SameThreadMessage;
public record OpenPairAnalysisWindow(Pair Pair) : SameThreadMessage;
public record OpenSyncTroubleshootingWindow(Pair Pair) : SameThreadMessage;
public record OpenBbCodeLinkPopupMessage(string Url) : SameThreadMessage;
public record OpenFrostbrandUiMessage : SameThreadMessage;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
