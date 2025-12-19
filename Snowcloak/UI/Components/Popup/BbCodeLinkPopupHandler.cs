using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Snowcloak.Utils;
using System.Numerics;
using Snowcloak.Services.Localisation;

namespace Snowcloak.UI.Components.Popup;

internal class BbCodeLinkPopupHandler : IPopupHandler
{
    private readonly UiSharedService _uiSharedService;
    private string _url = string.Empty;
    private readonly LocalisationService _localisationService;

    public BbCodeLinkPopupHandler(UiSharedService uiSharedService, LocalisationService localisationService)
    {
        _uiSharedService = uiSharedService;
        _localisationService = localisationService;
    }

    public Vector2 PopupSize => new(520, 220);
    public bool ShowClose => false;

    public void DrawContent()
    {
        UiSharedService.TextWrapped(L("LinkWarning", "You're about to open a link outside of Snowcloak. We haven't vetted it, so please only proceed if you trust the destination."));
        ImGuiHelpers.ScaledDummy(6f);
        UiSharedService.TextWrapped(L("DestinationUrl", "Destination URL:"));
        var urlValue = _url;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##bbcode_link_url", ref urlValue, 4096, ImGuiInputTextFlags.ReadOnly);

        var canOpen = !string.IsNullOrWhiteSpace(_url);
        ImGuiHelpers.ScaledDummy(10f);

        using (ImRaii.Disabled(!canOpen))
        {
            using var openColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGreen);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ExternalLinkAlt, L("OpenLink", "Open link")))
            {
                Util.OpenLink(_url);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Clipboard, L("CopyUrl", "Copy URL")))
        {
            ImGui.SetClipboardText(_url);
        }
        
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, L("Close", "Close")))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    public void Open(string url)
    {
        _url = url;
    }
    
    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"BbCodeLinkPopup.{key}", fallback);
    }
}