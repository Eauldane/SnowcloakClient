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
    private readonly UserSafetyStore _safetyStore;
    private UserData? _reportedUser;
    private string _reportedIdent = string.Empty;
    private ProfileVisibility _reportedVisibility;
    private long _reportedRevision;
    private ProfileReportSurface _surface;
    private string _reportReason = string.Empty;
    private bool _blockUser;

    public ReportPopupHandler(ApiController apiController, UiFontService fontService, UserSafetyStore safetyStore)
    {
        _apiController = apiController;
        _fontService = fontService;
        _safetyStore = safetyStore;
    }

    public Vector2 PopupSize => new(500, 500);

    public bool ShowClose => true;

    public void DrawContent()
    {
        using (_fontService.UidFont.Push())
            ElezenImgui.WrappedText($"Report {_reportedUser!.AliasOrUID} Profile");
        
        ImGui.InputTextMultiline("##reportReason", ref _reportReason, 500, new Vector2(500 - ImGui.GetStyle().ItemSpacing.X * 2, 200));
        ElezenImgui.ColouredWrappedText("Report spam and wrong reports will not be tolerated and can lead to permanent account suspension.", ImGuiColors.DalamudRed);
        ElezenImgui.ColouredWrappedText("This is for reporting misbehaviour but solely for the actual profile. Reports that are not solely for the profile will be ignored.", ImGuiColors.DalamudYellow);
        ImGui.Checkbox("Also block this UID", ref _blockUser);
        ElezenImgui.DrawHelpText("Blocking removes direct pairing and prevents future discovery, pair requests, direct messages, and mutual chat delivery. Shared syncshell appearance remains unchanged.");
        using (ImRaii.Disabled(string.IsNullOrEmpty(_reportReason) || !_apiController.SupportsOpenRpSafety))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ExclamationTriangle, "Send Report"))
            {
                ImGui.CloseCurrentPopup();
                var reason = _reportReason;
                _ = SubmitAsync(reason, _blockUser);
            }
        }
    }

    private async Task SubmitAsync(string reason, bool blockUser)
    {
        await _apiController.CharacterProfileReport(new CharacterProfileReportDto(
            _reportedIdent, _reportedVisibility, _reportedRevision, reason, _surface, blockUser)).ConfigureAwait(false);
        if (blockUser)
            _safetyStore.Refresh();
    }

    public void Open(OpenReportPopupMessage msg)
    {
        _reportedUser = msg.User;
        _reportedIdent = msg.Ident;
        _reportedVisibility = msg.Visibility;
        _reportedRevision = msg.Revision;
        _surface = msg.Surface;
        _reportReason = string.Empty;
        _blockUser = false;
    }
}
