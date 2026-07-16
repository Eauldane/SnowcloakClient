using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Chat;
using Snowcloak.Services.Mediator;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class RoomAdministrationWindow : WindowMediatorSubscriberBase
{
    private readonly ChatClientService _chatService;
    private readonly ChatIdentityResolver _identityResolver;
    private readonly AsyncOp _inviteOperation = new();
    private readonly PairManager _pairManager;
    private readonly AsyncOp _topicOperation = new();
    private readonly AsyncOp _unbanOperation = new();
    private string _inviteSearch = string.Empty;
    private string _inviteStatus = string.Empty;
    private bool _inviteStatusIsError;
    private string _pendingInviteName = string.Empty;
    private string _topicDraft = string.Empty;
    private bool _topicInitialised;
    private string _topicSource = string.Empty;
    private string _topicStatus = string.Empty;
    private bool _topicStatusIsError;
    private string _unbanStatus = string.Empty;
    private bool _unbanStatusIsError;
    private string _unbanUid = string.Empty;

    public RoomAdministrationWindow(ILogger<RoomAdministrationWindow> logger, SnowMediator mediator,
        ChatClientService chatService, PairManager pairManager, ChatIdentityResolver identityResolver,
        PerformanceCollectorService performanceCollectorService, string roomId)
        : base(logger, mediator, BuildTitle(chatService, roomId), performanceCollectorService)
    {
        _chatService = chatService;
        _pairManager = pairManager;
        _identityResolver = identityResolver;
        RoomId = roomId;
        SetScaledSizeConstraints(new Vector2(500, 440), new Vector2(900, 1200));
        Size = new Vector2(620, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = true;
    }

    public string RoomId { get; }

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
        DrawInvitationSection(room, members);
        ModernSection.SoftSeparator();
        DrawBanSection(room);
    }

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
                _ = _inviteOperation.Run(() => _chatService.InviteAsync(room, user));
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
