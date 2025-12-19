using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components.BbCode;
using Snowcloak.Services.Localisation;

namespace Snowcloak.UI;

public sealed class BbCodeTestUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiSharedService;
    private readonly LocalisationService _localisationService;
    private string _previewText = "[color=#ff6699]Colour[/color] [b]bold[/b] [i]italic[/i] [u]underline[/u]\n[list][*]Generic list\n[/list]\n[ol][*]Ordered List\n[/ol]\n[ul][*]Unordered List\n[/ul][size=125%]Scaled up text[/size] and [size=12]12px text[/size]\n[center]Centered line[/center]\n[align=right]Right aligned line[/align]\n[url=https://snowcloak-sync.com]Link with label[/url] and [url]https://snowcloak-sync.com[/url]\nImage: [img]https://raw.githubusercontent.com/Eauldane/SnowcloakClient/refs/heads/main/Snowcloak/images/logo.png[/img]\nEmote test: :smile: :sparkle:";

    public BbCodeTestUi(ILogger<BbCodeTestUi> logger, SnowMediator mediator, UiSharedService uiSharedService,
        PerformanceCollectorService performanceCollectorService, LocalisationService localisationService) : base(logger, mediator, "Snowcloak BBCode Tester###SnowcloakBBCodeTestUi", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _localisationService = localisationService;
        WindowName = L("Window.Title", "Snowcloak BBCode Tester") + "###SnowcloakBBCodeTestUi";
        IsOpen = false;
        SizeConstraints = new()
        {
            MinimumSize = ImGuiHelpers.ScaledVector2(520, 460),
            MaximumSize = ImGuiHelpers.ScaledVector2(1200, 1000)
        };
    }
    
    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"BbCodeTest.{key}", fallback);
    }


    protected override void DrawInternal()
    {
        ImGui.TextWrapped(L("Intro.Description", "Preview how Snowcloak renders BBCode inside profile and venue descriptions."));
        UiSharedService.ColorTextWrapped(L("Intro.Supported", "Supported tags include colours, bold, italics, underline, links, images, emotes, and basic alignment."), ImGuiColors.DalamudGrey);
        
        ImGui.Separator();
        ImGui.TextUnformatted(L("Input.Label", "BBCode Input"));
        ImGui.InputTextMultiline("##bbcode-test-input", ref _previewText, 8000, ImGuiHelpers.ScaledVector2(-1, 200 * ImGuiHelpers.GlobalScale));

        ImGui.Separator();
        ImGui.TextUnformatted(L("Preview.Label", "Preview"));
        using (ImRaii.Child("##bbcode-test-preview", ImGuiHelpers.ScaledVector2(-1, 220), true))
        {
            _uiSharedService.RenderBbCode(_previewText, ImGui.GetContentRegionAvail().X);
        }

        //if (ImGui.CollapsingHeader("Available emotes"))
        //{
        //    foreach (var mapping in _uiSharedService.BbCodeRenderer.EmoteMappings)
        //    {
        //        ImGui.BulletText($":{mapping.Key}: -> {Path.GetFileName(mapping.Value)}");
        //        ImGui.SameLine();
        //        _uiSharedService.RenderBbCode($":{mapping.Key}:", 64f, new BbCodeRenderOptions(EmoteSize: 28f));
        //    }
        //}
    }
    
}