using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using System.Numerics;

namespace Snowcloak.UI.Components.Popup;

internal class BbCodeLinkPopupHandler : IPopupHandler
{
    private readonly UiSharedService _uiSharedService;
    private string _url = string.Empty;

    public BbCodeLinkPopupHandler(UiSharedService uiSharedService)
    {
        _uiSharedService = uiSharedService;
    }

    public Vector2 PopupSize => new(520, 220);
    public bool ShowClose => false;

    public void DrawContent()
    {
        UiSharedService.TextWrapped("You're about to open a link outside of Snowcloak. We haven't vetted it, so please only proceed if you trust the destination.");
        ImGuiHelpers.ScaledDummy(6f);
        UiSharedService.TextWrapped("Destination URL:");
        var urlValue = _url;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##bbcode_link_url", ref urlValue, 4096, ImGuiInputTextFlags.ReadOnly);

        var canOpen = !string.IsNullOrWhiteSpace(_url);
        ImGuiHelpers.ScaledDummy(10f);

        using (ImRaii.Disabled(!canOpen))
        {
            using var openColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGreen);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ExternalLinkAlt, "Open link"))
            {
                Util.OpenLink(_url);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Clipboard, "Copy URL"))
        {
            ImGui.SetClipboardText(_url);
        }
        
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, "Close"))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    public void Open(string url)
    {
        _url = url;
    }
}