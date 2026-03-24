using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class SyncTroubleshootingUi : WindowMediatorSubscriberBase
{
    private readonly SyncTroubleshootingService _syncTroubleshootingService;
    private string? _statusMessage;

    public SyncTroubleshootingUi(ILogger<SyncTroubleshootingUi> logger, Pair pair, SnowMediator mediator,
        SyncTroubleshootingService syncTroubleshootingService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator,
            string.Format("Why am I not seeing {0}?###SnowcloakSyncTroubleshooting{1}", pair.UserData.AliasOrUID, pair.UserData.UID),
            performanceCollectorService)
    {
        Pair = pair;
        _syncTroubleshootingService = syncTroubleshootingService;
        IsOpen = true;
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(620, 440),
            MaximumSize = new Vector2(1600, 1600)
        };
    }

    public Pair Pair { get; }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    protected override void DrawInternal()
    {
        var report = _syncTroubleshootingService.BuildReport(Pair);

        ElezenImgui.ColouredWrappedText(
            "High-signal summary first. Raw detail below reflects your local Snowcloak client state for this target.",
            ImGuiColors.DalamudGrey);

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Copy, "Copy diagnostic report"))
        {
            ImGui.SetClipboardText(report.ClipboardText);
            _statusMessage = "Diagnostic report copied to clipboard.";
        }
        ElezenImgui.AttachTooltip("Copy the current summary and raw detail for support or debugging.");

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            ImGui.SameLine();
            ElezenImgui.ColouredWrappedText(_statusMessage, ImGuiColors.HealerGreen);
        }

        ImGui.Separator();
        foreach (var finding in report.Findings)
        {
            var colour = finding.Severity switch
            {
                SyncTroubleshootingSeverity.Error => ImGuiColors.DalamudRed,
                SyncTroubleshootingSeverity.Warning => ImGuiColors.DalamudYellow,
                _ => ImGuiColors.ParsedGreen,
            };

            using var textColour = ImRaii.PushColor(ImGuiCol.Text, colour);
            ImGui.TextUnformatted(finding.Title);
            textColour.Dispose();
            if (!string.IsNullOrWhiteSpace(finding.Detail))
            {
                ImGui.Indent();
                ElezenImgui.ColouredWrappedText(finding.Detail, ImGuiColors.DalamudGrey);
                ImGui.Unindent();
            }
            ImGui.Spacing();
        }

        ImGui.Separator();
        if (ImGui.CollapsingHeader("Raw detail", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var section in report.Sections)
            {
                using var tree = ImRaii.TreeNode(section.Title, ImGuiTreeNodeFlags.DefaultOpen);
                if (!tree.Success) continue;

                foreach (var line in section.Lines)
                {
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ElezenImgui.ColouredWrappedText(line, ImGuiColors.DalamudGrey);
                }
            }
        }
    }
}
