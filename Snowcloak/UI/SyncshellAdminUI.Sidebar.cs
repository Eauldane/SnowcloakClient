using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.UI.Components;
using System.Numerics;

namespace Snowcloak.UI;

public partial class SyncshellAdminUI
{
    private bool PerformanceTabAvailable => _configService.Current.ShowSyncshellBudgetDashboard;

    private void NormalizeSelectedTab()
    {
        if (_selectedTab == SyncshellAdminTab.Performance && !PerformanceTabAvailable)
        {
            _selectedTab = SyncshellAdminTab.Settings;
        }

        if (_selectedTab == SyncshellAdminTab.Owner && !IsOwner)
        {
            _selectedTab = SyncshellAdminTab.Settings;
        }

        if (_selectedTab == SyncshellAdminTab.Events && !_apiController.SupportsRpFeatures)
        {
            _selectedTab = SyncshellAdminTab.Community;
        }
    }

    private void DrawAdminSidebar()
    {
        var sidebarWidth = ModernSidebar.ExpandedWidth * ImGuiHelpers.GlobalScale;

        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var childPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f) * ImGuiHelpers.GlobalScale);
        using var sidebar = ImRaii.Child("syncshell_admin_sidebar", new Vector2(sidebarWidth, -1), false);
        if (!sidebar)
        {
            return;
        }

        using var sidebarSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 8f * ImGuiHelpers.GlobalScale));
        ImGuiHelpers.ScaledDummy(9);

        if (PerformanceTabAvailable)
        {
            DrawSidebarTab(SyncshellAdminTab.Performance, FontAwesomeIcon.TachometerAlt, "Performance");
        }

        DrawSidebarTab(SyncshellAdminTab.Settings, FontAwesomeIcon.Cog, "Settings");
        DrawSidebarTab(SyncshellAdminTab.Community, FontAwesomeIcon.Bullhorn, "Community");
        if (_apiController.SupportsRpFeatures)
        {
            DrawSidebarTab(SyncshellAdminTab.Events, FontAwesomeIcon.CalendarDay, "Events");
        }
        ModernSidebar.DrawSeparator();
        DrawSidebarTab(SyncshellAdminTab.Members, FontAwesomeIcon.Users, "Members");
        ModernSidebar.DrawSeparator();
        DrawSidebarTab(SyncshellAdminTab.Permissions, FontAwesomeIcon.Wrench, "Permissions");
        DrawSidebarTab(SyncshellAdminTab.Audit, FontAwesomeIcon.History, "Audit History");

        if (IsOwner)
        {
            ModernSidebar.DrawSeparator();
            DrawSidebarTab(SyncshellAdminTab.Owner, FontAwesomeIcon.Crown, "Owner Settings");
        }
    }

    private void DrawSidebarTab(SyncshellAdminTab tab, FontAwesomeIcon icon, string label)
    {
        if (ModernSidebar.DrawRow(icon, label, _selectedTab == tab))
        {
            _selectedTab = tab;
        }
    }
}
