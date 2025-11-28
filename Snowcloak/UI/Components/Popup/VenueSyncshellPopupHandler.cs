using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Threading.Tasks;
using Snowcloak.Services.Venue;

namespace Snowcloak.UI.Components.Popup;

internal class VenueSyncshellPopupHandler : IPopupHandler
{
    private readonly UiSharedService _uiSharedService;
    private readonly VenueSyncshellService _venueSyncshellService;
    private bool _closeOnSuccess;
    private bool _isJoining;
    private bool _joinFailed;
    private VenueSyncshellPrompt? _prompt;

    public VenueSyncshellPopupHandler(UiSharedService uiSharedService, VenueSyncshellService venueSyncshellService)
    {
        _uiSharedService = uiSharedService;
        _venueSyncshellService = venueSyncshellService;
    }

    public Vector2 PopupSize => new(480, 320);

    public bool ShowClose => true;

    public void DrawContent()
    {
        if (_prompt == null) return;

        if (_closeOnSuccess)
        {
            ImGui.CloseCurrentPopup();
            _closeOnSuccess = false;
        }

        using (_uiSharedService.UidFont.Push())
            UiSharedService.TextWrapped(_prompt.Venue.VenueName);

        UiSharedService.TextWrapped($"Syncshell: {_prompt.Venue.JoinInfo.Group.AliasOrGID}");
        UiSharedService.TextWrapped($"Location: {_prompt.Venue.LocationDisplay}");

        if (!string.IsNullOrWhiteSpace(_prompt.Venue.VenueHost))
        {
            UiSharedService.TextWrapped($"Host: {_prompt.Venue.VenueHost}");
        }

        if (!string.IsNullOrWhiteSpace(_prompt.Venue.VenueWebsite))
        {
            UiSharedService.TextWrapped($"Website: {_prompt.Venue.VenueWebsite}");
        }

        if (!string.IsNullOrWhiteSpace(_prompt.Venue.VenueDescription))
        {
            using var child = ImRaii.Child("##venue_description", new Vector2(-1, 120 * ImGuiHelpers.GlobalScale), true);
            if (child)
            {
                UiSharedService.TextWrapped(_prompt.Venue.VenueDescription);
            }
        }

        UiSharedService.TextWrapped("If you leave the venue, you will be removed from this syncshell after a two minute grace period.");

        if (_joinFailed)
        {
            UiSharedService.ColorTextWrapped("Failed to join syncshell. Please try again.", ImGuiColors.DalamudRed);
        }

        using (ImRaii.Disabled(_isJoining))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.SignInAlt, _isJoining ? "Joining..." : "Join syncshell"))
            {
                _joinFailed = false;
                _isJoining = true;
                var promptId = _prompt.PromptId;
                _ = Task.Run(async () =>
                {
                    var success = await _venueSyncshellService.JoinVenueShellAsync(promptId).ConfigureAwait(false);
                    _joinFailed = !success;
                    _closeOnSuccess = success;
                    _isJoining = false;
                });
            }
        }
    }

    public void Open(VenueSyncshellPrompt prompt)
    {
        _prompt = prompt;
        _joinFailed = false;
        _isJoining = false;
        _closeOnSuccess = false;
    }
}