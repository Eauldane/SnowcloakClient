using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;

namespace Snowcloak.UI;

internal sealed class SupportSnowcloakWindow : WindowMediatorSubscriberBase, IStaticWindow
{
    private const float MinimumWidth = 300f;
    private const string SupportCopy = "Thank you for thinking about supporting Snowcloak!\n\nWithout people like you, Snowcloak wouldn't be able to continue running. As thanks for helping, monthly donators can " +
                                       "connect their backer account to unlock special perks!\n\nCustom name colours in the pair list, overworld, and chat, as well as higher image storage and backup size limits are available" +
                                       "on all tiers. Alternatively, if you'd like to just throw us a tip to grab dinner, feel free to donate to the Ko-fi.\n\nThank you once again!";
    private const string PatreonUrl = "https://patreon.com/elznmods";
    private const string KoFiUrl = "https://ko-fi.com/eauldane";

    public SupportSnowcloakWindow(ILogger<SupportSnowcloakWindow> logger, SnowMediator mediator,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Support Snowcloak!###SnowcloakSupport", performanceCollectorService)
    {
        Flags |= ImGuiWindowFlags.NoDocking;
        Size = new Vector2(380, 160);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        var wrapWidth = Math.Max(1f, MinimumWidth * scale - 2f * style.WindowPadding.X);
        var copyHeight = ImGui.CalcTextSize(SupportCopy, hideTextAfterDoubleHash: false, wrapWidth).Y;
        var minimumHeight = ImGui.GetFrameHeight() + 2f * style.WindowPadding.Y
            + copyHeight + OutlinedActionButton.Height + style.ItemSpacing.Y;
        SetScaledSizeConstraints(new Vector2(MinimumWidth, MathF.Ceiling(minimumHeight) / scale));
    }

    protected override void DrawInternal()
    {
        var buttonRowHeight = OutlinedActionButton.Height + ImGui.GetStyle().ItemSpacing.Y;
        using (var content = ImRaii.Child("support-copy", new Vector2(0, -buttonRowHeight), false))
        {
            if (content)
                ImGui.TextWrapped(SupportCopy);
        }

        var buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        if (OutlinedActionButton.Draw(FontAwesomeIcon.Heart, "Patreon", ElezenColours.SnowcloakBlue, buttonWidth))
            Util.OpenLink(PatreonUrl);

        ImGui.SameLine();
        if (OutlinedActionButton.Draw(FontAwesomeIcon.ExternalLinkAlt, "Ko-fi", ElezenColours.SnowcloakBlue, buttonWidth))
            Util.OpenLink(KoFiUrl);
    }
}
