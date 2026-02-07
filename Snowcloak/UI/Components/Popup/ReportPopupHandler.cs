using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System.Numerics;

namespace Snowcloak.UI.Components.Popup;

internal class ReportPopupHandler : IPopupHandler
{
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private Pair? _reportedPair;
    private string _reportReason = string.Empty;

    public ReportPopupHandler(ApiController apiController, UiSharedService uiSharedService)
    {
        _apiController = apiController;
        _uiSharedService = uiSharedService;
    }

    public Vector2 PopupSize => new(500, 500);

    public bool ShowClose => true;

    public void DrawContent()
    {
        using (_uiSharedService.UidFont.Push())
            ElezenImgui.WrappedText(string.Format("Report {0} Profile", _reportedPair!.UserData.AliasOrUID));
        
        ImGui.InputTextMultiline("##reportReason", ref _reportReason, 500, new Vector2(500 - ImGui.GetStyle().ItemSpacing.X * 2, 200));
        ElezenImgui.WrappedText("Note: Sending a report will disable the offending profile globally.{0}The report will be sent to the team of your currently connected server.{0}Depending on the severity of the offense the users profile or account can be permanently disabled or banned."
            .Replace("{0}", Environment.NewLine, StringComparison.Ordinal));
        ElezenImgui.ColouredWrappedText("Report spam and wrong reports will not be tolerated and can lead to permanent account suspension.", ImGuiColors.DalamudRed);
        ElezenImgui.ColouredWrappedText("This is not for reporting misbehavior but solely for the actual profile. Reports that are not solely for the profile will be ignored.", ImGuiColors.DalamudYellow);

        using (ImRaii.Disabled(string.IsNullOrEmpty(_reportReason)))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ExclamationTriangle, "Send Report"))
            {
                ImGui.CloseCurrentPopup();
                var reason = _reportReason;
                _ = _apiController.UserReportProfile(new(_reportedPair.UserData, reason));
            }
        }
    }

    public void Open(OpenReportPopupMessage msg)
    {
        _reportedPair = msg.PairToReport;
        _reportReason = string.Empty;
    }
}