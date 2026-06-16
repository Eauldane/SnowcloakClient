using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using ElezenTools.UI.Mvu;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.Pairing;

namespace Snowcloak.UI.Components;

internal sealed class FrostbrandPanel
{
    private enum FrostbrandPanelView
    {
        Welcome,
        Pending,
        Settings
    }

    private readonly SnowcloakConfigService _configService;
    private readonly PairingAvailabilityStore _store;
    private readonly IDispatcher _dispatcher;
    private readonly FrostbrandEnableFlow _enableFlow;
    private readonly FrostbrandFilterEditor _filterEditor;
    private readonly string _localisationPrefix;
    private FrostbrandPanelView _activeView = FrostbrandPanelView.Pending;
    private bool _defaultViewInitialised;
    private bool _wasPairingEnabled;

    public FrostbrandPanel(SnowcloakConfigService configService, PairingFilterConfigService filterConfigService,
        UiFontService fontService, DalamudUtilService dalamudUtilService, PairDisplayDecorationService guiHookService,
        PairingAvailabilityStore store, IDispatcher dispatcher, string localisationPrefix = "SettingsUi")
    {
        _configService = configService;
        _store = store;
        _dispatcher = dispatcher;
        _localisationPrefix = localisationPrefix;
        _enableFlow = new FrostbrandEnableFlow();
        _filterEditor = new FrostbrandFilterEditor(configService, filterConfigService, fontService,
            dalamudUtilService, guiHookService);
    }

    public void Draw()
    {
        var state = _store.State;
        using var itemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing,
            new Vector2(ImGui.GetStyle().ItemSpacing.X, 7f * ImGuiHelpers.GlobalScale));

        FrostbrandPanelChrome.DrawSectionTitle(FontAwesomeIcon.Snowflake, "Frostbrand Pairing");
        _enableFlow.Draw(state, _dispatcher);

        if (state.PairingEnabled != _wasPairingEnabled)
        {
            _defaultViewInitialised = false;
            _wasPairingEnabled = state.PairingEnabled;
        }

        if (!_defaultViewInitialised)
        {
            _activeView = DetermineDefaultView(state.PairingEnabled);
            _defaultViewInitialised = true;
        }

        using (ImRaii.Disabled(!state.PairingEnabled))
        {
            DrawNavigationRow(state.PendingRequestCount);

            ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

            switch (_activeView)
            {
                case FrostbrandPanelView.Welcome:
                    DrawWelcomeSection();
                    break;
                case FrostbrandPanelView.Pending:
                    FrostbrandPendingRequestsView.Draw(state, _dispatcher, _localisationPrefix);
                    break;
                case FrostbrandPanelView.Settings:
                    _filterEditor.Draw();
                    break;
            }
        }

        DrawFooterNote();
    }

    private FrostbrandPanelView DetermineDefaultView(bool pairingEnabled)
    {
        if (pairingEnabled && !_configService.Current.FrostbrandWelcomeSeen)
        {
            _configService.Update(c => c.FrostbrandWelcomeSeen = true);
            return FrostbrandPanelView.Welcome;
        }

        return FrostbrandPanelView.Pending;
    }

    private void DrawNavigationRow(int pendingCount)
    {
        var buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        var pendingLabel = pendingCount > 0
            ? $"{"Pending requests"} ({pendingCount})"
            : "Pending requests";

        DrawTabButton(FrostbrandPanelView.Pending, pendingLabel, buttonWidth);
        ImGui.SameLine();
        DrawTabButton(FrostbrandPanelView.Settings, "Frostbrand settings", buttonWidth);
    }

    private void DrawTabButton(FrostbrandPanelView view, string label, float width)
    {
        var isActive = _activeView == view;
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(width, 34f * scale);
        var min = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton($"##frostbrand-tab-{view}", size);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            _activeView = view;

        var hovered = ImGui.IsItemHovered();
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        var fill = isActive
            ? new Vector4(SnowcloakColours.OnlineBlue.X, SnowcloakColours.OnlineBlue.Y, SnowcloakColours.OnlineBlue.Z, 0.19f)
            : hovered
                ? new Vector4(0.075f, 0.130f, 0.185f, 0.50f)
                : new Vector4(0.035f, 0.080f, 0.115f, 0.62f);

        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(fill), 0f);
        drawList.AddLine(min with { Y = max.Y }, max, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, isActive ? 0.62f : 0.24f)), 1f * scale);

        if (isActive)
            drawList.AddLine(min, min with { Y = max.Y }, Colour.Vector4ToColour(SnowcloakColours.OnlineBlue), 2f * scale);

        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(min + new Vector2((size.X - textSize.X) * 0.5f, (size.Y - textSize.Y) * 0.5f),
            Colour.Vector4ToColour(isActive ? Vector4.One : SnowcloakColours.CompactTextMuted), label);
    }

    private static void DrawWelcomeSection()
    {
        DrawNoticeCard(
            FontAwesomeIcon.Snowflake,
            "Welcome to Frostbrand",
            "You've opted into Frostbrand. Pending pair requests will be listed here, and you can manage filters from the Frostbrand settings tab.",
            "Other opted-in Frostbrand users can see that you're using Snowcloak while Frostbrand is enabled.");
    }

    private static void DrawNoticeCard(FontAwesomeIcon icon, string title, string body, string secondary)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var startCursor = ImGui.GetCursorPos();
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 96f * scale;
        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(new Vector4(0.030f, 0.075f, 0.108f, 0.62f)), 0f);
        drawList.AddLine(min, min with { Y = max.Y }, Colour.Vector4ToColour(new Vector4(SnowcloakColours.OnlineBlue.X, SnowcloakColours.OnlineBlue.Y, SnowcloakColours.OnlineBlue.Z, 0.62f)), 2f * scale);
        drawList.AddLine(min, min with { X = max.X }, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.32f)), 1f * scale);

        var padding = new Vector2(12f, 10f) * scale;
        ImGui.SetCursorScreenPos(min + padding);
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, icon.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine(0f, 10f * scale);
        ImGui.BeginGroup();
        ImGui.TextUnformatted(title);
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
        {
            ImGui.TextWrapped(body);
            ImGui.TextWrapped(secondary);
        }
        ImGui.EndGroup();

        ImGui.SetCursorPosY(MathF.Max(ImGui.GetCursorPosY(), startCursor.Y + height + 6f * scale));
    }

    private static void DrawFooterNote()
    {
        var scale = ImGuiHelpers.GlobalScale;
        const string note = "Only users who also have Frostbrand pairing enabled will be able to send or receive requests.";
        var width = ImGui.GetContentRegionAvail().X;
        var padding = new Vector2(12f, 9f) * scale;
        var iconGap = 10f * scale;

        ImGui.PushFont(UiBuilder.IconFont);
        var iconStr = FontAwesomeIcon.InfoCircle.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();

        var textWidth = MathF.Max(40f * scale, width - padding.X * 2f - iconSize.X - iconGap);
        var lines = FrostbrandPanelChrome.WrapText(note, textWidth);
        var lineHeight = ImGui.GetTextLineHeight();
        var spacingY = ImGui.GetStyle().ItemSpacing.Y;
        var textHeight = lines.Count * lineHeight + MathF.Max(0, lines.Count - 1) * spacingY;
        var boxHeight = MathF.Max(iconSize.Y, textHeight) + padding.Y * 2f;

        var reserve = ImGui.GetFrameHeightWithSpacing() + 6f * scale;
        var avail = ImGui.GetContentRegionAvail().Y;
        if (avail > boxHeight + reserve)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (avail - boxHeight - reserve));

        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, boxHeight);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(new Vector4(0.030f, 0.075f, 0.108f, 0.62f)), 4f * scale);
        drawList.AddRect(min, max, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.45f)), 4f * scale, ImDrawFlags.None, 1f * scale);

        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(new Vector2(min.X + padding.X, min.Y + (boxHeight - iconSize.Y) * 0.5f),
            Colour.Vector4ToColour(SnowcloakColours.CompactTextMuted), iconStr);
        ImGui.PopFont();

        var textX = min.X + padding.X + iconSize.X + iconGap;
        var textStartY = min.Y + (boxHeight - textHeight) * 0.5f;
        for (var i = 0; i < lines.Count; i++)
            drawList.AddText(new Vector2(textX, textStartY + i * (lineHeight + spacingY)),
                Colour.Vector4ToColour(SnowcloakColours.CompactTextMuted), lines[i]);

        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
    }
}
