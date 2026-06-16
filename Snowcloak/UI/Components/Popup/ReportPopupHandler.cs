using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.PlayerData.Pairs;
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
    private Pair? _reportedPair;
    private string _reportedIdent = string.Empty;
    private ProfileVisibility _reportedVisibility;
    private long _reportedRevision;
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
            ElezenImgui.WrappedText(string.Format("Report {0} Profile", _reportedPair!.UserData.AliasOrUID));
        
        ImGui.InputTextMultiline("##reportReason", ref _reportReason, 500, new Vector2(500 - ImGui.GetStyle().ItemSpacing.X * 2, 200));
        ElezenImgui.WrappedText("Note: Sending a report will quarantine the displayed profile variant for moderation review.{0}The report will be sent to the team of your currently connected server.{0}Depending on the severity of the offense the profile or account can be permanently disabled or banned."
            .Replace("{0}", Environment.NewLine, StringComparison.Ordinal));
        ElezenImgui.ColouredWrappedText("Report spam and wrong reports will not be tolerated and can lead to permanent account suspension.", ImGuiColors.DalamudRed);
        ElezenImgui.ColouredWrappedText("This is not for reporting misbehavior but solely for the actual profile. Reports that are not solely for the profile will be ignored.", ImGuiColors.DalamudYellow);

        using (ImRaii.Disabled(string.IsNullOrEmpty(_reportReason)))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ExclamationTriangle, "Send Report"))
            {
                ImGui.CloseCurrentPopup();
                var reason = _reportReason;
                _ = _apiController.CharacterProfileReport(new CharacterProfileReportDto(
                    _reportedIdent, _reportedVisibility, _reportedRevision, reason));
            }
        }
    }

    public void Open(OpenReportPopupMessage msg)
    {
        _reportedPair = msg.PairToReport;
        _reportedIdent = msg.Ident;
        _reportedVisibility = msg.Visibility;
        _reportedRevision = msg.Revision;
        _reportReason = string.Empty;
    }
}
