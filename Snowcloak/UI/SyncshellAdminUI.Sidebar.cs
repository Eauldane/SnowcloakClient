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

        if (_selectedTab == SyncshellAdminTab.Owner && !_isOwner)
        {
            _selectedTab = SyncshellAdminTab.Settings;
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
        DrawSidebarTab(SyncshellAdminTab.Directory, FontAwesomeIcon.Globe, "Directory");
        ModernSidebar.DrawSeparator();
        DrawSidebarTab(SyncshellAdminTab.Members, FontAwesomeIcon.Users, "Members");
        DrawSidebarTab(SyncshellAdminTab.Invites, FontAwesomeIcon.Envelope, "Invites");
        DrawSidebarTab(SyncshellAdminTab.Cleanup, FontAwesomeIcon.Broom, "Cleanup");
        DrawSidebarTab(SyncshellAdminTab.Bans, FontAwesomeIcon.Ban, "Bans");
        ModernSidebar.DrawSeparator();
        DrawSidebarTab(SyncshellAdminTab.Permissions, FontAwesomeIcon.Wrench, "Permissions");
        DrawSidebarTab(SyncshellAdminTab.Audit, FontAwesomeIcon.History, "Audit History");

        if (_isOwner)
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
