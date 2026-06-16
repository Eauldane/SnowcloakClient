using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.Data;
using ElezenTools.Data.Classes;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Services;
using ElezenWorldData = ElezenTools.Data.Classes.WorldData;

namespace Snowcloak.UI.Components;

public sealed class FrostbrandFilterEditor
{
    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly PairingFilterConfigService _filterConfigService;
    private readonly UiFontService _fontService;
    private readonly PairDisplayDecorationService _guiHookService;
    private readonly IReadOnlyList<RaceTribeOptionData> _raceClanOptions;
    private string _homeworldFilterSearch = string.Empty;

    public FrostbrandFilterEditor(SnowcloakConfigService configService,
        PairingFilterConfigService filterConfigService, UiFontService fontService,
        DalamudUtilService dalamudUtilService, PairDisplayDecorationService guiHookService)
    {
        _configService = configService;
        _filterConfigService = filterConfigService;
        _fontService = fontService;
        _dalamudUtilService = dalamudUtilService;
        _guiHookService = guiHookService;
        _raceClanOptions = ElezenData.RaceTribes.GetOptions();
    }

    public void Draw()
    {
        DrawHighlightingSection();
        ImGuiHelpers.ScaledDummy(new Vector2(0, 7));
        FrostbrandPanelChrome.DrawSoftSeparator();
        ImGuiHelpers.ScaledDummy(new Vector2(0, 4));
        DrawFilterColumn();
    }

    private void DrawHighlightingSection()
    {
        FrostbrandPanelChrome.DrawSectionTitle(FontAwesomeIcon.Highlighter, "Highlighting");

        var pairRequestColor = _configService.Current.PairRequestNameColors;
        if (InputDtrColors("Highlight color", ref pairRequestColor))
        {
            _configService.Update(c => c.PairRequestNameColors = pairRequestColor);
            _guiHookService.RequestRedraw();
        }
        ElezenImgui.DrawHelpText("Opted-in Frostbrand users are shown to you in this color while Frostbrand is enabled.");

        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
        ImGui.TextColored(ConvertColorToVec4(pairRequestColor.Foreground), "Opted-in user preview");
    }

    private void DrawFilterColumn()
    {
        FrostbrandPanelChrome.DrawSectionTitle(FontAwesomeIcon.Filter, "Auto-reject filters");
        ImGui.TextWrapped("Snowcloak will automatically filter pair requests from the following characters if they're within inspection range.\n\n Note: If the sender is not within visible range, the request will show as pending and will be checked when they're next in range.");
        ImGuiHelpers.ScaledDummy(new Vector2(0, 2));

        var minimumLevel = _configService.Current.PairRequestMinimumLevel;
        ImGui.TextUnformatted("Reject requests below level");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("##FrostbrandMinLevel", ref minimumLevel))
        {
            minimumLevel = Math.Clamp(minimumLevel, 0, 90);
            _configService.Update(c => c.PairRequestMinimumLevel = minimumLevel);
        }
        ElezenImgui.DrawHelpText("Set to 0 to disable level-based rejection.");
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        var friendsOnly = _configService.Current.PairRequestFriendsOnly;
        if (ImGui.Checkbox("Friends only", ref friendsOnly))
        {
            _configService.Update(c => c.PairRequestFriendsOnly = friendsOnly);
        }
        ElezenImgui.DrawHelpText("Only allow pairing with characters marked as friends in your nameplates.");
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        ImGui.TextWrapped("If you don't want to interact with a certain kind of character regardless of their level, check the appropriate box below. Requests from matching characters will be rejected.");
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        ImGui.TextWrapped("Please note: Snowcloak can only make determinations for this feature based on their unpaired, unglamoured state. For your safety, the client does not attempt to automatically load adventurer plates.");
        foreach (var option in _raceClanOptions)
            DrawRaceFilter(option.Id, option.Name, option.Tribes);

        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));
        DrawHomeworldFilters();
    }

    private void DrawRaceFilter(byte raceId, string raceLabel, IReadOnlyList<TribeOptionData> clans)
    {
        ImGuiHelpers.ScaledDummy(new Vector2(0, 2));
        var raceLabelSize = ImGui.CalcTextSize(raceLabel);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var centeredStart = Math.Max(0, (availableWidth - raceLabelSize.X) / 2);
        var cursorX = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(cursorX + centeredStart);
        ImGui.TextUnformatted(raceLabel);
        ImGui.SetCursorPosX(cursorX);

        if (!ImGui.BeginTable($"FrostbrandRaceRow_{raceId}", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Clan");
        ImGui.TableSetupColumn("Male", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Female", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var clan in clans)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(clan.Name);
            ImGui.TableNextColumn();
            using (ImRaii.PushId($"{raceId}_{clan.Id}_M"))
            {
                var rejectMale = HasAutoRejectCombo(raceId, clan.Id, 0);
                if (ImGui.Checkbox("M", ref rejectMale))
                    UpdateComboRejection(rejectMale, raceId, clan.Id, 0);
            }

            ImGui.TableNextColumn();
            using (ImRaii.PushId($"{raceId}_{clan.Id}_F"))
            {
                var rejectFemale = HasAutoRejectCombo(raceId, clan.Id, 1);
                if (ImGui.Checkbox("F", ref rejectFemale))
                    UpdateComboRejection(rejectFemale, raceId, clan.Id, 1);
            }
        }

        ImGui.EndTable();
    }

    private void DrawHomeworldFilters()
    {
        FrostbrandPanelChrome.DrawSectionTitle(FontAwesomeIcon.GlobeEurope, "Homeworld filters");
        ImGui.TextWrapped("Reject requests from specific homeworlds.");
        ElezenImgui.DrawHelpText("Checked homeworlds are filtered out when Frostbrand pairing is enabled.");

        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##FrostbrandHomeworldSearch", "Search homeworlds...", ref _homeworldFilterSearch, 64);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));

        var rejectedHomeworlds = _filterConfigService.Current.PairRequestRejectedHomeworlds;
        var filteredWorlds = _dalamudUtilService.WorldDetails
            .Where(kvp => string.IsNullOrWhiteSpace(_homeworldFilterSearch)
                          || kvp.Value.Name.Contains(_homeworldFilterSearch, StringComparison.OrdinalIgnoreCase)
                          || kvp.Value.DataCenterName.Contains(_homeworldFilterSearch, StringComparison.OrdinalIgnoreCase))
            .GroupBy(kvp => kvp.Value.DataCenterName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var childMin = ImGui.GetCursorScreenPos();
        var childSize = new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 9);
        var childMax = childMin + new Vector2(ImGui.GetContentRegionAvail().X, childSize.Y);
        ImGui.GetWindowDrawList().AddRectFilled(childMin, childMax, Colour.Vector4ToColour(new Vector4(0.025f, 0.065f, 0.095f, 0.50f)), 0f);
        ImGui.GetWindowDrawList().AddLine(childMin, childMin with { X = childMax.X }, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.32f)), 1f * ImGuiHelpers.GlobalScale);
        using var homeworldBg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.065f, 0.095f, 0.30f));
        using var homeworldList = ImRaii.Child("FrostbrandHomeworldFilters", childSize, false);
        if (homeworldList)
            DrawHomeworldGroups(filteredWorlds, rejectedHomeworlds);

        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
        DrawHomeworldSummary(rejectedHomeworlds);
    }

    private void DrawHomeworldGroups(IEnumerable<IGrouping<string, KeyValuePair<ushort, ElezenWorldData>>> filteredWorlds,
        HashSet<ushort> rejectedHomeworlds)
    {
        foreach (var group in filteredWorlds)
        {
            ImGuiHelpers.ScaledDummy(new Vector2(0, 2));
            _fontService.BigText(group.Key);

            if (!ImGui.BeginTable($"FrostbrandHomeworldGroup_{group.Key}", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg))
                continue;

            foreach (var (worldId, info) in group.OrderBy(kvp => kvp.Value.Name, StringComparer.Ordinal))
            {
                ImGui.TableNextColumn();
                var rejected = rejectedHomeworlds.Contains(worldId);
                if (ImGui.Checkbox($"{info.Name}##FrostbrandHomeworld_{worldId}", ref rejected))
                {
                    _filterConfigService.Update(_ =>
                    {
                        if (rejected)
                            rejectedHomeworlds.Add(worldId);
                        else
                            rejectedHomeworlds.Remove(worldId);
                    });
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawHomeworldSummary(HashSet<ushort> rejectedHomeworlds)
    {
        if (rejectedHomeworlds.Count == 0)
        {
            ImGui.TextDisabled("No homeworld filters configured.");
            return;
        }

        var filteredNames = rejectedHomeworlds
            .Select(id => _dalamudUtilService.WorldData.GetValueOrDefault(id, id.ToString(CultureInfo.InvariantCulture)))
            .OrderBy(name => name, StringComparer.Ordinal);
        ImGui.TextWrapped("Rejecting pair requests from: " + string.Join(", ", filteredNames));
    }

    private bool HasAutoRejectCombo(byte race, byte clan, byte gender)
        => _filterConfigService.Current.AutoRejectCombos.Contains(new AutoRejectCombo(race, clan, gender));

    private void UpdateComboRejection(bool enabled, byte race, byte clan, byte gender)
    {
        var key = new AutoRejectCombo(race, clan, gender);
        _filterConfigService.Update(c =>
        {
            if (enabled)
                c.AutoRejectCombos.Add(key);
            else
                c.AutoRejectCombos.Remove(key);
        });
    }

    private static bool InputDtrColors(string label, ref ElezenStrings.Colour colors)
    {
        using var id = ImRaii.PushId(label);
        var innerSpacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var foregroundColor = ConvertColor(colors.Foreground);
        var glowColor = ConvertColor(colors.Glow);

        var ret = ImGui.ColorEdit3("###foreground", ref foregroundColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Foreground Color - Set to pure black (#000000) to use the default color");

        ImGui.SameLine(0.0f, innerSpacing);
        ret |= ImGui.ColorEdit3("###glow", ref glowColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Glow Color - Set to pure black (#000000) to use the default color");

        ImGui.SameLine(0.0f, innerSpacing);
        ImGui.TextUnformatted(label);

        if (ret)
            colors = new ElezenStrings.Colour(ConvertBackColor(foregroundColor), ConvertBackColor(glowColor));

        return ret;

        static Vector3 ConvertColor(uint color)
            => unchecked(new((byte)color / 255.0f, (byte)(color >> 8) / 255.0f, (byte)(color >> 16) / 255.0f));

        static uint ConvertBackColor(Vector3 color)
            => byte.CreateSaturating(color.X * 255.0f) | ((uint)byte.CreateSaturating(color.Y * 255.0f) << 8) | ((uint)byte.CreateSaturating(color.Z * 255.0f) << 16);
    }

    private static Vector4 ConvertColorToVec4(uint color)
        => new(
            (byte)color / 255.0f,
            (byte)(color >> 8) / 255.0f,
            (byte)(color >> 16) / 255.0f,
            1.0f);
}
