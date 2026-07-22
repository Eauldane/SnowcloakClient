using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Roleplay;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Chat;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class RoomAdministrationWindow : WindowMediatorSubscriberBase
{
    private readonly ChatClientService _chatService;
    private readonly ApiController _apiController;
    private readonly ChatIdentityResolver _identityResolver;
    private readonly AsyncOp _inviteOperation = new();
    private readonly AsyncOp _discoveryOperation = new();
    private readonly AsyncOp _sceneOperation = new();
    private readonly AsyncOp _finishSceneOperation = new();
    private readonly AsyncOp _turnOperation = new();
    private readonly PairManager _pairManager;
    private readonly RoleplayClientService _roleplayService;
    private readonly AsyncOp _topicOperation = new();
    private readonly AsyncOp _unbanOperation = new();
    private string _inviteSearch = string.Empty;
    private string _inviteStatus = string.Empty;
    private bool _inviteStatusIsError;
    private string _pendingInviteName = string.Empty;
    private string? _inviteHookId;
    private string _topicDraft = string.Empty;
    private bool _topicInitialised;
    private string _topicSource = string.Empty;
    private string _topicStatus = string.Empty;
    private bool _topicStatusIsError;
    private string _unbanStatus = string.Empty;
    private bool _unbanStatusIsError;
    private string _unbanUid = string.Empty;
    private bool _directoryListed;
    private string _directoryTags = string.Empty;
    private ProfileContentRating _directoryRating = ProfileContentRating.General;
    private string _directorySource = string.Empty;
    private string _directoryStatus = string.Empty;
    private bool _directoryStatusIsError;
    private bool _sceneEnabled;
    private string _sceneTitle = string.Empty;
    private string _sceneCast = string.Empty;
    private string _sceneSetting = string.Empty;
    private string _sceneWarnings = string.Empty;
    private string _sceneTone = string.Empty;
    private string _sceneSource = string.Empty;
    private string _sceneStatus = string.Empty;
    private bool _sceneStatusIsError;
    private readonly List<string> _turnOrder = [];
    private string _turnSource = string.Empty;
    private string _turnStatus = string.Empty;
    private bool _turnStatusIsError;

    public RoomAdministrationWindow(ILogger<RoomAdministrationWindow> logger, SnowMediator mediator,
        ApiController apiController, ChatClientService chatService, PairManager pairManager, ChatIdentityResolver identityResolver,
        RoleplayClientService roleplayService,
        PerformanceCollectorService performanceCollectorService, string roomId)
        : base(logger, mediator, BuildTitle(chatService, roomId), performanceCollectorService)
    {
        _apiController = apiController;
        _chatService = chatService;
        _pairManager = pairManager;
        _identityResolver = identityResolver;
        _roleplayService = roleplayService;
        RoomId = roomId;
        SetScaledSizeConstraints(new Vector2(500, 440), new Vector2(900, 1200));
        Size = new Vector2(620, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = true;
    }

    public string RoomId { get; }

    public override void OnOpen()
    {
        _ = _roleplayService.RefreshAsync();
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    protected override void DrawInternal()
    {
        ConsumeOperations();

        var room = _chatService.ListRooms()
            .FirstOrDefault(candidate => string.Equals(candidate.RoomId, RoomId, StringComparison.Ordinal));
        if (room == null)
        {
            DrawUnavailable("This room is no longer available.");
            return;
        }

        var members = _chatService.GetRoomMembers(RoomId);
        var actorRole = members
            .FirstOrDefault(member => string.Equals(member.User.UID, _identityResolver.SelfUid, StringComparison.Ordinal))?.Role
            ?? RoomRole.Member;

        DrawRoomSummary(room, members.Count, actorRole);
        ImGuiHelpers.ScaledDummy(10f);

        if (actorRole < RoomRole.Moderator)
        {
            DrawUnavailable("Room administration requires moderator or owner access.");
            return;
        }

        SynchroniseTopic(room);
        DrawTopicSection(room);
        ModernSection.SoftSeparator();
        if (_apiController.SupportsRpFeatures)
        {
            SynchroniseRoleplay(room);
            DrawDiscoverySection(room);
            ModernSection.SoftSeparator();
            DrawSceneSection(room, members);
            ModernSection.SoftSeparator();
        }
        else
        {
            ModernSection.Header(FontAwesomeIcon.TheaterMasks, "Roleplay scenes");
            ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                "Scene and public-discovery controls are unavailable on this server.");
            ModernSection.SoftSeparator();
        }
        DrawInvitationSection(room, members);
        ModernSection.SoftSeparator();
        DrawBanSection(room);
    }

    private void SynchroniseRoleplay(RoomData room)
    {
        var discovery = room.Discovery ?? new RoomDiscoveryDto();
        var discoverySource = string.Join('|', discovery.IsListed, discovery.ContentRating,
            string.Join('\u001f', discovery.Tags));
        if (!string.Equals(_directorySource, discoverySource, StringComparison.Ordinal) && !_discoveryOperation.IsRunning)
        {
            _directoryListed = discovery.IsListed;
            _directoryRating = discovery.ContentRating;
            _directoryTags = string.Join(", ", discovery.Tags);
            _directorySource = discoverySource;
        }

        var scene = room.Scene ?? new RoomSceneMetadataDto();
        var sceneSource = string.Join('|', scene.IsScene, scene.Title, scene.Setting, scene.ExpectedTone,
            string.Join('\u001f', scene.Cast), string.Join('\u001f', scene.ContentWarnings));
        if (!string.Equals(_sceneSource, sceneSource, StringComparison.Ordinal) && !_sceneOperation.IsRunning)
        {
            _sceneEnabled = scene.IsScene;
            _sceneTitle = scene.Title;
            _sceneCast = string.Join(", ", scene.Cast);
            _sceneSetting = scene.Setting;
            _sceneWarnings = string.Join(", ", scene.ContentWarnings);
            _sceneTone = scene.ExpectedTone;
            _sceneSource = sceneSource;
        }

        var turn = scene.TurnState ?? new RoomTurnStateDto();
        var turnSource = string.Join('|', turn.Enabled, turn.CurrentIndex, string.Join('\u001f', turn.UserUids));
        if (!string.Equals(_turnSource, turnSource, StringComparison.Ordinal) && !_turnOperation.IsRunning)
        {
            _turnOrder.Clear();
            _turnOrder.AddRange(turn.UserUids);
            _turnSource = turnSource;
        }
    }

    private void DrawDiscoverySection(RoomData room)
    {
        ModernSection.Header(FontAwesomeIcon.Compass, "Public discovery");
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            room.IsPrivate ? "Private rooms cannot be listed publicly." : "Let roleplayers find this room by theme and content rating.");
        using (ImRaii.Disabled(room.IsPrivate))
        {
            ImGui.Checkbox("List this room in RP discovery", ref _directoryListed);
        }
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##room-directory-tags", "Tags, comma-separated", ref _directoryTags, 520);
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Content rating##room-directory-rating", _directoryRating.ToString()))
        {
            foreach (var rating in Enum.GetValues<ProfileContentRating>())
            {
                if (ImGui.Selectable(rating.ToString(), rating == _directoryRating))
                    _directoryRating = rating;
            }
            ImGui.EndCombo();
        }
        using (ImRaii.Disabled(_discoveryOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save public discovery settings"))
            {
                _directoryStatus = string.Empty;
                _directoryStatusIsError = false;
                _ = _discoveryOperation.Run(() => _chatService.SetDiscoveryAsync(room, new RoomDiscoveryDto
                {
                    IsListed = !room.IsPrivate && _directoryListed,
                    Tags = SplitValues(_directoryTags),
                    ContentRating = _directoryRating,
                }));
            }
        }
        DrawOperationStatus(_discoveryOperation, _directoryStatus, _directoryStatusIsError, "Saving discovery...");
    }

    private void DrawSceneSection(RoomData room, IReadOnlyList<Snowcloak.API.Dto.Chat.RoomMemberDto> members)
    {
        ModernSection.Header(FontAwesomeIcon.TheaterMasks, "Scene room");
        ImGui.Checkbox("Use this room as an IC scene", ref _sceneEnabled);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##scene-title", "Scene title", ref _sceneTitle, 160);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##scene-cast", "Cast names, comma-separated", ref _sceneCast, 800);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##scene-setting", "Setting", ref _sceneSetting, 500);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##scene-warnings", "Content warnings, comma-separated", ref _sceneWarnings, 800);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##scene-tone", "Expected tone", ref _sceneTone, 200);
        using (ImRaii.Disabled(_sceneOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save scene metadata"))
            {
                _sceneStatus = string.Empty;
                _sceneStatusIsError = false;
                _ = _sceneOperation.Run(() => _chatService.SetSceneAsync(room, new RoomSceneMetadataDto
                {
                    IsScene = _sceneEnabled,
                    Title = _sceneTitle.Trim(),
                    Cast = SplitValues(_sceneCast),
                    Setting = _sceneSetting.Trim(),
                    ContentWarnings = SplitValues(_sceneWarnings),
                    ExpectedTone = _sceneTone.Trim(),
                    TurnState = _sceneEnabled
                        ? room.Scene?.TurnState ?? new RoomTurnStateDto()
                        : new RoomTurnStateDto(),
                }));
            }
        }
        DrawOperationStatus(_sceneOperation, _sceneStatus, _sceneStatusIsError, "Saving scene...");
        if (room.Scene?.IsScene == true)
        {
            ImGuiHelpers.ScaledDummy(6f);
            ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                "Finishing preserves this scene for its participants and clears the live room transcript.");
            using (ImRaii.Disabled(_finishSceneOperation.IsRunning))
            {
                if (ImGui.Button("Finish and archive scene"))
                {
                    _sceneStatus = string.Empty;
                    _sceneStatusIsError = false;
                    _ = _finishSceneOperation.Run(() => _chatService.FinishSceneAsync(room));
                }
            }
            DrawOperationStatus(_finishSceneOperation, _sceneStatus, _sceneStatusIsError, "Finishing scene...");
        }
        ImGuiHelpers.ScaledDummy(6f);
        DrawTurnOrder(room, members);
    }

    private void DrawTurnOrder(RoomData room, IReadOnlyList<Snowcloak.API.Dto.Chat.RoomMemberDto> members)
    {
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Turn order");
        foreach (var member in members)
        {
            using var id = ImRaii.PushId("turn-" + member.User.UID);
            var index = _turnOrder.FindIndex(uid => string.Equals(uid, member.User.UID, StringComparison.Ordinal));
            var selected = index >= 0;
            if (ImGui.Checkbox(ResolveMemberName(member), ref selected))
            {
                if (selected) _turnOrder.Add(member.User.UID);
                else if (index >= 0) _turnOrder.RemoveAt(index);
            }
            if (selected)
            {
                ImGui.SameLine();
                using (ImRaii.Disabled(index <= 0))
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowUp, "Move earlier"))
                        (_turnOrder[index - 1], _turnOrder[index]) = (_turnOrder[index], _turnOrder[index - 1]);
                ImGui.SameLine();
                using (ImRaii.Disabled(index < 0 || index >= _turnOrder.Count - 1))
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowDown, "Move later"))
                        (_turnOrder[index + 1], _turnOrder[index]) = (_turnOrder[index], _turnOrder[index + 1]);
            }
        }

        var activeTurn = room.Scene?.TurnState;
        if (activeTurn?.Enabled == true && activeTurn.UserUids.Count > 0)
        {
            var uid = activeTurn.UserUids[Math.Clamp(activeTurn.CurrentIndex, 0, activeTurn.UserUids.Count - 1)];
            var current = members.FirstOrDefault(member => string.Equals(member.User.UID, uid, StringComparison.Ordinal));
            ImGui.TextColored(ImGuiColors.HealerGreen, "Current: " + (current == null ? uid : ResolveMemberName(current)));
        }

        using (ImRaii.Disabled(!_sceneEnabled || _turnOrder.Count == 0 || _turnOperation.IsRunning))
        {
            if (ImGui.Button(activeTurn?.Enabled == true ? "Update turn order" : "Start turn order"))
            {
                _turnStatus = string.Empty;
                _turnStatusIsError = false;
                _ = _turnOperation.Run(() => _chatService.SetTurnOrderAsync(room, true, _turnOrder));
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(activeTurn?.Enabled != true || _turnOperation.IsRunning))
        {
            if (ImGui.Button("Advance turn"))
                _ = _turnOperation.Run(() => _chatService.AdvanceTurnAsync(room));
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(activeTurn?.Enabled != true || _turnOperation.IsRunning))
        {
            if (ImGui.Button("End turn order"))
                _ = _turnOperation.Run(() => _chatService.SetTurnOrderAsync(room, false, []));
        }
        DrawOperationStatus(_turnOperation, _turnStatus, _turnStatusIsError, "Updating turns...");
    }

    private string ResolveMemberName(Snowcloak.API.Dto.Chat.RoomMemberDto member)
        => member.SceneNickname ?? _identityResolver.Resolve(member.User).Name;

    private static List<string> SplitValues(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string BuildTitle(ChatClientService chatService, string roomId)
    {
        ArgumentNullException.ThrowIfNull(chatService);
        var roomName = chatService.ListRooms()
            .FirstOrDefault(room => string.Equals(room.RoomId, roomId, StringComparison.Ordinal))?.Name
            ?? "Room";
        return string.Format(CultureInfo.InvariantCulture, "Room Administration — {0}###SnowcloakRoomAdmin_{1}", roomName, roomId);
    }

    private static void DrawRoomSummary(RoomData room, int memberCount, RoomRole actorRole)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var border = ImRaii.PushColor(ImGuiCol.Border, SnowcloakColours.CompactBorderSubtle);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 11f) * scale);
        using var summary = ImRaii.Child("room-administration-summary", new Vector2(-1f, 72f * scale), true,
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(room.IsPrivate ? ImGuiColors.DalamudYellow : SnowcloakColours.OnlineBlue,
                (room.IsPrivate ? FontAwesomeIcon.Lock : FontAwesomeIcon.DoorOpen).ToIconString());
        }

        ImGui.SameLine(0f, 10f * scale);
        ImGui.TextUnformatted(room.Name);
        var access = room.IsPrivate ? "Private room" : "Public room";
        var members = string.Format(CultureInfo.InvariantCulture, "{0} {1}", memberCount, memberCount == 1 ? "member" : "members");
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            string.Format(CultureInfo.InvariantCulture, "{0}  •  {1}  •  {2}", access, members, actorRole));
    }

    private void SynchroniseTopic(RoomData room)
    {
        var currentTopic = room.Topic ?? string.Empty;
        if (!_topicInitialised)
        {
            _topicDraft = currentTopic;
            _topicSource = currentTopic;
            _topicInitialised = true;
            return;
        }

        if (!string.Equals(currentTopic, _topicSource, StringComparison.Ordinal))
        {
            if (string.Equals(_topicDraft, _topicSource, StringComparison.Ordinal))
            {
                _topicDraft = currentTopic;
            }

            _topicSource = currentTopic;
        }
    }

    private void DrawTopicSection(RoomData room)
    {
        ModernSection.Header(FontAwesomeIcon.CommentAlt, "Room topic");
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            "Shown beneath the room title and in the room directory.");
        ImGuiHelpers.ScaledDummy(5f);
        ImGui.InputTextMultiline("##room-administration-topic", ref _topicDraft, 200,
            new Vector2(-1f, 74f * ImGuiHelpers.GlobalScale));

        var requestedTopic = string.IsNullOrWhiteSpace(_topicDraft) ? null : _topicDraft.Trim();
        var changed = !string.Equals(requestedTopic ?? string.Empty, room.Topic ?? string.Empty, StringComparison.Ordinal);
        using (ImRaii.Disabled(!changed || _topicOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save topic"))
            {
                _topicStatus = string.Empty;
                _topicStatusIsError = false;
                _ = _topicOperation.Run(() => _chatService.SetTopicAsync(room, requestedTopic));
            }
        }

        DrawOperationStatus(_topicOperation, _topicStatus, _topicStatusIsError, "Saving topic...");
    }

    private void DrawInvitationSection(RoomData room, IReadOnlyList<Snowcloak.API.Dto.Chat.RoomMemberDto> members)
    {
        ModernSection.Header(FontAwesomeIcon.UserPlus, "Invitations");
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            room.IsPrivate
                ? "Grant a direct pair access to discover and join this private room."
                : "Invite a direct pair to join this room.");
        ImGuiHelpers.ScaledDummy(5f);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##room-invite-search", "Search direct pairs", ref _inviteSearch, 80);
        var hooks = _roleplayService.CurrentHooks.Hooks;
        if (room.Scene?.IsScene == true && hooks.Count > 0)
        {
            ImGui.SetNextItemWidth(-1f);
            var selectedHook = hooks.FirstOrDefault(hook => string.Equals(hook.HookId, _inviteHookId, StringComparison.Ordinal));
            if (ImGui.BeginCombo("##room-invite-hook", selectedHook == null ? "No hook attached" : "Attach: " + selectedHook.Title))
            {
                if (ImGui.Selectable("No hook attached", _inviteHookId == null)) _inviteHookId = null;
                foreach (var hook in hooks)
                {
                    if (ImGui.Selectable(hook.Title, string.Equals(hook.HookId, _inviteHookId, StringComparison.Ordinal)))
                        _inviteHookId = hook.HookId;
                }
                ImGui.EndCombo();
            }
        }

        var activeMembers = members.Select(member => member.User.UID).ToHashSet(StringComparer.Ordinal);
        var pairs = _pairManager.DirectPairs
            .Where(pair => !activeMembers.Contains(pair.UserData.UID)
                           && (string.IsNullOrWhiteSpace(_inviteSearch)
                               || pair.UserData.AliasOrUID.Contains(_inviteSearch, StringComparison.OrdinalIgnoreCase)
                               || pair.UserData.UID.Contains(_inviteSearch, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pair => pair.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ImGuiHelpers.ScaledDummy(5f);
        using (var list = ImRaii.Child("room-invite-list", new Vector2(-1f, 138f * ImGuiHelpers.GlobalScale), true))
        {
            if (pairs.Length == 0)
            {
                ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                    string.IsNullOrWhiteSpace(_inviteSearch)
                        ? "No direct pairs are available to invite."
                        : "No direct pairs match this search.");
            }
            else
            {
                foreach (var pair in pairs)
                {
                    DrawInviteRow(room, pair.UserData);
                }
            }
        }

        DrawOperationStatus(_inviteOperation, _inviteStatus, _inviteStatusIsError, "Sending invitation...");
    }

    private void DrawInviteRow(RoomData room, UserData user)
    {
        using var id = ImRaii.PushId(user.UID);
        var display = _identityResolver.Resolve(user);
        var buttonWidth = 82f * ImGuiHelpers.GlobalScale;
        ImGui.AlignTextToFramePadding();
        ElezenImgui.ColouredText(display.Name, display.Colour, display.Glow);
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - buttonWidth));
        using (ImRaii.Disabled(_inviteOperation.IsRunning))
        {
            if (ImGui.Button("Invite", new Vector2(buttonWidth, 0f)))
            {
                _pendingInviteName = display.Name;
                _inviteStatus = string.Empty;
                _inviteStatusIsError = false;
                var hook = room.Scene?.IsScene == true
                    ? _roleplayService.CurrentHooks.Hooks.FirstOrDefault(candidate => string.Equals(candidate.HookId, _inviteHookId, StringComparison.Ordinal))
                    : null;
                var intro = hook == null ? null : new RpIntroSnapshotDto
                {
                    HookId = hook.HookId,
                    Title = hook.Title,
                    Description = hook.Description,
                };
                _ = _inviteOperation.Run(() => _chatService.InviteAsync(room, user, intro));
            }
        }
    }

    private void DrawBanSection(RoomData room)
    {
        ModernSection.Header(FontAwesomeIcon.UnlockAlt, "Restore access");
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            "Enter a banned user's UID to allow them to be invited or join again.");
        ImGuiHelpers.ScaledDummy(5f);

        var buttonWidth = 112f * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(Math.Max(120f * ImGuiHelpers.GlobalScale,
            ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##room-unban-uid", "User UID", ref _unbanUid, 20);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_unbanUid) || _unbanOperation.IsRunning))
        {
            if (ImGui.Button("Unban UID", new Vector2(buttonWidth, 0f)))
            {
                var uid = _unbanUid.Trim();
                _unbanStatus = string.Empty;
                _unbanStatusIsError = false;
                _ = _unbanOperation.Run(() => _chatService.UnbanAsync(room, new UserData(uid)));
            }
        }

        DrawOperationStatus(_unbanOperation, _unbanStatus, _unbanStatusIsError, "Restoring access...");
    }

    private void ConsumeOperations()
    {
        if (_topicOperation.IsCompleted)
        {
            _topicStatusIsError = _topicOperation.Faulted;
            _topicStatus = _topicOperation.Faulted ? _topicOperation.Error ?? "Unable to update the room topic." : "Room topic updated.";
            _topicOperation.Reset();
        }

        if (_inviteOperation.IsCompleted)
        {
            _inviteStatusIsError = _inviteOperation.Faulted;
            _inviteStatus = _inviteOperation.Faulted
                ? _inviteOperation.Error ?? "Unable to invite this user."
                : string.Format(CultureInfo.InvariantCulture, "Room access granted to {0}.", _pendingInviteName);
            _inviteOperation.Reset();
        }

        if (_unbanOperation.IsCompleted)
        {
            _unbanStatusIsError = _unbanOperation.Faulted;
            _unbanStatus = _unbanOperation.Faulted ? _unbanOperation.Error ?? "Unable to restore access." : "Room access restored.";
            if (!_unbanOperation.Faulted)
            {
                _unbanUid = string.Empty;
            }

            _unbanOperation.Reset();
        }

        ConsumeRoleplayOperation(_discoveryOperation, ref _directoryStatus, ref _directoryStatusIsError,
            "Unable to update public discovery.", "Public discovery updated.");
        ConsumeRoleplayOperation(_sceneOperation, ref _sceneStatus, ref _sceneStatusIsError,
            "Unable to update scene metadata.", "Scene metadata updated.");
        ConsumeRoleplayOperation(_turnOperation, ref _turnStatus, ref _turnStatusIsError,
            "Unable to update the turn order.", "Turn order updated.");
    }

    private static void ConsumeRoleplayOperation(AsyncOp operation, ref string status, ref bool isError,
        string fallbackError, string success)
    {
        if (!operation.IsCompleted)
            return;
        isError = operation.Faulted;
        status = operation.Faulted ? operation.Error ?? fallbackError : success;
        operation.Reset();
    }

    private static void DrawOperationStatus(AsyncOp operation, string status, bool isError, string runningText)
    {
        if (operation.IsRunning)
        {
            ImGui.SameLine();
            ElezenImgui.ColouredText(runningText, ImGuiColors.DalamudYellow);
        }
        else if (!string.IsNullOrWhiteSpace(status))
        {
            ImGui.SameLine();
            ElezenImgui.ColouredText(status, isError ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen);
        }
    }

    private static void DrawUnavailable(string message)
    {
        ModernSection.Header(FontAwesomeIcon.ExclamationTriangle, "Room administration unavailable");
        ElezenImgui.ColouredWrappedText(message, ImGuiColors.DalamudYellow);
    }
}
