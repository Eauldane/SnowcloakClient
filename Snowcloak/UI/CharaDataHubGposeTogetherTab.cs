using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.WebAPI;

namespace Snowcloak.UI;

internal sealed class CharaDataHubGposeTogetherTab
{
    private readonly CharaDataHubContext _ctx;
    private readonly CharaDataConfigService _configService;
    private readonly CharaDataManager _charaDataManager;
    private readonly GposeLobbySession _session;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly NotesStore _notesStore;
    private readonly ApiController _apiController;
    private string _joinLobbyId = string.Empty;

    public CharaDataHubGposeTogetherTab(CharaDataHubContext ctx, CharaDataConfigService configService,
        CharaDataManager charaDataManager, GposeLobbySession session,
        DalamudUtilService dalamudUtilService, NotesStore notesStore,
        ApiController apiController)
    {
        _ctx = ctx;
        _configService = configService;
        _charaDataManager = charaDataManager;
        _session = session;
        _dalamudUtilService = dalamudUtilService;
        _notesStore = notesStore;
        _apiController = apiController;
    }

    public void Draw()
    {
        if (!_charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(5);
            ElezenImgui.DrawGroupedCenteredColorText("BRIO IS MANDATORY FOR GPOSE TOGETHER.", ImGuiColors.DalamudRed);
            ImGuiHelpers.ScaledDummy(5);
        }

        if (!_apiController.IsConnected)
        {
            ImGuiHelpers.ScaledDummy(5);
            ElezenImgui.DrawGroupedCenteredColorText("CANNOT USE GPOSE TOGETHER WHILE DISCONNECTED FROM THE SERVER.", ImGuiColors.DalamudRed);
            ImGuiHelpers.ScaledDummy(5);
        }

        ModernSection.Header(FontAwesomeIcon.Users, "GPose Together");
        CharaDataHubWidgets.DrawHelpFoldout(_configService, "GPose together is a way to do multiplayer GPose sessions and collaborations." + (Environment.NewLine + Environment.NewLine)
            + "GPose together requires Brio to function. Only Brio is also supported for the actual posing interactions. Attempting to pose using other tools will lead to conflicts and exploding characters." + (Environment.NewLine + Environment.NewLine)
            + "To use GPose together you either create or join a GPose Together Lobby. After you and other people have joined, make sure that everyone is on the same map. "
            + "It is not required for you to be on the same server, DC or instance. Users that are on the same map will be drawn as moving purple wisps in the overworld, so you can easily find each other." + (Environment.NewLine + Environment.NewLine)
            + "Once you are close to each other you can initiate GPose. You must either assign or spawn characters for each of the lobby users. Their own poses and positions to their character will be automatically applied." + Environment.NewLine
            + "Pose and location data during GPose are updated approximately every few seconds.");

        using var disabled = ImRaii.Disabled(!_charaDataManager.BrioAvailable || !_apiController.IsConnected);

        SnowcloakUi.DistanceSeparator();
        ModernSection.Header(FontAwesomeIcon.DoorOpen, "Lobby Controls");
        if (string.IsNullOrEmpty(_session.CurrentGPoseLobbyId))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Create New GPose Together Lobby"))
            {
                _session.CreateNewLobby();
            }
            ImGuiHelpers.ScaledDummy(5);
            ImGui.SetNextItemWidth(250);
            ImGui.InputTextWithHint("##lobbyId", "GPose Lobby ID", ref _joinLobbyId, 30);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowRight, "Join GPose Together Lobby"))
            {
                _session.JoinGPoseLobby(_joinLobbyId);
                _joinLobbyId = string.Empty;
            }
            if (!string.IsNullOrEmpty(_session.LastGPoseLobbyId)
                && ElezenImgui.ShowIconButton(FontAwesomeIcon.LongArrowAltRight, string.Format("Rejoin Last Lobby {0}", _session.LastGPoseLobbyId)))
            {
                _session.JoinGPoseLobby(_session.LastGPoseLobbyId);
            }
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("GPose Lobby");
            ImGui.SameLine();
            ElezenImgui.ColouredWrappedText(_session.CurrentGPoseLobbyId, ImGuiColors.ParsedGreen);
            ImGui.SameLine();
            if (ElezenImgui.IconButton(FontAwesomeIcon.Clipboard))
            {
                ImGui.SetClipboardText(_session.CurrentGPoseLobbyId);
            }
            ElezenImgui.AttachTooltip("Copy Lobby ID to clipboard.");
            using (ImRaii.Disabled(!ElezenImgui.CtrlPressed()))
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowLeft, "Leave GPose Lobby"))
                {
                    _session.LeaveGPoseLobby();
                }
            }
            ElezenImgui.AttachTooltip("Leave the current GPose lobby." + ElezenImgui.TooltipSeparator + "Hold CTRL and click to leave.");
        }
        SnowcloakUi.DistanceSeparator();
        using (ImRaii.Disabled(string.IsNullOrEmpty(_session.CurrentGPoseLobbyId)))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowUp, "Send Updated Character Data"))
            {
                _ = _session.PushCharacterDownloadDto();
            }
            ElezenImgui.AttachTooltip("This will send your current appearance, pose and world data to all users in the lobby.");
            if (!_dalamudUtilService.IsInGpose)
            {
                ImGuiHelpers.ScaledDummy(5);
                ElezenImgui.DrawGroupedCenteredColorText("Assigning users to characters is only available in GPose.", ImGuiColors.DalamudYellow, 300);
            }
            SnowcloakUi.DistanceSeparator();
            ImGui.TextUnformatted("Users In Lobby");
            var gposeCharas = _dalamudUtilService.GetGposeCharactersFromObjectTable();
            var self = _dalamudUtilService.GetPlayerCharacter();
            gposeCharas = gposeCharas.Where(c => c != null && !string.Equals(c.Name.TextValue, self.Name.TextValue, StringComparison.Ordinal)).ToList();

            using (ImRaii.Child("charaChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGuiHelpers.ScaledDummy(3);

                if (!_session.UsersInLobby.Any() && !string.IsNullOrEmpty(_session.CurrentGPoseLobbyId))
                {
                    ElezenImgui.DrawGroupedCenteredColorText("No other users in current GPose lobby", ImGuiColors.DalamudYellow);
                }
                else
                {
                    foreach (var user in _session.UsersInLobby)
                    {
                        DrawLobbyUser(user, gposeCharas);
                    }
                }
            }
        }
    }

    private void DrawLobbyUser(GposeLobbyUserData user,
        IEnumerable<Dalamud.Game.ClientState.Objects.Types.ICharacter?> gposeCharas)
    {
        using var id = ImRaii.PushId(user.UserData.UID);
        using var indent = ImRaii.PushIndent(5f);
        var sameMapAndServer = _session.IsOnSameMapAndServer(user);
        var width = ImGui.GetContentRegionAvail().X - 5;
        ElezenImgui.DrawGrouped(() =>
        {
            var availWidth = ImGui.GetContentRegionAvail().X;
            ImGui.AlignTextToFramePadding();
            var note = _notesStore.GetNoteForUid(user.UserData.UID);
            var userText = note == null ? user.UserData.AliasOrUID : $"{note} ({user.UserData.AliasOrUID})";
            ElezenImgui.ColouredText(userText, ImGuiColors.ParsedGreen);

            var buttonsize = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.ArrowRight).X;
            var buttonsize2 = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Plus).X;
            ImGui.SameLine();
            ImGui.SetCursorPosX(availWidth - (buttonsize + buttonsize2 + ImGui.GetStyle().ItemSpacing.X));
            using (ImRaii.Disabled(!_dalamudUtilService.IsInGpose || user.CharaData == null || user.Address == nint.Zero
                || user.LastUpdatedCharaData <= user.LastAppliedCharaDataDate))
            {
                if (ElezenImgui.IconButton(FontAwesomeIcon.ArrowRight))
                {
                    _ = _session.ApplyCharaData(user);
                }
            }
            ElezenImgui.AttachTooltip("Apply newly received character data to selected actor." + ElezenImgui.TooltipSeparator + "Note: If the button is grayed out, the latest data has already been applied.");
            ImGui.SameLine();
            using (ImRaii.Disabled(!_dalamudUtilService.IsInGpose || user.CharaData == null || sameMapAndServer.SameEverything))
            {
                if (ElezenImgui.IconButton(FontAwesomeIcon.Plus))
                {
                    _ = _session.SpawnAndApplyData(user);
                }
            }
            ElezenImgui.AttachTooltip("Spawn new actor, apply character data and and assign it to this user." + ElezenImgui.TooltipSeparator + "Note: If the button is grayed out, " +
                                                                               "the user has not sent any character data or you are on the same map, server and instance. If the latter is the case, join a group with that user and assign the character to them.");


            using (ImRaii.Group())
            {
                ElezenImgui.ColouredText("Map Info", ImGuiColors.DalamudGrey);
                ImGui.SameLine();
                ElezenImgui.ShowIcon(FontAwesomeIcon.ExternalLinkSquareAlt, ImGuiColors.DalamudGrey);
            }
            ElezenImgui.AttachTooltip(user.WorldDataDescriptor + ElezenImgui.TooltipSeparator);

            ImGui.SameLine();
            ElezenImgui.ShowIcon(FontAwesomeIcon.Map, sameMapAndServer.SameMap ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && user.WorldData != null)
            {
                PlayerInteractionService.SetMarkerAndOpenMap(new(user.WorldData.Value.PositionX, user.WorldData.Value.PositionY, user.WorldData.Value.PositionZ), user.Map);
            }
            ElezenImgui.AttachTooltip(string.Format("{0}" + ElezenImgui.TooltipSeparator + "Note: Click to open the users location on your map." + Environment.NewLine + "Note: For GPose synchronization to work properly, you must be on the same map.",
                sameMapAndServer.SameMap ? "You are on the same map." : "You are not on the same map."));

            ImGui.SameLine();
            ElezenImgui.ShowIcon(FontAwesomeIcon.Globe, sameMapAndServer.SameServer ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
            ElezenImgui.AttachTooltip(string.Format("{0}" + ElezenImgui.TooltipSeparator + "Note: GPose synchronization is not dependent on the current server, but you will have to spawn a character for the other lobby users.",
                sameMapAndServer.SameServer ? "You are on the same server." : "You are not on the same server."));

            ImGui.SameLine();
            ElezenImgui.ShowIcon(FontAwesomeIcon.Running, sameMapAndServer.SameEverything ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed);
            ElezenImgui.AttachTooltip((sameMapAndServer.SameEverything ? "You are in the same instanced area." : "You are not the same instanced area.") + ElezenImgui.TooltipSeparator +
                                          "Note: Users not in your instance, but on the same map, will be drawn as floating wisps." + Environment.NewLine
                                              + "Note: GPose synchronization is not dependent on the current instance, but you will have to spawn a character for the other lobby users.");

            using (ImRaii.Disabled(!_dalamudUtilService.IsInGpose))
            {
                ImGui.SetNextItemWidth(200);
                using (var combo = ImRaii.Combo("##character", string.IsNullOrEmpty(user.AssociatedCharaName) ? "No character assigned" : _ctx.CharaName(user.AssociatedCharaName)))
                {
                    if (combo)
                    {
                        foreach (var chara in gposeCharas)
                        {
                            if (chara == null) continue;

                            if (ImGui.Selectable(_ctx.CharaName(chara.Name.TextValue), chara.Address == user.Address))
                            {
                                user.AssociatedCharaName = chara.Name.TextValue;
                                user.Address = chara.Address;
                            }
                        }
                    }
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(user.Address == nint.Zero))
                {
                    if (ElezenImgui.IconButton(FontAwesomeIcon.Trash))
                    {
                        user.AssociatedCharaName = string.Empty;
                        user.Address = nint.Zero;
                    }
                }
                ElezenImgui.AttachTooltip("Unassign Actor for this user");
                if (_dalamudUtilService.IsInGpose && user.Address == nint.Zero)
                {
                    ImGui.SameLine();
                    ElezenImgui.ShowIcon(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed);
                    ElezenImgui.AttachTooltip("No valid character assigned for this user. Pose data will not be applied.");
                }
            }
        }, 5, width);
        ImGuiHelpers.ScaledDummy(5);
    }
}
