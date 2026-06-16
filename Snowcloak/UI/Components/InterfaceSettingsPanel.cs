using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Numerics;

namespace Snowcloak.UI.Components;

public sealed partial class InterfaceSettingsPanel
{
    private readonly SnowcloakConfigService _configService;
    private readonly PairDisplayDecorationService _displayDecorationService;
    private readonly ILogger<InterfaceSettingsPanel> _logger;
    private readonly SnowMediator _mediator;
    private readonly UiFontService _fontService;
    private readonly Dictionary<string, object> _selectedComboItems = new(StringComparer.Ordinal);

    public InterfaceSettingsPanel(
        SnowcloakConfigService configService,
        PairDisplayDecorationService displayDecorationService,
        ILogger<InterfaceSettingsPanel> logger,
        SnowMediator mediator,
        UiFontService fontService)
    {
        _configService = configService;
        _displayDecorationService = displayDecorationService;
        _logger = logger;
        _mediator = mediator;
        _fontService = fontService;
    }

    public void Draw()
    {
        _fontService.BigText("Game Integration");
        var enableRightClickMenu = _configService.Current.EnableRightClickMenus;

        if (ImGui.Checkbox("Enable Game Right Click Menu Entries", ref enableRightClickMenu))
        {
            _configService.Update(c => c.EnableRightClickMenus = enableRightClickMenu);
        }
        ElezenImgui.DrawHelpText("This will add Snowcloak related right click menu entries in the game UI on paired players.");

        DrawServerInfoBar();
        DrawNameplates();
        DrawProfiles();
    }

    private void DrawServerInfoBar()
    {
        var enableDtrEntry = _configService.Current.EnableDtrEntry;
        var showUidInDtrTooltip = _configService.Current.ShowUidInDtrTooltip;
        var preferNoteInDtrTooltip = _configService.Current.PreferNoteInDtrTooltip;
        var useColorsInDtr = _configService.Current.UseColorsInDtr;
        var dtrColorsDefault = _configService.Current.DtrColorsDefault;
        var dtrColorsNotConnected = _configService.Current.DtrColorsNotConnected;
        var dtrColorsPairsInRange = _configService.Current.DtrColorsPairsInRange;
        var dtrColorsPendingRequests = _configService.Current.DtrColorsPendingRequests;

        ImGui.Separator();
        _fontService.BigText("Server Info Bar");
        if (ImGui.Checkbox("Display status and visible pair count in Server Info Bar", ref enableDtrEntry))
        {
            _configService.Update(c => c.EnableDtrEntry = enableDtrEntry);
        }
        ElezenImgui.DrawHelpText("This will add Snowcloak connection status and visible pair count in the Server Info Bar.\nYou can further configure this through your Dalamud Settings.");

        using (ImRaii.Disabled(!enableDtrEntry))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox("Show visible character's UID in tooltip", ref showUidInDtrTooltip))
            {
                _configService.Update(c => c.ShowUidInDtrTooltip = showUidInDtrTooltip);
            }

            if (ImGui.Checkbox("Prefer notes over player names in tooltip", ref preferNoteInDtrTooltip))
            {
                _configService.Update(c => c.PreferNoteInDtrTooltip = preferNoteInDtrTooltip);
            }

            ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
            SettingsUiControls.DrawCombo("Server Info Bar style", Enumerable.Range(0, DtrEntry.NumStyles), i => DtrEntry.RenderDtrStyle(i, "123"),
                _selectedComboItems,
                i => _configService.Update(c => c.DtrStyle = i),
                _configService.Current.DtrStyle);

            if (ImGui.Checkbox("Color-code the Server Info Bar entry according to status", ref useColorsInDtr))
            {
                _configService.Update(c => c.UseColorsInDtr = useColorsInDtr);
            }

            using (ImRaii.Disabled(!useColorsInDtr))
            {
                using var colourIndent = ImRaii.PushIndent();
                if (ImGui.BeginTable("DtrColorTable", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableNextColumn();
                    if (SettingsUiControls.InputDtrColors("Default", ref dtrColorsDefault))
                    {
                        _configService.Update(c => c.DtrColorsDefault = dtrColorsDefault);
                    }

                    ImGui.TableNextColumn();
                    if (SettingsUiControls.InputDtrColors("Not Connected", ref dtrColorsNotConnected))
                    {
                        _configService.Update(c => c.DtrColorsNotConnected = dtrColorsNotConnected);
                    }

                    ImGui.TableNextColumn();
                    if (SettingsUiControls.InputDtrColors("Pairs in Range", ref dtrColorsPairsInRange))
                    {
                        _configService.Update(c => c.DtrColorsPairsInRange = dtrColorsPairsInRange);
                    }

                    ImGui.TableNextColumn();
                    if (SettingsUiControls.InputDtrColors("Pending Requests", ref dtrColorsPendingRequests))
                    {
                        _configService.Update(c => c.DtrColorsPendingRequests = dtrColorsPendingRequests);
                    }

                    ImGui.EndTable();
                }
            }
        }
    }

    private void DrawNameplates()
    {
        var showCharacterNames = _configService.Current.ShowCharacterNames;
        var showVisibleSeparate = _configService.Current.ShowVisibleUsersSeparately;
        var showSyncshellBudgetDashboard = _configService.Current.ShowSyncshellBudgetDashboard;
        var showCompactUiPerformanceTab = _configService.Current.ShowCompactUiPerformanceTab;
        var sortSyncshellByVRAM = _configService.Current.SortSyncshellsByVRAM;
        var showOfflineSeparate = _configService.Current.ShowOfflineUsersSeparately;

        ImGui.Separator();
        _fontService.BigText("Nameplates");
        var useNameColors = _configService.Current.UseNameColors;
        var nameColors = _configService.Current.NameColors;
        var autoPausedNameColors = _configService.Current.BlockedNameColors;
        if (ImGui.Checkbox("Color nameplates of paired players", ref useNameColors))
        {
            _configService.Update(c => c.UseNameColors = useNameColors);
            _displayDecorationService.RequestRedraw();
        }

        using (ImRaii.Disabled(!useNameColors))
        {
            using var indent = ImRaii.PushIndent();
            if (SettingsUiControls.InputDtrColors("Character Name Color", ref nameColors))
            {
                _configService.Update(c => c.NameColors = nameColors);
                _displayDecorationService.RequestRedraw();
            }

            ImGui.SameLine();

            if (SettingsUiControls.InputDtrColors("Blocked Character Color", ref autoPausedNameColors))
            {
                _configService.Update(c => c.BlockedNameColors = autoPausedNameColors);
                _displayDecorationService.RequestRedraw();
            }
        }

        ImGui.Separator();
        _fontService.BigText("Pair List");
        if (ImGui.Checkbox("Show separate Visible group", ref showVisibleSeparate))
        {
            _configService.Update(c => c.ShowVisibleUsersSeparately = showVisibleSeparate);
        }
        ElezenImgui.DrawHelpText("This will show all currently visible users in a special 'Visible' group in the main UI.");

        if (ImGui.Checkbox("Sort visible syncshell users by VRAM usage", ref sortSyncshellByVRAM))
        {
            _configService.Update(c => c.SortSyncshellsByVRAM = sortSyncshellByVRAM);
            LogSortSyncshellsByVramChanged(_logger, sortSyncshellByVRAM);
        }
        ElezenImgui.DrawHelpText("This will put users using the most VRAM in a syncshell at the top of the list.");

        if (ImGui.Checkbox("Show syncshell performance panels", ref showSyncshellBudgetDashboard))
        {
            _configService.Update(c => c.ShowSyncshellBudgetDashboard = showSyncshellBudgetDashboard);
        }
        ElezenImgui.DrawHelpText("Shows the syncshell performance summary in expanded syncshells and in the syncshell admin window.");

        if (ImGui.Checkbox("Show CompactUI performance tab", ref showCompactUiPerformanceTab))
        {
            _configService.Update(c => c.ShowCompactUiPerformanceTab = showCompactUiPerformanceTab);
        }
        ElezenImgui.DrawHelpText("Shows a dedicated CompactUI performance dashboard with total GPU memory pressure when available, visible Snowcloak load, auto-block totals, and top offenders.");

        if (ImGui.Checkbox("Group users by connection status", ref showOfflineSeparate))
        {
            _configService.Update(c => c.ShowOfflineUsersSeparately = showOfflineSeparate);
        }
        ElezenImgui.DrawHelpText("This will categorize users by their connection status in the main UI.");

        if (ImGui.Checkbox("Show player names", ref showCharacterNames))
        {
            _configService.Update(c => c.ShowCharacterNames = showCharacterNames);
        }
        ElezenImgui.DrawHelpText("This will show character names instead of UIDs when possible");
    }

    private void DrawProfiles()
    {
        var showProfiles = _configService.Current.ProfilesShow;
        var showNsfwProfiles = _configService.Current.ProfilesAllowNsfw;
        var profileDelay = _configService.Current.ProfileDelay;
        var profileOnRight = _configService.Current.ProfilePopoutRight;
        var allowBbCodeImages = _configService.Current.AllowBbCodeImages;

        ImGui.Separator();
        _fontService.BigText("Profiles");
        if (ImGui.Checkbox("Show Profiles on Hover", ref showProfiles))
        {
            _mediator.Publish(new ClearProfileDataMessage());
            _configService.Update(c => c.ProfilesShow = showProfiles);
        }
        ElezenImgui.DrawHelpText("This will show the configured user profile after a set delay");

        ImGui.Indent();
        if (!showProfiles)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Checkbox("Popout profiles on the right", ref profileOnRight))
        {
            _configService.Update(c => c.ProfilePopoutRight = profileOnRight);
            _mediator.Publish(new CompactUiChange(Vector2.Zero, Vector2.Zero));
        }
        ElezenImgui.DrawHelpText("Will show profiles on the right side of the main UI");

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("Hover Delay", ref profileDelay, 1, 10))
        {
            _configService.Update(c => c.ProfileDelay = profileDelay);
        }
        ElezenImgui.DrawHelpText("Delay until the profile should be displayed");

        if (!showProfiles)
        {
            ImGui.EndDisabled();
        }
        ImGui.Unindent();

        if (ImGui.Checkbox("Show profiles marked as NSFW", ref showNsfwProfiles))
        {
            _mediator.Publish(new ClearProfileDataMessage());
            _configService.Update(c => c.ProfilesAllowNsfw = showNsfwProfiles);
        }
        ElezenImgui.DrawHelpText("Will show profiles that have the NSFW tag enabled");

        if (ImGui.Checkbox("Render BBCode images", ref allowBbCodeImages))
        {
            _configService.Update(c => c.AllowBbCodeImages = allowBbCodeImages);
        }
        ElezenImgui.DrawHelpText("Disable this to show [img] tags as text instead of loading external images.");
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Changing value: {SortSyncshellsByVram}")]
    private static partial void LogSortSyncshellsByVramChanged(ILogger logger, bool sortSyncshellsByVram);
}
