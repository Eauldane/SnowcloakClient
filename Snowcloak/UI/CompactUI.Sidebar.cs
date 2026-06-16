using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ElezenTools.UI;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Account;
using Snowcloak.API.Dto.User;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.UI.Handlers;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Snowcloak.UI;

public partial class CompactUi
{
    private const float ExpandedSidebarWidth = ModernSidebar.ExpandedWidth;

    private static void DrawSidebarSeparator() => ModernSidebar.DrawSeparator();

    private void DrawSidebarButton(Menu menu, FontAwesomeIcon icon, string label)
    {
        bool isActive = _selectedMenu == menu;

        if (ModernSidebar.DrawRow(icon, label, isActive))
        {
            _selectedMenu = menu;
        }
    }
    // helper for buttons that dont cause state change
    private static void DrawSidebarAction(FontAwesomeIcon icon, string label, Action onClick)
    {
        if (ModernSidebar.DrawRow(icon, label, active: false))
        {
            onClick();
        }
    }

    private string GetFrostbrandSidebarLabel()
    {
        var label = "Frostbrand";
        var pending = _pairRequestService.AvailabilityStore.State.PendingRequestCount;

        return pending > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", label, pending)
            : label;
    }
    private void DrawSidebar()
    {
        var sidebarWidth = ExpandedSidebarWidth * ImGuiHelpers.GlobalScale;

        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var childPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f) * ImGuiHelpers.GlobalScale);
        using (var child = ImRaii.Child("Sidebar", new Vector2(sidebarWidth, -1), false))
        {
            using var sidebarPadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(10f, 9f) * ImGuiHelpers.GlobalScale);
            using var sidebarSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 8f * ImGuiHelpers.GlobalScale));
            ImGuiHelpers.ScaledDummy(9);

            // Buttons with state change
            DrawSidebarButton(Menu.IndividualPairs, FontAwesomeIcon.User, "Direct Pairs");
            DrawSidebarButton(Menu.Syncshells, FontAwesomeIcon.PeopleGroup, "Syncshells");
            if (_configService.Current.ShowCompactUiPerformanceTab)
            {
                DrawSidebarButton(Menu.Performance, FontAwesomeIcon.ChartBar, "Performance");
            }
            DrawSidebarSeparator();
            if (_apiController.ServerState is ServerState.Connected)
            {
                DrawSidebarButton(Menu.Frostbrand, FontAwesomeIcon.Snowflake, GetFrostbrandSidebarLabel());
            }
            DrawSidebarSeparator();
            //buttons without state change
            DrawSidebarAction(FontAwesomeIcon.ChartBar, "Character Analysis",
                () => Mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi))));
            //Abbrivated because Character Data Hub is too long and loogs ugly in the lables
            DrawSidebarAction(FontAwesomeIcon.UserFriends, "Character Hub",
                () => Mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi))));
            DrawSidebarAction(FontAwesomeIcon.MapMarkedAlt, "Venues",
                () => Mediator.Publish(new UiToggleMessage(typeof(VenueAdsWindow))));
            DrawSidebarAction(FontAwesomeIcon.Comments, "Chat [BETA]",
                () => Mediator.Publish(new UiToggleMessage(typeof(ChatWindow))));
            DrawSidebarAction(FontAwesomeIcon.Cog, "Settings",
                () => Mediator.Publish(new UiToggleMessage(typeof(SettingsUi))));
            DrawSidebarSeparator();

            if (_apiController.ServerState is ServerState.Connected)
            {
                DrawSidebarAction(FontAwesomeIcon.UserCircle, "Edit Profile",
                    () => Mediator.Publish(new UiToggleMessage(typeof(EditProfileUi))));
            }

            DrawSidebarAction(FontAwesomeIcon.Book, "User Guide",
                () => Util.OpenLink("https://docs.snowcloak-sync.com"));
            float bottomElementsHeight = 108f * ImGuiHelpers.GlobalScale;
            var availableSpace = ImGui.GetContentRegionAvail().Y;
            if (availableSpace > bottomElementsHeight)
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + availableSpace - bottomElementsHeight);
            }

            var connectedIcon = _serverManager.CurrentServer!.FullPause ? FontAwesomeIcon.Unlink : FontAwesomeIcon.Link;
            var color = !_serverManager.CurrentServer!.FullPause ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactOffline;

            DrawSidebarServerFooter(connectedIcon, color);
        }
    }

    private void DrawSidebarServerFooter(FontAwesomeIcon connectedIcon, Vector4 connectionColor)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var cardMin = ImGui.GetCursorScreenPos();
        var cardSize = new Vector2(ImGui.GetContentRegionAvail().X, 108f * scale);
        var cardMax = cardMin + cardSize;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(cardMin, cardMax, Colour.Vector4ToColour(new Vector4(0.035f, 0.080f, 0.125f, 0.54f)), 3f * scale);
        drawList.AddLine(cardMin, cardMin with { X = cardMax.X }, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.40f)), 1f * scale);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 12f * scale);
        if (_apiController.ServerState is ServerState.Connected)
        {
            var userCount = _apiController.OnlineUsers.ToString(CultureInfo.InvariantCulture);
            DrawSidebarStatusLine(string.Format(CultureInfo.InvariantCulture, "{0} Users Online", userCount), Colour.Lighten(SnowcloakUi.AccentColor, 0.12f));
        }
        else
        {
            DrawSidebarStatusLine("Not connected", ImGuiColors.DalamudRed);
        }

        ImGuiHelpers.ScaledDummy(10);

        if (_apiController.ServerState is ServerState.Reconnecting or ServerState.Disconnecting)
            return;

        var label = !_serverManager.CurrentServer!.FullPause ? "Disconnect" : "Connect";
        if (DrawSidebarFooterButton(connectedIcon, label, connectionColor))
        {
            ToggleServerConnection();
        }
        ElezenImgui.AttachTooltip(GetServerConnectionTooltip());
    }

    private static void DrawSidebarStatusLine(string label, Vector4 color)
    {
        var min = ImGui.GetCursorScreenPos();
        var height = ImGui.GetTextLineHeightWithSpacing();
        var drawList = ImGui.GetWindowDrawList();

        ImGui.Dummy(new Vector2(1f, height));
        var textSize = ImGui.CalcTextSize(label);
        var textPos = min + new Vector2((ImGui.GetContentRegionAvail().X - textSize.X) * 0.5f, 0f);
        drawList.AddText(textPos + new Vector2(0f, (height - textSize.Y) * 0.5f), Colour.Vector4ToColour(color), label);
    }

    private static bool DrawSidebarFooterButton(FontAwesomeIcon icon, string label, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(ImGui.GetContentRegionAvail().X - 18f * scale, 38f * scale);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 9f * scale);
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##sidebar-footer-{label}", size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        var fill = hovered
            ? new Vector4(0.080f, 0.145f, 0.220f, 0.86f)
            : new Vector4(0.050f, 0.100f, 0.160f, 0.68f);
        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(fill), 3f * scale);
        drawList.AddRect(min, max, Colour.Vector4ToColour(new Vector4(color.X, color.Y, color.Z, hovered ? 0.95f : 0.72f)), 3f * scale, ImDrawFlags.None, 1f * scale);

        var iconText = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText);
        ImGui.PopFont();
        var textSize = ImGui.CalcTextSize(label);
        var contentWidth = iconSize.X + 10f * scale + textSize.X;
        var iconPos = min + new Vector2((size.X - contentWidth) * 0.5f, 0f);
        iconPos.Y = min.Y + (size.Y - iconSize.Y) * 0.5f;
        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(iconPos, Colour.Vector4ToColour(color), iconText);
        ImGui.PopFont();
        drawList.AddText(new Vector2(iconPos.X + iconSize.X + 10f * scale, min.Y + (size.Y - textSize.Y) * 0.5f), Colour.Vector4ToColour(color), label);

        return clicked;
    }

    private string GetServerConnectionTooltip()
    {
        var tooltip = !_serverManager.CurrentServer!.FullPause
            ? string.Format(CultureInfo.InvariantCulture, "Disconnect from {0}", _serverManager.CurrentServer.ServerName)
            : string.Format(CultureInfo.InvariantCulture, "Connect to {0}", _serverManager.CurrentServer.ServerName);

        if (_apiController.ServerState is not ServerState.Connected)
        {
            return tooltip;
        }

        var activeAnnouncements = _apiController.SystemInfoDto.Announcements
            .Where(a => !_dismissedAnnouncementIds.Contains(a.Id))
            .Take(3)
            .ToList();
        if (activeAnnouncements.Count == 0)
        {
            return tooltip;
        }

        return tooltip
            + ElezenImgui.TooltipSeparator
            + string.Join(Environment.NewLine, activeAnnouncements.Select(a => a.IsMaintenance ? "Maintenance: " + a.Text : a.Text));
    }

    private void ToggleServerConnection()
    {
        _serverManager.CurrentServer!.FullPause = !_serverManager.CurrentServer.FullPause;
        _serverManager.Save();
        _ = _apiController.CreateConnections();
    }
}
