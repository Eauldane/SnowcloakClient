using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components.BbCode;

namespace Snowcloak.UI;

public sealed class BbCodeTestUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiSharedService;
    private string _previewText = "[color=#ff6699]Colour[/color] [b]bold[/b] [i]italic[/i] [u]underline[/u]\n[list][*]Generic list\n[/list]\n[ol][*]Ordered List\n[/ol]\n[ul][*]Unordered List\n[/ul]\n:at_left:Elezen:at_right:\n[size=125%]Scaled up text[/size] and [size=12]12px text[/size]\n[center]Centered line[/center]\n[align=right]Right aligned line[/align]\n[url=https://snowcloak-sync.com]Link with label[/url] and [url]https://snowcloak-sync.com[/url]\nImage:\n[img]https://raw.githubusercontent.com/Eauldane/SnowcloakClient/refs/heads/main/Snowcloak/images/logo.png[/img]\nEmote test:\n:smile: :sparkle:";

    public BbCodeTestUi(ILogger<BbCodeTestUi> logger, SnowMediator mediator, UiSharedService uiSharedService,
        PerformanceCollectorService performanceCollectorService) : base(logger, mediator, "Snowcloak BBCode Tester###SnowcloakBBCodeTestUi", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        WindowName = "Snowcloak BBCode Tester###SnowcloakBBCodeTestUi";
        IsOpen = false;
        SizeConstraints = new()
        {
            MinimumSize = ImGuiHelpers.ScaledVector2(520, 460),
            MaximumSize = ImGuiHelpers.ScaledVector2(1200, 1000)
        };
    }
    
    protected override void DrawInternal()
    {
        ImGui.TextWrapped("Preview how Snowcloak renders BBCode inside profile and venue descriptions.");
        ElezenImgui.ColouredWrappedText("Supported tags include colours, bold, italics, underline, links, images, emotes, and basic alignment.", ImGuiColors.DalamudGrey);
        
        ImGui.Separator();
        ImGui.TextUnformatted("BBCode Input");
        ImGui.InputTextMultiline("##bbcode-test-input", ref _previewText, 8000, ImGuiHelpers.ScaledVector2(-1, 200 * ImGuiHelpers.GlobalScale));

        ImGui.Separator();
        ImGui.TextUnformatted("Preview");
        using (ImRaii.Child("##bbcode-test-preview", ImGuiHelpers.ScaledVector2(-1, 220), true))
        {
            _uiSharedService.RenderBbCode(_previewText, ImGui.GetContentRegionAvail().X);
        }
    }
    
}