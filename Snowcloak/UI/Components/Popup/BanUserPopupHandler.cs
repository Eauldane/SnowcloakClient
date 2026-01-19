using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Snowcloak.API.Dto.Group;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System.Numerics;

namespace Snowcloak.UI.Components.Popup;

public class BanUserPopupHandler : IPopupHandler
{
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private string _banReason = string.Empty;
    private GroupFullInfoDto _group = null!;
    private Pair _reportedPair = null!;

    public BanUserPopupHandler(ApiController apiController, UiSharedService uiSharedService)
    {
        _apiController = apiController;
        _uiSharedService = uiSharedService;
    }

    public Vector2 PopupSize => new(500, 250);

    public bool ShowClose => true;

    public void DrawContent()
    {
        UiSharedService.TextWrapped(string.Format("User {0} will be banned and removed from this Syncshell.", _reportedPair.UserData.AliasOrUID));
        ImGui.InputTextWithHint("##banreason", "Ban Reason", ref _banReason, 255);

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserSlash, "Ban User"))
        {
            ImGui.CloseCurrentPopup();
            var reason = _banReason;
            _ = _apiController.GroupBanUser(new GroupPairDto(_group.Group, _reportedPair.UserData), reason);
            _banReason = string.Empty;
        }
        UiSharedService.TextWrapped("The reason will be displayed in the banlist. The current server-side alias if present (Vanity ID) will automatically be attached to the reason.");
    }

    public void Open(OpenBanUserPopupMessage message)
    {
        _reportedPair = message.PairToBan;
        _group = message.GroupFullInfoDto;
        _banReason = string.Empty;
    }

}