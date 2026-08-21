using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using System.Numerics;

namespace Snowcloak.UI.Components.Popup;

internal class ReportPopupHandler : IPopupHandler
{
    private readonly ApiController _apiController;
    private readonly UiFontService _fontService;
    private UserData? _reportedUser;
    private string _reportedIdent = string.Empty;
    private ProfileVisibility _reportedVisibility;
    private long _reportedRevision;
    private ProfileReportSurface _surface;
    private string _reportReason = string.Empty;

    public ReportPopupHandler(ApiController apiController, UiFontService fontService)
    {
        _apiController = apiController;
        _fontService = fontService;
    }

    public Vector2 PopupSize => new(500, 500);

    public bool ShowClose => true;

    public void DrawContent()
    {
        using (_fontService.UidFont.Push())
            ElezenImgui.WrappedText($"Report {_reportedUser!.AliasOrUID}");
        
        ImGui.InputTextMultiline("##reportReason", ref _reportReason, 500, new Vector2(500 - ImGui.GetStyle().ItemSpacing.X * 2, 200));
        ElezenImgui.ColouredWrappedText("Report spam and wrong reports will not be tolerated and can lead to permanent account suspension.", ImGuiColors.DalamudRed);
        ElezenImgui.ColouredWrappedText("Describe the behaviour or content you are reporting and include enough context for a moderator to review it.", ImGuiColors.DalamudYellow);
        var reportSupported = _apiController.SupportsOpenRpSafety
            && (_surface != ProfileReportSurface.User || _apiController.SupportsUnpairedUserReporting);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_reportReason) || !reportSupported))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ExclamationTriangle, "Send Report"))
            {
                ImGui.CloseCurrentPopup();
                var reason = _reportReason;
                _ = SubmitAsync(reason);
            }
        }
    }

    private Task SubmitAsync(string reason)
    {
        return _apiController.CharacterProfileReport(new CharacterProfileReportDto(
            _reportedIdent, _reportedVisibility, _reportedRevision, reason, _surface, false,
            _reportedUser?.UID ?? string.Empty));
    }

    public void Open(OpenReportPopupMessage msg)
    {
        _reportedUser = msg.User;
        _reportedIdent = msg.Ident;
        _reportedVisibility = msg.Visibility;
        _reportedRevision = msg.Revision;
        _surface = msg.Surface;
        _reportReason = string.Empty;
    }
}
