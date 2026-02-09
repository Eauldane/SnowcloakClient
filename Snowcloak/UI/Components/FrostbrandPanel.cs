using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
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
        UiSharedService uiShared, GuiHookService guiHookService, 
        PendingPairRequestSection pendingPairRequestSection, string localisationPrefix = "SettingsUi")
    {
        _configService = configService;
        _pairRequestService = pairRequestService;
        _uiShared = uiShared;
        _guiHookService = guiHookService;
        _pendingPairRequestSection = pendingPairRequestSection;
        _localisationPrefix = localisationPrefix;
        _raceClanOptions = GetRaceClanOptions();
    }

    private (byte RaceId, string RaceLabel, (byte ClanId, string ClanLabel)[] Clans)[] GetRaceClanOptions()
    {
        return
        [
            (1, "Hyur", new (byte, string)[] { (1, "Midlander"), (2, "Highlander") }),
            (2, "Elezen", new (byte, string)[] { (3, "Wildwood"), (4, "Duskwight") }),
            (3, "Lalafell", new (byte, string)[] { (5, "Plainsfolk"), (6, "Dunesfolk") }),
            (4, "Miqo'te", new (byte, string)[] { (7, "Seeker of the Sun"), (8, "Keeper of the Moon") }),
            (5, "Roegadyn", new (byte, string)[] { (9, "Sea Wolf"), (10, "Hellsguard") }),
            (6, "Au Ra", new (byte, string)[] { (11, "Raen"), (12, "Xaela") }),
            (7, "Hrothgar", new (byte, string)[] { (13, "Helions"), (14, "The Lost") }),
            (8, "Viera", new (byte, string)[] { (15, "Rava"), (16, "Veena") }),
        ];
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
        _uiShared.BigText("Frostbrand Pairing");

        var pairingEnabled = _configService.Current.PairingSystemEnabled;
        var requestedPairingEnabled = pairingEnabled;
        if (ImGui.Checkbox("Enable Frostbrand pairing features", ref requestedPairingEnabled))
        {
            if (requestedPairingEnabled && !pairingEnabled)
            {
                _frostbrandEnablePopupModalJustShown = true;
                _frostbrandEnablePopupModalShown = true;
                ImGui.OpenPopup("Enable Frostbrand pairing?");
            }
            else
            {
                SetPairingSystemEnabled(requestedPairingEnabled);
            }
        }
        _uiShared.DrawHelpText("Disable to hide pairing highlights, suppress right-click pairing actions, and pause auto-rejection.");

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

        if (ImGui.BeginPopupModal("Enable Frostbrand pairing?", ref _frostbrandEnablePopupModalShown, UiSharedService.PopupWindowFlags))
        {
            if (_frostbrandEnablePopupModalJustShown)
            {
                _useUriangerText = Rng.Next(99) == 0;
                _frostbrandEnablePopupModalJustShown = false;
            }

            if (!_useUriangerText)
            {
                ElezenImgui.WrappedText(
                    "Frostbrand is a system that, when opted-in to, shows other nearby users who've opted in that you're open to pairing.");
                ElezenImgui.WrappedText(
                    "Whilst Snowcloak provides filters to automatically reject those you're not interested in pairing with, please be aware that " +
                                                       "while you have it enabled, anyone using Frostbrand will be able to see that you're using Snowcloak.");
                ElezenImgui.WrappedText(
                    "Please take the time to understand the privacy risk this introduces, and if you choose to enable the system, " +
                                                       "you're advised to configure filters immediately, preferably in a quiet area.");
                ElezenImgui.WrappedText("Continue?");
                ImGui.Separator();
                ImGui.Spacing();

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                  ImGui.GetStyle().ItemSpacing.X) / 2;

                if (ImGui.Button("Confirm", new Vector2(buttonSize, 0)))
                {
                    SetPairingSystemEnabled(true);
                    _frostbrandEnablePopupModalShown = false;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();

                if (ImGui.Button("Cancel##cancelFrostbrandEnable", new Vector2(buttonSize, 0)))
                {
                    _frostbrandEnablePopupModalShown = false;
                    ImGui.CloseCurrentPopup();
                }
            }
            else
            {
                ElezenImgui.WrappedText(
                    "Frostbrand be a covenant of mutual accord, whereby those who do willingly partake therein may perceive, " +
                                                           "among the souls nearby, others likewise disposed unto pairing.");
                ElezenImgui.WrappedText(
                    "Know this also: though Snowcloak doth grant thee wards and strictures, by which thou mayest " +
                                                           "deny communion with those thou wouldst not suffer, yet whilst Frostbrand remaineth enabled," +
                                                           " any who wield its sight shall discern that thou makest use of Snowcloak.");
                ElezenImgui.WrappedText(
                    "Ponder well, then, the peril to thine own privacy that this revelation entailest. Shouldst " +
                                                           "thou resolve to walk this path regardless, thou art strongly counseled to set thy filters " +
                                                           "with all haste - best done where few eyes linger and fewer ears attend.");
                ElezenImgui.WrappedText("Wilt thou press on?");
                ImGui.Separator();
                ImGui.Spacing();

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                  ImGui.GetStyle().ItemSpacing.X) / 2;

                if (ImGui.Button("Thus do I assent", new Vector2(buttonSize, 0)))
                {
                    SetPairingSystemEnabled(true);
                    _frostbrandEnablePopupModalShown = false;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();

                if (ImGui.Button("I shall refrain##cancelFrostbrandEnable", new Vector2(buttonSize, 0)))
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
            ? $"{"Pending requests"} ({pendingCount})"
            : "Pending requests";

        DrawTabButton(FrostbrandPanelView.Pending, pendingLabel, buttonWidth);
        ImGui.SameLine();
        DrawTabButton(FrostbrandPanelView.Settings, "Frostbrand settings", buttonWidth);
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
        _uiShared.BigText("Welcome to Frostbrand");
        ImGui.TextWrapped("You've opted into Frostbrand. Pending pair requests will be listed here, and you can manage filters from the Frostbrand settings tab.");
        ImGuiHelpers.ScaledDummy(new Vector2(0, 4));
        ImGui.TextWrapped("Remember: other opted-in Frostbrand users can see that you're using Snowcloak while Frostbrand is enabled. Configure your filters to stay in control.");
    }

    private void DrawPendingTab()
    {
        _uiShared.BigText("Pending pair requests");
        if (_pendingPairRequestSection.PendingCount == 0)
        {
            ImGuiHelpers.ScaledDummy(new Vector2(0, 6));
            ImGui.BeginGroup();
            ElezenImgui.ShowIcon(FontAwesomeIcon.UserPlus, ImGui.GetColorU32(ImGuiCol.TextDisabled));
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextUnformatted("No pending pair requests right now.");
            ImGui.TextWrapped("Pair requests appear here when a nearby Frostbrand user sends you a request while you're opted in.");
            ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, "Need to fine-tune who can send a request? Head to the settings tab and set some filters.");
            ImGui.EndGroup();
            ImGui.EndGroup();
            ImGuiHelpers.ScaledDummy(new Vector2(0, 4));
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Cog, "Open Frostbrand settings"))
            {
                _activeView = FrostbrandPanelView.Settings;
            }            return;
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
        _uiShared.BigText("Highlighting");

        var pairRequestColor = _configService.Current.PairRequestNameColors;
        if (InputDtrColors("Highlight color", ref pairRequestColor))
        {
            _configService.Current.PairRequestNameColors = pairRequestColor;
            _configService.Save();
            _guiHookService.RequestRedraw();
        }
        _uiShared.DrawHelpText("Opted-in Frostbrand users are shown to you in this color while Frostbrand is enabled.");

        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
        ImGui.TextColored(ConvertColorToVec4(pairRequestColor.Foreground), "Opted-in user preview");
    }

    private void DrawFilterColumn()
    {
        _uiShared.BigText("Auto-reject filters");
        ImGui.TextWrapped("Snowcloak will automatically filter pair requests from the following characters if they're within"
                                                              + " inspection range.\n\n Note: If the sender is not within visible range, the request will show as pending and will"
                                                              + " be checked when they're next in range.");
        ImGuiHelpers.ScaledDummy(new Vector2(0, 2));

        var minimumLevel = _configService.Current.PairRequestMinimumLevel;
        ImGui.TextUnformatted("Reject requests below level");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("##FrostbrandMinLevel", ref minimumLevel))
        {
            minimumLevel = Math.Clamp(minimumLevel, 0, 90);
            _configService.Current.PairRequestMinimumLevel = minimumLevel;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Set to 0 to disable level-based rejection.");
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        var friendsOnly = _configService.Current.PairRequestFriendsOnly;
        if (ImGui.Checkbox("Friends only", ref friendsOnly))
        {
            _configService.Current.PairRequestFriendsOnly = friendsOnly;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Only allow pairing with characters marked as friends in your nameplates.");
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        ImGui.TextWrapped("If you don't want to interact with a certain kind of character regardless of their level, check the appropriate box"
                                                                  + " below. Requests from matching characters will be rejected.");
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        ImGui.TextWrapped("Please note: Snowcloak can only make determinations for this feature based on their unpaired, unglamoured state. For your safety,"
                                                              + " the client does not attempt to automatically load adventurer plates.");
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
                ImGui.TableSetupColumn("Clan");
                ImGui.TableSetupColumn("Male", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Female", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
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

        _uiShared.BigText("Homeworld filters");
        ImGui.TextWrapped("Reject requests from specific homeworlds.");
        _uiShared.DrawHelpText("Checked homeworlds are filtered out when Frostbrand pairing is enabled.");

        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##FrostbrandHomeworldSearch", "Search homeworlds...", ref _homeworldFilterSearch, 64);
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
            ImGui.TextDisabled("No homeworld filters configured.");
        }
        else
        {
            var filteredNames = rejectedHomeworlds
                .Select(id => _uiShared.WorldData.GetValueOrDefault(id, id.ToString()))
                .OrderBy(name => name, StringComparer.Ordinal);
            ImGui.TextWrapped("Rejecting pair requests from: " + string.Join(", ", filteredNames));
        }
    }
    private bool InputDtrColors(string label, ref ElezenStrings.Colour colors)
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
