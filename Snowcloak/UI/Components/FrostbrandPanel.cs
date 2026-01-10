using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Localisation;
using Snowcloak.WebAPI;

namespace Snowcloak.UI.Components;

public class FrostbrandPanel
{
    private enum FrostbrandPanelView
    {
        Welcome,
        Pending,
        Settings
    }

    private readonly SnowcloakConfigService _configService;
    private readonly PairRequestService _pairRequestService;
    private readonly UiSharedService _uiShared;
    private readonly GuiHookService _guiHookService;
    private readonly LocalisationService _localisationService;
    private readonly PendingPairRequestSection _pendingPairRequestSection;
    private readonly string _localisationPrefix;
    private readonly (byte RaceId, string RaceLabel, (byte ClanId, string ClanLabel)[] Clans)[] _raceClanOptions;
    private bool _frostbrandEnablePopupModalShown;
    private bool _frostbrandEnablePopupModalJustShown;
    private static readonly Random Rng = new();
    private bool _useUriangerText;
    private string _homeworldFilterSearch = string.Empty;
    private FrostbrandPanelView _activeView = FrostbrandPanelView.Pending;
    private bool _defaultViewInitialised;
    private bool _wasPairingEnabled;

    public FrostbrandPanel(SnowcloakConfigService configService, PairRequestService pairRequestService,
        UiSharedService uiShared, GuiHookService guiHookService, LocalisationService localisationService, 
        PendingPairRequestSection pendingPairRequestSection, string localisationPrefix = "SettingsUi")
    {
        _configService = configService;
        _pairRequestService = pairRequestService;
        _uiShared = uiShared;
        _guiHookService = guiHookService;
        _localisationService = localisationService;
        _pendingPairRequestSection = pendingPairRequestSection;
        _localisationPrefix = localisationPrefix;
        _raceClanOptions = GetRaceClanOptions();
    }

    private (byte RaceId, string RaceLabel, (byte ClanId, string ClanLabel)[] Clans)[] GetRaceClanOptions()
    {
        return
        [
            (1, L("Frostbrand.Races.Hyur", "Hyur"), new (byte, string)[] { (1, L("Frostbrand.Clans.Midlander", "Midlander")), (2, L("Frostbrand.Clans.Highlander", "Highlander")) }),
            (2, L("Frostbrand.Races.Elezen", "Elezen"), new (byte, string)[] { (3, L("Frostbrand.Clans.Wildwood", "Wildwood")), (4, L("Frostbrand.Clans.Duskwight", "Duskwight")) }),
            (3, L("Frostbrand.Races.Lalafell", "Lalafell"), new (byte, string)[] { (5, L("Frostbrand.Clans.Plainsfolk", "Plainsfolk")), (6, L("Frostbrand.Clans.Dunesfolk", "Dunesfolk")) }),
            (4, L("Frostbrand.Races.Miqote", "Miqo'te"), new (byte, string)[] { (7, L("Frostbrand.Clans.SeekerOfTheSun", "Seeker of the Sun")), (8, L("Frostbrand.Clans.KeeperOfTheMoon", "Keeper of the Moon")) }),
            (5, L("Frostbrand.Races.Roegadyn", "Roegadyn"), new (byte, string)[] { (9, L("Frostbrand.Clans.SeaWolf", "Sea Wolf")), (10, L("Frostbrand.Clans.Hellsguard", "Hellsguard")) }),
            (6, L("Frostbrand.Races.AuRa", "Au Ra"), new (byte, string)[] { (11, L("Frostbrand.Clans.Raen", "Raen")), (12, L("Frostbrand.Clans.Xaela", "Xaela")) }),
            (7, L("Frostbrand.Races.Hrothgar", "Hrothgar"), new (byte, string)[] { (13, L("Frostbrand.Clans.Helions", "Helions")), (14, L("Frostbrand.Clans.TheLost", "The Lost")) }),
            (8, L("Frostbrand.Races.Viera", "Viera"), new (byte, string)[] { (15, L("Frostbrand.Clans.Rava", "Rava")), (16, L("Frostbrand.Clans.Veena", "Veena")) }),
        ];
    }

    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"{_localisationPrefix}.{key}", fallback);
    }

    private void SetPairingSystemEnabled(bool enabled)
    {
        _configService.Current.PairingSystemEnabled = enabled;
        _configService.Save();

        _ = _pairRequestService.SyncAdvertisingAsync();

        _guiHookService.RequestRedraw();
    }

    public void Draw()
    {
        _uiShared.BigText(L("Frostbrand.Header", "Frostbrand Pairing"));

        var pairingEnabled = _configService.Current.PairingSystemEnabled;
        var requestedPairingEnabled = pairingEnabled;
        if (ImGui.Checkbox(L("Frostbrand.Enable", "Enable Frostbrand pairing features"), ref requestedPairingEnabled))
        {
            if (requestedPairingEnabled && !pairingEnabled)
            {
                _frostbrandEnablePopupModalJustShown = true;
                _frostbrandEnablePopupModalShown = true;
                ImGui.OpenPopup(L("Frostbrand.Enable.PopupTitle", "Enable Frostbrand pairing?"));
            }
            else
            {
                SetPairingSystemEnabled(requestedPairingEnabled);
            }
        }
        _uiShared.DrawHelpText(L("Frostbrand.Enable.Help", "Disable to hide pairing highlights, suppress right-click pairing actions, and pause auto-rejection."));

        if (pairingEnabled != _wasPairingEnabled)
        {
            _defaultViewInitialised = false;
            _wasPairingEnabled = pairingEnabled;
        }

        if (!_defaultViewInitialised)
        {
            _activeView = DetermineDefaultView(pairingEnabled);
            _defaultViewInitialised = true;
        }

        if (ImGui.BeginPopupModal(L("Frostbrand.Enable.PopupTitle", "Enable Frostbrand pairing?"), ref _frostbrandEnablePopupModalShown, UiSharedService.PopupWindowFlags))
        {
            if (_frostbrandEnablePopupModalJustShown)
            {
                _useUriangerText = Rng.Next(99) == 0;
                _frostbrandEnablePopupModalJustShown = false;
            }

            if (!_useUriangerText)
            {
                UiSharedService.TextWrapped(
                    L("Frostbrand.Enable.Popup.Body1", "Frostbrand is a system that, when opted-in to, shows other nearby users who've opted in that you're open to pairing."));
                UiSharedService.TextWrapped(
                    L("Frostbrand.Enable.Popup.Body2", "Whilst Snowcloak provides filters to automatically reject those you're not interested in pairing with, please be aware that " +
                                                       "while you have it enabled, anyone using Frostbrand will be able to see that you're using Snowcloak."));
                UiSharedService.TextWrapped(
                    L("Frostbrand.Enable.Popup.Body3", "Please take the time to understand the privacy risk this introduces, and if you choose to enable the system, " +
                                                       "you're advised to configure filters immediately, preferably in a quiet area."));
                UiSharedService.TextWrapped(L("Frostbrand.Enable.Popup.Continue", "Continue?"));
                ImGui.Separator();
                ImGui.Spacing();

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                  ImGui.GetStyle().ItemSpacing.X) / 2;

                if (ImGui.Button(L("Frostbrand.Enable.Popup.Confirm", "Confirm"), new Vector2(buttonSize, 0)))
                {
                    SetPairingSystemEnabled(true);
                    _frostbrandEnablePopupModalShown = false;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();

                if (ImGui.Button(L("Frostbrand.Enable.Popup.Cancel", "Cancel##cancelFrostbrandEnable"), new Vector2(buttonSize, 0)))
                {
                    _frostbrandEnablePopupModalShown = false;
                    ImGui.CloseCurrentPopup();
                }
            }
            else
            {
                UiSharedService.TextWrapped(
                    L("Frostbrand.Enable.Popup.Urianger1", "Frostbrand be a covenant of mutual accord, whereby those who do willingly partake therein may perceive, " +
                                                           "among the souls nearby, others likewise disposed unto pairing."));
                UiSharedService.TextWrapped(
                    L("Frostbrand.Enable.Popup.Urianger2", "Know this also: though Snowcloak doth grant thee wards and strictures, by which thou mayest " +
                                                           "deny communion with those thou wouldst not suffer, yet whilst Frostbrand remaineth enabled," +
                                                           " any who wield its sight shall discern that thou makest use of Snowcloak."));
                UiSharedService.TextWrapped(
                    L("Frostbrand.Enable.Popup.Urianger3", "Ponder well, then, the peril to thine own privacy that this revelation entailest. Shouldst " +
                                                           "thou resolve to walk this path regardless, thou art strongly counseled to set thy filters " +
                                                           "with all haste - best done where few eyes linger and fewer ears attend."));
                UiSharedService.TextWrapped(L("Frostbrand.Enable.Popup.Urianger4", "Wilt thou press on?"));
                ImGui.Separator();
                ImGui.Spacing();

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                  ImGui.GetStyle().ItemSpacing.X) / 2;

                if (ImGui.Button(L("Frostbrand.Enable.Popup.UriangerConfirm", "Thus do I assent"), new Vector2(buttonSize, 0)))
                {
                    SetPairingSystemEnabled(true);
                    _frostbrandEnablePopupModalShown = false;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();

                if (ImGui.Button(L("Frostbrand.Enable.Popup.UriangerCancel", "I shall refrain##cancelFrostbrandEnable"), new Vector2(buttonSize, 0)))
                {
                    _frostbrandEnablePopupModalShown = false;
                    ImGui.CloseCurrentPopup();
                }
            }

            UiSharedService.SetScaledWindowSize(500);
            ImGui.EndPopup();
        }

        using (ImRaii.Disabled(!pairingEnabled))
        {
            DrawNavigationRow();

            ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

            switch (_activeView)
            {
                case FrostbrandPanelView.Welcome:
                    DrawWelcomeSection();
                    break;
                case FrostbrandPanelView.Pending:
                    DrawPendingTab();
                    break;
                case FrostbrandPanelView.Settings:
                    DrawSettingsTab();
                    break;
            }
        }
    }


    private FrostbrandPanelView DetermineDefaultView(bool pairingEnabled)
    {
        if (pairingEnabled && !_configService.Current.FrostbrandWelcomeSeen)
        {
            _configService.Current.FrostbrandWelcomeSeen = true;
            _configService.Save();
            return FrostbrandPanelView.Welcome;
        }

        return FrostbrandPanelView.Pending;
    }

    private void DrawNavigationRow()
    {
        var pendingCount = _pendingPairRequestSection.PendingCount;
        var buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        var pendingLabel = pendingCount > 0
            ? $"{L("Frostbrand.Layout.PendingTab", "Pending requests")} ({pendingCount})"
            : L("Frostbrand.Layout.PendingTab", "Pending requests");

        DrawTabButton(FrostbrandPanelView.Pending, pendingLabel, buttonWidth);
        ImGui.SameLine();
        DrawTabButton(FrostbrandPanelView.Settings, L("Frostbrand.Layout.SettingsTab", "Frostbrand settings"), buttonWidth);
    }

    private void DrawTabButton(FrostbrandPanelView view, string label, float width)
    {
        var isActive = _activeView == view;
        using var buttonColor = ImRaii.PushColor(ImGuiCol.Button, isActive ? UiSharedService.AccentColor : ImGui.GetStyle().Colors[(int)ImGuiCol.Button]);
        using var hoverColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, isActive ? UiSharedService.AccentColor : ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered]);
        using var activeColor = ImRaii.PushColor(ImGuiCol.ButtonActive, isActive ? UiSharedService.AccentColor : ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);

        if (ImGui.Button(label, new Vector2(width, 0)))
        {
            _activeView = view;
        }
    }

    private void DrawWelcomeSection()
    {
        _uiShared.BigText(L("Frostbrand.Welcome.Header", "Welcome to Frostbrand"));
        ImGui.TextWrapped(L("Frostbrand.Welcome.Body1", "You've opted into Frostbrand. Pending pair requests will be listed here, and you can manage filters from the Frostbrand settings tab."));
        ImGuiHelpers.ScaledDummy(new Vector2(0, 4));
        ImGui.TextWrapped(L("Frostbrand.Welcome.Body2", "Remember: other opted-in Frostbrand users can see that you're using Snowcloak while Frostbrand is enabled. Configure your filters to stay in control."));
    }

    private void DrawPendingTab()
    {
        _uiShared.BigText(L("Frostbrand.Pending.Header", "Pending pair requests"));
        if (_pendingPairRequestSection.PendingCount == 0)
        {
            ImGui.TextDisabled(L("Frostbrand.Pending.Empty", "No pending pair requests right now."));
            return;
        }

        _pendingPairRequestSection.Draw(tagHandler: null, localisationPrefix: _localisationPrefix, indent: false);
    }

    private void DrawSettingsTab()
    {
        DrawHighlightingSection();

        ImGuiHelpers.ScaledDummy(new Vector2(0, 10));
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        DrawFilterColumn();
    }

    private void DrawHighlightingSection()
    {
        _uiShared.BigText(L("Frostbrand.Highlighting.Header", "Highlighting"));

        var pairRequestColor = _configService.Current.PairRequestNameColors;
        if (InputDtrColors(L("Frostbrand.Highlighting.Color", "Highlight color"), ref pairRequestColor))
        {
            _configService.Current.PairRequestNameColors = pairRequestColor;
            _configService.Save();
            _guiHookService.RequestRedraw();
        }
        _uiShared.DrawHelpText(L("Frostbrand.Highlighting.Color.Help", "Opted-in Frostbrand users are shown to you in this color while Frostbrand is enabled."));

        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
        ImGui.TextColored(ConvertColorToVec4(pairRequestColor.Foreground), L("Frostbrand.Highlighting.Preview", "Opted-in user preview"));
    }

    private void DrawFilterColumn()
    {
        _uiShared.BigText(L("Frostbrand.Filters.Header", "Auto-reject filters"));
        ImGui.TextWrapped(L("Frostbrand.Filters.Description", "Snowcloak will automatically filter pair requests from the following characters if they're within"
                                                              + " inspection range.\n\n Note: If the sender is not within visible range, the request will show as pending and will"
                                                              + " be checked when they're next in range."));
        ImGuiHelpers.ScaledDummy(new Vector2(0, 2));

        var minimumLevel = _configService.Current.PairRequestMinimumLevel;
        ImGui.TextUnformatted(L("Frostbrand.Filters.MinimumLevel", "Reject requests below level"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("##FrostbrandMinLevel", ref minimumLevel))
        {
            minimumLevel = Math.Clamp(minimumLevel, 0, 90);
            _configService.Current.PairRequestMinimumLevel = minimumLevel;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Frostbrand.Filters.MinimumLevel.Help", "Set to 0 to disable level-based rejection."));
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        var friendsOnly = _configService.Current.PairRequestFriendsOnly;
        if (ImGui.Checkbox(L("Frostbrand.Filters.FriendsOnly", "Friends only"), ref friendsOnly))
        {
            _configService.Current.PairRequestFriendsOnly = friendsOnly;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Frostbrand.Filters.FriendsOnly.Help", "Only allow pairing with characters marked as friends in your nameplates."));
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        ImGui.TextWrapped(L("Frostbrand.Filters.ClanDescription", "If you don't want to interact with a certain kind of character regardless of their level, check the appropriate box"
                                                                  + " below. Requests from matching characters will be rejected."));
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        ImGui.TextWrapped(L("Frostbrand.Filters.PlateNotice", "Please note: Snowcloak can only make determinations for this feature based on their unpaired, unglamoured state. For your safety,"
                                                              + " the client does not attempt to automatically load adventurer plates."));
        foreach (var (raceId, raceLabel, clans) in _raceClanOptions)
        {
            ImGuiHelpers.ScaledDummy(new Vector2(0, 2));
            var raceLabelSize = ImGui.CalcTextSize(raceLabel);
            var availableWidth = ImGui.GetContentRegionAvail().X;
            var centeredStart = Math.Max(0, (availableWidth - raceLabelSize.X) / 2);
            var cursorX = ImGui.GetCursorPosX();
            ImGui.SetCursorPosX(cursorX + centeredStart);
            ImGui.TextUnformatted(raceLabel);
            ImGui.SetCursorPosX(cursorX);

            if (ImGui.BeginTable($"FrostbrandRaceRow_{raceId}", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn(L("Frostbrand.Filters.Clan", "Clan"));
                ImGui.TableSetupColumn(L("Frostbrand.Filters.Male", "Male"), ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn(L("Frostbrand.Filters.Female", "Female"), ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();

                foreach (var (clanId, clanLabel) in clans)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(clanLabel);
                    ImGui.TableNextColumn();
                    using (ImRaii.PushId($"{raceId}_{clanId}_M"))
                    {
                        var rejectMale = HasAutoRejectCombo(raceId, clanId, 0);
                        if (ImGui.Checkbox("M", ref rejectMale))
                        {
                            UpdateComboRejection(rejectMale, raceId, clanId, 0);
                        }
                    }

                    ImGui.TableNextColumn();
                    using (ImRaii.PushId($"{raceId}_{clanId}_F"))
                    {
                        var rejectFemale = HasAutoRejectCombo(raceId, clanId, 1);
                        if (ImGui.Checkbox("F", ref rejectFemale))
                        {
                            UpdateComboRejection(rejectFemale, raceId, clanId, 1);
                        }
                    }
                }

                ImGui.EndTable();
            }
        }
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        _uiShared.BigText(L("Frostbrand.HomeworldFilters.Header", "Homeworld filters"));
        ImGui.TextWrapped(L("Frostbrand.HomeworldFilters.Description", "Reject requests from specific homeworlds."));
        _uiShared.DrawHelpText(L("Frostbrand.HomeworldFilters.Help", "Checked homeworlds are filtered out when Frostbrand pairing is enabled."));

        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##FrostbrandHomeworldSearch", L("Frostbrand.HomeworldFilters.Search", "Search homeworlds..."), ref _homeworldFilterSearch, 64);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));

        var rejectedHomeworlds = _configService.Current.PairRequestRejectedHomeworlds;
        var filteredWorlds = _uiShared.WorldInfoData
            .Where(kvp => string.IsNullOrWhiteSpace(_homeworldFilterSearch)
                          || kvp.Value.Name.Contains(_homeworldFilterSearch, StringComparison.OrdinalIgnoreCase)
                          || kvp.Value.DataCenter.Contains(_homeworldFilterSearch, StringComparison.OrdinalIgnoreCase))
            .GroupBy(kvp => kvp.Value.DataCenter)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        using var homeworldList = ImRaii.Child("FrostbrandHomeworldFilters", new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 9), true);
        if (homeworldList)
        {
            foreach (var group in filteredWorlds)
            {
                ImGuiHelpers.ScaledDummy(new Vector2(0, 2));
                _uiShared.BigText(group.Key);

                if (ImGui.BeginTable($"FrostbrandHomeworldGroup_{group.Key}", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg))
                {
                    foreach (var (worldId, info) in group.OrderBy(kvp => kvp.Value.Name, StringComparer.Ordinal))
                    {
                        ImGui.TableNextColumn();
                        var rejected = rejectedHomeworlds.Contains(worldId);
                        if (ImGui.Checkbox($"{info.Name}##FrostbrandHomeworld_{worldId}", ref rejected))
                        {
                            if (rejected)
                                rejectedHomeworlds.Add(worldId);
                            else
                                rejectedHomeworlds.Remove(worldId);

                            _configService.Save();
                        }
                    }

                    ImGui.EndTable();
                }
            }
        }

        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));

        if (rejectedHomeworlds.Count == 0)
        {
            ImGui.TextDisabled(L("Frostbrand.HomeworldFilters.Empty", "No homeworld filters configured."));
        }
        else
        {
            var filteredNames = rejectedHomeworlds
                .Select(id => _uiShared.WorldData.GetValueOrDefault(id, id.ToString()))
                .OrderBy(name => name, StringComparer.Ordinal);
            ImGui.TextWrapped(L("Frostbrand.HomeworldFilters.Active", "Rejecting pair requests from: ") + string.Join(", ", filteredNames));
        }
    }
    private bool InputDtrColors(string label, ref DtrEntry.Colors colors)
    {
        using var id = ImRaii.PushId(label);
        var innerSpacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var foregroundColor = ConvertColor(colors.Foreground);
        var glowColor = ConvertColor(colors.Glow);

        var ret = ImGui.ColorEdit3("###foreground", ref foregroundColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("General.Ui.ColorTooltip.Foreground", "Foreground Color - Set to pure black (#000000) to use the default color"));

        ImGui.SameLine(0.0f, innerSpacing);
        ret |= ImGui.ColorEdit3("###glow", ref glowColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("General.Ui.ColorTooltip.Glow", "Glow Color - Set to pure black (#000000) to use the default color"));

        ImGui.SameLine(0.0f, innerSpacing);
        ImGui.TextUnformatted(label);

        if (ret)
            colors = new(ConvertBackColor(foregroundColor), ConvertBackColor(glowColor));

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

    private bool HasAutoRejectCombo(byte race, byte clan, byte gender)
        => _configService.Current.AutoRejectCombos.Contains(new AutoRejectCombo(race, clan, gender));

    private void UpdateComboRejection(bool enabled, byte race, byte clan, byte gender)
    {
        var key = new AutoRejectCombo(race, clan, gender);
        if (enabled)
            _configService.Current.AutoRejectCombos.Add(key);
        else
            _configService.Current.AutoRejectCombos.Remove(key);

        _configService.Save();
    }
}
