using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.UI.Components;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public partial class SettingsUi
{

    private void DrawSettingsSidebar()
    {
        var sidebarWidth = ModernSidebar.ExpandedWidth * ImGuiHelpers.GlobalScale;

        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var childPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f) * ImGuiHelpers.GlobalScale);
        using (ImRaii.Child("SettingsSidebar", new Vector2(sidebarWidth, -1), false))
        {
            using var sidebarSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 8f * ImGuiHelpers.GlobalScale));
            ImGuiHelpers.ScaledDummy(9);

            DrawSidebarTab(SettingsTab.General, FontAwesomeIcon.UserCog, "General");
            DrawSidebarTab(SettingsTab.Interface, FontAwesomeIcon.Palette, "Interface");
            DrawSidebarTab(SettingsTab.Notifications, FontAwesomeIcon.Bell, "Notifications");
            ModernSidebar.DrawSeparator();
            DrawSidebarTab(SettingsTab.Performance, FontAwesomeIcon.TachometerAlt, "Performance");
            DrawSidebarTab(SettingsTab.Storage, FontAwesomeIcon.Database, "Storage");
            DrawSidebarTab(SettingsTab.Transfers, FontAwesomeIcon.SyncAlt, "Transfers");
            ModernSidebar.DrawSeparator();
            DrawSidebarTab(SettingsTab.Service, FontAwesomeIcon.Server, "Service Settings");
            DrawSidebarTab(SettingsTab.Chat, FontAwesomeIcon.Comments, "Chat");
            ModernSidebar.DrawSeparator();
            DrawSidebarTab(SettingsTab.Advanced, FontAwesomeIcon.Wrench, "Advanced");

            var footerHeight = 64f * ImGuiHelpers.GlobalScale;
            var availableSpace = ImGui.GetContentRegionAvail().Y;
            if (availableSpace > footerHeight)
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + availableSpace - footerHeight);
            }

            DrawSidebarStatusFooter();
        }
    }

    private void DrawSidebarTab(SettingsTab tab, FontAwesomeIcon icon, string label)
    {
        if (ModernSidebar.DrawRow(icon, label, _selectedTab == tab))
        {
            _selectedTab = tab;
        }
    }

    private void DrawSidebarStatusFooter()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var cardMin = ImGui.GetCursorScreenPos();
        var cardSize = new Vector2(ImGui.GetContentRegionAvail().X, 64f * scale);
        var cardMax = cardMin + cardSize;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(cardMin, cardMax, Colour.Vector4ToColour(new Vector4(0.035f, 0.080f, 0.125f, 0.54f)), 3f * scale);
        drawList.AddLine(cardMin, cardMin with { X = cardMax.X }, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.40f)), 1f * scale);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 14f * scale);
        if (_apiController.ServerState is ServerState.Connected)
        {
            DrawSidebarCenteredText(_serverConfigurationManager.CurrentServer!.ServerName, SnowcloakColours.CompactTextMuted);
            ImGuiHelpers.ScaledDummy(2);
            DrawSidebarCenteredText(string.Format(CultureInfo.InvariantCulture, "{0} Users Online", _apiController.OnlineUsers), SnowcloakColours.OnlineBlue);
        }
        else
        {
            DrawSidebarCenteredText("Not connected", ImGuiColors.DalamudRed);
        }
    }

    private static void DrawSidebarCenteredText(string text, Vector4 color)
    {
        var min = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetTextLineHeight();
        ImGui.Dummy(new Vector2(1f, height));
        var textSize = ImGui.CalcTextSize(text);
        var pos = min + new Vector2((avail - textSize.X) * 0.5f, 0f);
        ImGui.GetWindowDrawList().AddText(pos, Colour.Vector4ToColour(color), text);
    }
}
