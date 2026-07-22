using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Core.Onboarding;
using System.Numerics;

namespace Snowcloak.UI;

public partial class IntroUi
{
    private void DrawSetupShell(OnboardingStep activeStep)
    {
        DrawSetupSidebar(activeStep);
        ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
        DrawSetupContent(activeStep);
    }

    private void DrawSetupSidebar(OnboardingStep activeStep)
    {
        var sidebarWidth = 210f * ImGuiHelpers.GlobalScale;
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var childPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,
            new Vector2(14f, 14f) * ImGuiHelpers.GlobalScale);
        using (ImRaii.Child("SetupSidebar", new Vector2(sidebarWidth, -1), false))
        {
            DrawSetupBrand();
            ImGuiHelpers.ScaledDummy(10);
            ModernSidebar.DrawSeparator();

            DrawSetupStepRow(activeStep, OnboardingStep.Welcome, FontAwesomeIcon.Snowflake,
                "Welcome", "Requirements");
            DrawSetupStepRow(activeStep, OnboardingStep.Agreement, FontAwesomeIcon.FileAlt,
                "Agreement", "Terms of service");
            DrawSetupStepRow(activeStep, OnboardingStep.Storage, FontAwesomeIcon.Database,
                "Storage", "Local files");
            DrawSetupStepRow(activeStep, OnboardingStep.Service, FontAwesomeIcon.Server,
                "Connect", "Service sign-in");
        }
    }

    private void DrawSetupBrand()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var rowHeight = 32f * scale;
        var min = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, rowHeight));
        var drawList = ImGui.GetWindowDrawList();

        if (_logoTextureHandle is { } logoTextureHandle)
        {
            drawList.AddImage(logoTextureHandle, min, min + new Vector2(rowHeight));
        }
        else
        {
            ImGui.PushFont(UiBuilder.IconFont);
            var fallback = FontAwesomeIcon.Snowflake.ToIconString();
            var fallbackSize = ImGui.CalcTextSize(fallback);
            drawList.AddText(min + new Vector2((rowHeight - fallbackSize.X) * 0.5f, (rowHeight - fallbackSize.Y) * 0.5f),
                Colour.Vector4ToColour(SnowcloakColours.OnlineBlue), fallback);
            ImGui.PopFont();
        }

        using (_fontService.UidFont.Push())
        {
            const string wordmark = "SNOWCLOAK";
            var wordmarkSize = ImGui.CalcTextSize(wordmark);
            drawList.AddText(new Vector2(min.X + rowHeight + 7f * scale,
                    min.Y + (rowHeight - wordmarkSize.Y) * 0.5f),
                Colour.Vector4ToColour(Vector4.One), wordmark);
        }
    }

    private static void DrawSetupStepRow(OnboardingStep activeStep, OnboardingStep step,
        FontAwesomeIcon icon, string title, string subtitle)
    {
        var active = step == activeStep;
        var completed = step < activeStep;
        var scale = ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;
        var height = 54f * scale;
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        ImGui.Dummy(new Vector2(width, height));
        var drawList = ImGui.GetWindowDrawList();

        if (active)
        {
            var left = Colour.Vector4ToColour(Colour.WithAlpha(SnowcloakColours.OnlineBlue, 0.34f));
            var right = Colour.Vector4ToColour(Colour.WithAlpha(SnowcloakColours.OnlineBlue, 0.06f));
            drawList.AddRectFilledMultiColor(min, max, left, right, right, left);
            drawList.AddRectFilled(min + new Vector2(0f, 7f * scale),
                new Vector2(min.X + 3f * scale, max.Y - 7f * scale),
                Colour.Vector4ToColour(SnowcloakColours.OnlineBlue));
        }

        var rowIcon = completed ? FontAwesomeIcon.CheckCircle : icon;
        var iconColour = active
            ? Vector4.One
            : completed ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactTextMuted;
        ImGui.PushFont(UiBuilder.IconFont);
        var iconText = rowIcon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconText);
        drawList.AddText(new Vector2(min.X + 12f * scale, min.Y + (height - iconSize.Y) * 0.5f),
            Colour.Vector4ToColour(iconColour), iconText);
        ImGui.PopFont();

        var textX = min.X + 40f * scale;
        var titleColour = active || completed ? Vector4.One : SnowcloakColours.CompactTextMuted;
        drawList.AddText(new Vector2(textX, min.Y + 9f * scale),
            Colour.Vector4ToColour(titleColour), title);
        drawList.AddText(new Vector2(textX, min.Y + 30f * scale),
            Colour.Vector4ToColour(SnowcloakColours.CompactTextMuted), subtitle);
    }

    private void DrawSetupContent(OnboardingStep step)
    {
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactBg);
        using var contentPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,
            new Vector2(20f, 16f) * ImGuiHelpers.GlobalScale);
        using (ImRaii.Child("SetupContent", new Vector2(-1, -1), false))
        {
            using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing,
                new Vector2(ImGui.GetStyle().ItemSpacing.X, 8f * ImGuiHelpers.GlobalScale));
            switch (step)
            {
                case OnboardingStep.Welcome:
                    DrawWelcomePage();
                    break;
                case OnboardingStep.Agreement:
                    DrawAgreementPage();
                    break;
                case OnboardingStep.Storage:
                    DrawStoragePage();
                    break;
                case OnboardingStep.Service:
                    DrawServicePage();
                    break;
            }
        }
    }
}
