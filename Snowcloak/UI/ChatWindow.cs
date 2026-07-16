using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Snowcloak.Core.Chat;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Chat;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.WebAPI;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class ChatWindow : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly ChatClientService _chatService;
    private readonly ChatConversationView _conversationView;
    private readonly UiFontService _fontService;
    private readonly PairManager _pairManager;
    private readonly ChatIdentityResolver _identityResolver;
    private readonly ApiController _apiController;
    private readonly NotesStore _notesStore;
    private readonly ConcurrentQueue<Action> _uiUpdates = new();
    private bool _sidebarCollapsed;
    private float _sidebarWidth = ModernSidebar.ExpandedWidth - 15f;
    private bool _showMembers = true;
    private bool _openRoomBrowser;
    private bool _closeRoomBrowser;
    private bool _showRoomCreation;
    private string _roomSearch = string.Empty;
    private string _roomName = string.Empty;
    private string _roomTopic = string.Empty;
    private bool _privateRoom;
    private string _roomStatus = string.Empty;
    private bool _creatingRoom;
    private string? _noteUid;
    private string _noteDraft = string.Empty;
    private bool _openNoteEditor;

    public ChatWindow(ILogger<ChatWindow> logger, SnowMediator mediator, ChatClientService chatService,
        ImGuiChatRenderer renderer, UiFontService fontService, PairManager pairManager, ChatIdentityResolver identityResolver,
        ApiController apiController, NotesStore notesStore,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Chat###SnowcloakChat", performanceCollectorService)
    {
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _chatService = chatService;
        _conversationView = new ChatConversationView(logger, chatService, renderer, mediator);
        _fontService = fontService;
        _pairManager = pairManager;
        _identityResolver = identityResolver;
        _apiController = apiController;
        _notesStore = notesStore;
        SetScaledSizeConstraints(new Vector2(720, 440), new Vector2(1800, 1800));
        Size = new Vector2(1040, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    protected override void DrawInternal()
    {
        SnowcloakUi.AccentColor = ElezenColours.SnowcloakBlue;
        while (_uiUpdates.TryDequeue(out var update))
        {
            update();
        }

        var snapshot = _chatService.Store.Snapshot;
        var selected = snapshot.ActiveConversation;
        if (selected == null || snapshot.Conversations.All(conversation => conversation.Key != selected))
        {
            selected = snapshot.Conversations.Count == 0 ? null : snapshot.Conversations[0].Key;
            Select(selected);
        }

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().WindowPadding.Y - ImGuiHelpers.GlobalScale
            + ImGui.GetStyle().ItemSpacing.Y);
        DrawConversationSidebar(snapshot);
        if (!_sidebarCollapsed)
        {
            ImGui.SameLine(0f, 0f);
            DrawSidebarResizeHandle();
            ImGui.SameLine(0f, 4f * ImGuiHelpers.GlobalScale);
        }
        else
        {
            ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
        }
        DrawConversationArea(snapshot, selected);
        if (_openRoomBrowser)
        {
            _openRoomBrowser = false;
            _showRoomCreation = false;
            ImGui.OpenPopup("Rooms browser");
        }
        DrawRoomBrowser();
        DrawNoteEditor();
    }

    private void DrawConversationSidebar(ChatStoreSnapshot snapshot)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = (_sidebarCollapsed ? 38f : _sidebarWidth) * scale;
        var padding = _sidebarCollapsed ? new Vector2(4f, 12f) : new Vector2(14f, 12f);
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var childPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, padding * scale);
        using var sidebar = ImRaii.Child("chat-conversations", new Vector2(width, -1), false);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 4f * scale));

        var index = 1;
        foreach (var kind in Enum.GetValues<ConversationKind>())
        {
            var conversations = snapshot.Conversations.Where(conversation => conversation.Key.Kind == kind).ToArray();
            if (conversations.Length == 0)
            {
                continue;
            }

            DrawConversationSectionHeader(kind);
            foreach (var conversation in conversations)
            {
                DrawConversationRow(conversation, index, snapshot.ActiveConversation == conversation.Key);
                index++;
            }

            if (!_sidebarCollapsed)
            {
                DrawSidebarSeparator();
            }
        }

        DrawSidebarAction(FontAwesomeIcon.Search, "Browse Rooms", () =>
        {
            _openRoomBrowser = true;
            Queue(_chatService.RefreshAsync(), nameof(ChatClientService.RefreshAsync));
        });

        var footerHeight = (_sidebarCollapsed ? 34f : 128f) * scale;
        var available = ImGui.GetContentRegionAvail().Y;
        if (available > footerHeight)
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + available - footerHeight);
        }

        DrawSidebarAction(_sidebarCollapsed ? FontAwesomeIcon.ChevronRight : FontAwesomeIcon.ChevronLeft,
            _sidebarCollapsed ? "Expand" : "Collapse", () => _sidebarCollapsed = !_sidebarCollapsed);
        if (_sidebarCollapsed)
        {
            return;
        }

        var unread = snapshot.TotalUnread;
        ImGuiHelpers.ScaledDummy(3);
        DrawCentredSidebarText(unread == 0
            ? "No Unread"
            : string.Format(CultureInfo.InvariantCulture, "{0} Unread", unread),
            unread == 0 ? SnowcloakColours.CompactTextMuted : SnowcloakColours.OnlineBlue);
        ImGuiHelpers.ScaledDummy(4);
        DrawChatSettingsButton();
    }

    private void DrawSidebarResizeHandle()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = 4f * scale;
        var height = ImGui.GetContentRegionAvail().Y;
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##chat-sidebar-resize", new Vector2(width, height));
        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        if (active)
        {
            _sidebarWidth = Math.Clamp(_sidebarWidth + ImGui.GetIO().MouseDelta.X / scale, 140f, 300f);
        }
        if (active || hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        }

        var colour = active || hovered ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactBorderSubtle;
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(min.X + width * 0.5f, min.Y),
            new Vector2(min.X + width * 0.5f, min.Y + height),
            Colour.Vector4ToColour(colour), scale);
    }

    public override void OnClose()
    {
        _chatService.Store.SetActive(null);
        base.OnClose();
    }

    private void DrawConversationArea(ChatStoreSnapshot snapshot, ConversationKey? selected)
    {
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactBg);
        using var contentPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,
            new Vector2(14f, 10f) * ImGuiHelpers.GlobalScale);
        using var content = ImRaii.Child("chat-content", new Vector2(-1, -1), false);

        if (!selected.HasValue)
        {
            DrawEmptyConversationState();
            return;
        }

        var conversation = snapshot.Conversations.FirstOrDefault(candidate => candidate.Key == selected.Value);
        if (conversation == null)
        {
            DrawEmptyConversationState();
            return;
        }

        var hasMemberList = conversation.Key.Kind is ConversationKind.Syncshell or ConversationKind.Room;
        var drawMembers = hasMemberList && _showMembers;
        var scale = ImGuiHelpers.GlobalScale;
        var memberWidth = 214f * scale;
        var gap = 8f * scale;
        var centreWidth = drawMembers ? -memberWidth - gap : -1f;

        using (var centre = ImRaii.Child("chat-centre", new Vector2(centreWidth, -1), false))
        {
            DrawConversationHeader(conversation, hasMemberList);
            _conversationView.Draw(conversation.Key, showHeader: false);
        }

        if (drawMembers)
        {
            ImGui.SameLine(0f, gap);
            using var membersBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
            using var membersPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f) * scale);
            using var members = ImRaii.Child("chat-members", new Vector2(memberWidth, -1), false);
            DrawMembers(conversation.Key);
        }
    }

    private void DrawConversationSectionHeader(ConversationKind kind)
    {
        if (_sidebarCollapsed)
        {
            return;
        }

        var label = kind switch
        {
            ConversationKind.Direct => "Direct Chats",
            ConversationKind.Syncshell => "Syncshells",
            ConversationKind.Room => "Rooms",
            _ => kind.ToString(),
        };
        var icon = GetConversationIcon(kind);
        using var colour = ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted);
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextUnformatted(icon.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGuiHelpers.ScaledDummy(2);
    }

    private void DrawConversationRow(ConversationSnapshot conversation, int index, bool active)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;
        var height = 38f * scale;
        var min = ImGui.GetCursorScreenPos();
        var id = $"conversation-{conversation.Key}";
        ImGui.InvisibleButton($"##{id}", new Vector2(width, height));
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            var tooltip = string.Format(CultureInfo.InvariantCulture, "{0}\n/snow {1} <message>{2}",
                conversation.Title, index, conversation.Muted ? "\nMuted" : string.Empty);
            ImGui.SetTooltip(tooltip);
        }

        DrawConversationContextMenu(conversation);
        if (clicked)
        {
            Select(conversation.Key);
        }

        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        if (active)
        {
            var accent = ModernTheme.Palette.Accent;
            var left = Colour.Vector4ToColour(Colour.WithAlpha(accent, 0.34f));
            var right = Colour.Vector4ToColour(Colour.WithAlpha(accent, 0.06f));
            drawList.AddRectFilledMultiColor(min, max, left, right, right, left);
            drawList.AddRectFilled(min + new Vector2(0f, 6f * scale),
                new Vector2(min.X + 3f * scale, max.Y - 6f * scale), Colour.Vector4ToColour(accent));
        }
        else if (hovered)
        {
            drawList.AddRectFilled(min, max, Colour.Vector4ToColour(new Vector4(0.090f, 0.150f, 0.220f, 0.54f)), 3f * scale);
        }

        var display = ResolveConversationDisplay(conversation);
        var iconText = display.Icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText);
        var iconX = _sidebarCollapsed ? min.X + (width - iconSize.X) * 0.5f : min.X + 9f * scale;
        drawList.AddText(new Vector2(iconX, min.Y + (height - iconSize.Y) * 0.5f),
            Colour.Vector4ToColour(display.Colour ?? SnowcloakColours.CompactTextMuted), iconText);
        ImGui.PopFont();

        if (_sidebarCollapsed)
        {
            if (conversation.Unread > 0)
            {
                drawList.AddCircleFilled(new Vector2(max.X - 4f * scale, min.Y + 5f * scale), 3f * scale,
                    Colour.Vector4ToColour(SnowcloakColours.OnlineBlue));
            }
            return;
        }

        var cursor = ImGui.GetCursorPos();
        var textX = min.X + 32f * scale;
        var rightLabel = conversation.Unread > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} · {1}", index, conversation.Unread)
            : index.ToString(CultureInfo.InvariantCulture);
        var rightSize = ImGui.CalcTextSize(rightLabel);
        var rightX = max.X - rightSize.X - 7f * scale;
        ImGui.PushClipRect(new Vector2(textX, min.Y), new Vector2(rightX - 5f * scale, max.Y), true);
        ImGui.SetCursorScreenPos(new Vector2(textX, min.Y + (height - ImGui.GetTextLineHeight()) * 0.5f));
        ElezenImgui.ColouredText(display.Title, display.Colour, display.Glow);
        ImGui.PopClipRect();
        ImGui.SetCursorPos(cursor);
        drawList.AddText(new Vector2(rightX, min.Y + (height - rightSize.Y) * 0.5f),
            Colour.Vector4ToColour(conversation.Unread > 0 ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactTextMuted),
            rightLabel);
    }

    private void DrawConversationContextMenu(ConversationSnapshot conversation)
    {
        if (!ImGui.BeginPopupContextItem($"conversation-menu-{conversation.Key}"))
        {
            return;
        }

        if (ImGui.MenuItem(conversation.Muted ? "Unmute" : "Mute"))
        {
            _chatService.SetMuted(conversation.Key, !conversation.Muted);
        }

        if (ImGui.MenuItem("Open pop-out"))
        {
            Mediator.Publish(new OpenChatPopoutMessage(conversation.Key));
        }

        ImGui.EndPopup();
    }

    private void DrawConversationHeader(ConversationSnapshot conversation, bool hasMemberList)
    {
        var display = ResolveConversationDisplay(conversation);
        var headerStart = ImGui.GetCursorPos();
        ImGuiHelpers.ScaledDummy(6);
        using (_fontService.UidFont.Push())
        {
            var titleSize = ImGui.CalcTextSize(display.Title);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X - titleSize.X) * 0.5f);
            ElezenImgui.ColouredText(display.Title, display.Colour ?? SnowcloakColours.OnlineBlue, display.Glow);
        }

        var subtitle = GetConversationSubtitle(conversation);
        var subtitleSize = ImGui.CalcTextSize(subtitle);
        ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X - subtitleSize.X) * 0.5f);
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, subtitle);
        if (conversation.Key.Kind == ConversationKind.Room)
        {
            var room = _chatService.ListRooms().FirstOrDefault(candidate =>
                string.Equals(candidate.RoomId, conversation.Key.Id, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(room?.Topic))
            {
                var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X
                    - 190f * ImGuiHelpers.GlobalScale;
                var topic = TrimToWidth(room.Topic, Math.Max(120f * ImGuiHelpers.GlobalScale, availableWidth));
                var topicSize = ImGui.CalcTextSize(topic);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X - topicSize.X) * 0.5f);
                ImGui.TextColored(SnowcloakColours.CompactTextMuted, topic);
            }
        }
        var headerEnd = ImGui.GetCursorPos();

        var buttonSize = new Vector2(34f, 30f) * ImGuiHelpers.GlobalScale;
        var actionCount = 2 + (conversation.Key.Kind == ConversationKind.Direct || hasMemberList ? 1 : 0);
        var actionsWidth = actionCount * buttonSize.X + Math.Max(0, actionCount - 1) * ImGui.GetStyle().ItemSpacing.X;
        var actionX = ImGui.GetWindowContentRegionMax().X - actionsWidth;
        var actionY = headerStart.Y + ((headerEnd.Y - headerStart.Y) - buttonSize.Y) * 0.5f;
        ImGui.SetCursorPos(new Vector2(actionX, actionY));

        if (conversation.Key.Kind == ConversationKind.Direct)
        {
            var pair = _pairManager.GetPairByUID(conversation.Key.Id);
            using (ImRaii.Disabled(pair == null))
            {
                if (DrawCompactIconButton(FontAwesomeIcon.UserCircle, buttonSize, "chat-profile"))
                {
                    Mediator.Publish(new ProfileOpenStandaloneMessage(pair!.UserData, pair, FallbackName: pair.PlayerName));
                }
            }
            ElezenImgui.AttachTooltip("Open profile");
            ImGui.SameLine();
        }
        else if (hasMemberList)
        {
            if (DrawCompactIconButton(FontAwesomeIcon.PeopleGroup, buttonSize, "chat-members-toggle"))
            {
                _showMembers = !_showMembers;
            }
            ElezenImgui.AttachTooltip(_showMembers ? "Hide member list" : "Show member list");
            ImGui.SameLine();
        }

        var muteIcon = conversation.Muted ? FontAwesomeIcon.BellSlash : FontAwesomeIcon.Bell;
        if (DrawCompactIconButton(muteIcon, buttonSize, "chat-mute"))
        {
            _chatService.SetMuted(conversation.Key, !conversation.Muted);
        }
        ElezenImgui.AttachTooltip(conversation.Muted ? "Unmute conversation" : "Mute conversation");
        ImGui.SameLine();

        if (DrawCompactIconButton(FontAwesomeIcon.ExternalLinkAlt, buttonSize, "chat-popout"))
        {
            Mediator.Publish(new OpenChatPopoutMessage(conversation.Key));
        }
        ElezenImgui.AttachTooltip("Open conversation in a separate window");
        ImGui.SetCursorPos(headerEnd);
        ImGuiHelpers.ScaledDummy(7);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);
    }

    private static string TrimToWidth(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth)
        {
            return text;
        }

        const string ellipsis = "…";
        var length = text.Length;
        while (length > 0 && ImGui.CalcTextSize(text.AsSpan(0, length).ToString() + ellipsis).X > maxWidth)
        {
            length--;
        }
        return text.AsSpan(0, length).ToString() + ellipsis;
    }

    private (string Title, Vector4? Colour, Vector4? Glow, FontAwesomeIcon Icon) ResolveConversationDisplay(
        ConversationSnapshot conversation)
    {
        if (conversation.Key.Kind == ConversationKind.Direct)
        {
            var display = _identityResolver.Resolve(conversation.Key.Id);
            return (display?.Name ?? conversation.Title, display?.Colour, display?.Glow, FontAwesomeIcon.User);
        }

        if (conversation.Key.Kind == ConversationKind.Syncshell)
        {
            var group = _pairManager.Groups.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.GID, conversation.Key.Id, StringComparison.Ordinal));
            return (group?.GroupAliasOrGID ?? conversation.Title,
                group == null ? null : Colour.HexToVector4OrNull(group.Group.DisplayColour),
                null,
                FontAwesomeIcon.PeopleGroup);
        }

        return (conversation.Title, SnowcloakColours.OnlineBlue, null, FontAwesomeIcon.Comments);
    }

    private string GetConversationSubtitle(ConversationSnapshot conversation)
    {
        var parts = new List<string>();
        switch (conversation.Key.Kind)
        {
            case ConversationKind.Direct:
                parts.Add("Direct Pair");
                parts.Add(_pairManager.GetPairByUID(conversation.Key.Id)?.IsOnline == true ? "Online" : "Offline");
                break;
            case ConversationKind.Syncshell:
                parts.Add("Syncshell");
                parts.Add(FormatMemberCount(conversation.Members.Count));
                break;
            case ConversationKind.Room:
                var room = _chatService.ListRooms().FirstOrDefault(candidate =>
                    string.Equals(candidate.RoomId, conversation.Key.Id, StringComparison.Ordinal));
                parts.Add(room?.IsPrivate == true ? "Private Room" : "Room");
                parts.Add(FormatMemberCount(conversation.Members.Count));
                break;
        }

        if (conversation.Muted)
        {
            parts.Add("Muted");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatMemberCount(int count)
        => string.Format(CultureInfo.InvariantCulture, "{0} {1}", count, count == 1 ? "Member" : "Members");

    private static FontAwesomeIcon GetConversationIcon(ConversationKind kind)
        => kind switch
        {
            ConversationKind.Direct => FontAwesomeIcon.User,
            ConversationKind.Syncshell => FontAwesomeIcon.PeopleGroup,
            ConversationKind.Room => FontAwesomeIcon.Comments,
            _ => FontAwesomeIcon.Comment,
        };

    private void DrawSidebarAction(FontAwesomeIcon icon, string label, Action action)
    {
        if (!_sidebarCollapsed)
        {
            if (ModernSidebar.DrawRow(icon, label, active: false))
            {
                action();
            }
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;
        var height = 28f * scale;
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##chat-sidebar-{label}", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetTooltip(label);
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            action();
        }

        if (hovered)
        {
            ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(width, height),
                Colour.Vector4ToColour(new Vector4(0.090f, 0.150f, 0.220f, 0.54f)), 3f * scale);
        }

        var iconText = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText);
        ImGui.GetWindowDrawList().AddText(min + new Vector2((width - iconSize.X) * 0.5f, (height - iconSize.Y) * 0.5f),
            Colour.Vector4ToColour(SnowcloakColours.CompactTextMuted), iconText);
        ImGui.PopFont();
    }

    private static void DrawSidebarSeparator()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorScreenPos() + new Vector2(6f, 4f) * scale;
        var end = start with { X = start.X + ImGui.GetContentRegionAvail().X - 12f * scale };
        ImGui.GetWindowDrawList().AddLine(start, end, Colour.Vector4ToColour(SnowcloakColours.CompactBorderSubtle), scale);
        ImGui.Dummy(new Vector2(1f, 9f * scale));
    }

    private static void DrawCentredSidebarText(string text, Vector4 colour)
    {
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(text);
        ImGui.Dummy(new Vector2(1f, textSize.Y));
        ImGui.GetWindowDrawList().AddText(new Vector2(min.X + (width - textSize.X) * 0.5f, min.Y),
            Colour.Vector4ToColour(colour), text);
    }

    private void DrawChatSettingsButton()
    {
        var baseColour = new Vector4(0.060f, 0.105f, 0.155f, 0.82f);
        var hoverColour = new Vector4(0.120f, 0.190f, 0.285f, 0.92f);
        using var buttonColour = ImRaii.PushColor(ImGuiCol.Button, baseColour);
        using var buttonHoverColour = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverColour);
        using var buttonActiveColour = ImRaii.PushColor(ImGuiCol.ButtonActive, hoverColour);
        using var borderColour = ImRaii.PushColor(ImGuiCol.Border, SnowcloakColours.OnlineBlue);
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, ImGuiHelpers.GlobalScale);
        if (ImGui.Button("Chat Settings", new Vector2(-1f, 38f * ImGuiHelpers.GlobalScale)))
        {
            Mediator.Publish(new OpenChatSettingsMessage());
        }
    }

    private void DrawEmptyConversationState()
    {
        var label = "No conversations yet";
        var textSize = ImGui.CalcTextSize(label);
        var available = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPos(new Vector2(
            ImGui.GetCursorPosX() + Math.Max(0f, (available.X - textSize.X) * 0.5f),
            ImGui.GetCursorPosY() + Math.Max(0f, (available.Y - ImGui.GetFrameHeightWithSpacing() * 2f) * 0.5f)));
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, label);
        var buttonWidth = 140f * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (available.X - buttonWidth) * 0.5f));
        if (ImGui.Button("Browse Rooms", new Vector2(buttonWidth, 0f)))
        {
            _openRoomBrowser = true;
            Queue(_chatService.RefreshAsync(), nameof(ChatClientService.RefreshAsync));
        }
    }

    private static bool DrawCompactIconButton(FontAwesomeIcon icon, Vector2 size, string id)
    {
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##compact-chat-icon-{id}", size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var max = min + size;
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var background = hovered
            ? new Vector4(0.120f, 0.190f, 0.285f, 0.92f)
            : new Vector4(0.060f, 0.105f, 0.155f, 0.82f);
        var border = hovered ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactBorderSubtle;
        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(background), 5f * scale);
        drawList.AddRect(min, max, Colour.Vector4ToColour(border), 5f * scale, ImDrawFlags.None, scale);

        var iconText = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText);
        drawList.AddText(min + (size - iconSize) * 0.5f, Colour.Vector4ToColour(Vector4.One), iconText);
        ImGui.PopFont();
        return clicked;
    }

    private void DrawMembers(ConversationKey key)
    {
        if (key.Kind == ConversationKind.Syncshell)
        {
            var group = _pairManager.Groups.Values.FirstOrDefault(candidate => string.Equals(candidate.GID, key.Id, StringComparison.Ordinal));
            if (group == null || !_pairManager.GroupPairs.TryGetValue(group, out var pairs))
            {
                ModernSection.Header(FontAwesomeIcon.PeopleGroup, "Members (0)");
                return;
            }

            ModernSection.Header(FontAwesomeIcon.PeopleGroup,
                string.Format(CultureInfo.InvariantCulture, "Members ({0})", pairs.Count + 1));
            ImGuiHelpers.ScaledDummy(4);
            var conversation = _chatService.Store.Snapshot.Conversations
                .FirstOrDefault(candidate => candidate.Key == key);
            var roles = conversation?.Members;
            var labels = conversation?.MemberLabels;
            var syncshellActorRole = roles?.GetValueOrDefault(_identityResolver.SelfUid) ?? RoomRole.Member;
            var self = new UserData(_identityResolver.SelfUid, _apiController.VanityId,
                _apiController.DisplayColour, _apiController.DisplayGlowColour);
            DrawMemberName(self, syncshellActorRole, labels?.GetValueOrDefault(self.UID), null, syncshellActorRole,
                online: true, group);
            foreach (var member in pairs
                         .Select(pair => (Pair: pair, Role: roles?.GetValueOrDefault(pair.UserData.UID) ?? RoomRole.Member))
                         .OrderByDescending(member => member.Role)
                         .ThenByDescending(member => member.Pair.IsOnline)
                         .ThenBy(member => _identityResolver.Resolve(member.Pair.UserData).Name, StringComparer.OrdinalIgnoreCase))
            {
                DrawMemberName(member.Pair.UserData, member.Role, labels?.GetValueOrDefault(member.Pair.UserData.UID),
                    null, syncshellActorRole, member.Pair.IsOnline, group);
            }

            return;
        }

        var roomData = _chatService.ListRooms().FirstOrDefault(room => string.Equals(room.RoomId, key.Id, StringComparison.Ordinal));
        if (roomData == null)
        {
            ModernSection.Header(FontAwesomeIcon.PeopleGroup, "Members (0)");
            return;
        }

        var members = _chatService.GetRoomMembers(key.Id);
        ModernSection.Header(FontAwesomeIcon.PeopleGroup,
            string.Format(CultureInfo.InvariantCulture, "Members ({0})", members.Count));
        var actorRole = members.FirstOrDefault(member => string.Equals(member.User.UID, _identityResolver.SelfUid, StringComparison.Ordinal))?.Role
                        ?? RoomRole.Member;
        DrawRoomControls(roomData, actorRole);
        foreach (var member in members)
        {
            DrawMemberName(member.User, member.Role, null, roomData, actorRole, online: true, null);
        }
    }

    private void DrawRoomControls(RoomData room, RoomRole actorRole)
    {
        if (!string.IsNullOrWhiteSpace(room.Topic))
        {
            ImGuiHelpers.ScaledDummy(4);
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Topic");
            ImGui.TextWrapped(room.Topic);
        }

        if (actorRole >= RoomRole.Moderator)
        {
            ImGuiHelpers.ScaledDummy(4);
            if (ImGui.Button("Room administration", new Vector2(-1f, 0f)))
            {
                Mediator.Publish(new OpenRoomAdministrationMessage(room.RoomId));
            }
        }

        ImGuiHelpers.ScaledDummy(4);
        if (ImGui.Button("Leave room", new Vector2(-1f, 0f)))
        {
            Queue(_chatService.LeaveRoomAsync(room), nameof(ChatClientService.LeaveRoomAsync));
        }

        ImGuiHelpers.ScaledDummy(4);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);
    }

    private void DrawMemberName(UserData user, RoomRole role, IReadOnlyList<string>? memberLabels, RoomData? room,
        RoomRole actorRole, bool online, GroupFullInfoDto? syncshell)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;
        var height = 32f * scale;
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##member-{user.UID}", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(width, height),
                Colour.Vector4ToColour(new Vector4(0.090f, 0.150f, 0.220f, 0.54f)), 3f * scale);
        }

        DrawMemberContextMenu(user, role, room, actorRole, syncshell);
        var drawList = ImGui.GetWindowDrawList();
        var badgeX = min.X + 7f * scale;
        var hasPermissionBadge = role != RoomRole.Member;
        if (role != RoomRole.Member)
        {
            DrawMemberBadge(drawList, min, height, scale, ref badgeX,
                role == RoomRole.Owner ? FontAwesomeIcon.Crown : FontAwesomeIcon.UserShield,
                SnowcloakColours.OnlineBlue);
        }

        var hasMemberLabel = SyncshellMemberLabelUi.TryGetPresenceOverride(memberLabels, out var labelIcon,
            out var labelColour, out var labelTooltip);
        if (hasMemberLabel)
        {
            DrawMemberBadge(drawList, min, height, scale, ref badgeX, labelIcon, labelColour);
        }

        if (!hasPermissionBadge && !hasMemberLabel)
        {
            DrawMemberBadge(drawList, min, height, scale, ref badgeX, FontAwesomeIcon.User,
                SnowcloakColours.CompactTextMuted);
        }

        var display = _identityResolver.Resolve(user.UID) ?? _identityResolver.Resolve(user);
        var cursor = ImGui.GetCursorPos();
        var textX = badgeX + 1f * scale;
        ImGui.PushClipRect(new Vector2(textX, min.Y), new Vector2(min.X + width - 16f * scale, min.Y + height), true);
        ImGui.SetCursorScreenPos(new Vector2(textX, min.Y + (height - ImGui.GetTextLineHeight()) * 0.5f));
        ElezenImgui.ColouredText(display.Name, display.Colour, display.Glow);
        ImGui.PopClipRect();
        ImGui.SetCursorPos(cursor);

        var statusColour = online ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactOffline;
        drawList.AddCircleFilled(new Vector2(min.X + width - 7f * scale, min.Y + height * 0.5f), 3f * scale,
            Colour.Vector4ToColour(statusColour));
        if (hovered)
        {
            var tooltip = display.Name;
            if (role != RoomRole.Member)
            {
                tooltip += $"\n{role}";
            }
            if (hasMemberLabel)
            {
                tooltip += $"\n{labelTooltip}";
            }
            ImGui.SetTooltip(tooltip);
        }
    }

    private static void DrawMemberBadge(ImDrawListPtr drawList, Vector2 min, float height, float scale,
        ref float badgeX, FontAwesomeIcon icon, Vector4 colour)
    {
        var iconText = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconSize = ImGui.CalcTextSize(iconText);
            drawList.AddText(new Vector2(badgeX, min.Y + (height - iconSize.Y) * 0.5f),
                Colour.Vector4ToColour(colour), iconText);
            badgeX += iconSize.X + 6f * scale;
        }
    }

    private void DrawMemberContextMenu(UserData user, RoomRole role, RoomData? room, RoomRole actorRole,
        GroupFullInfoDto? syncshell)
    {
        if (!ImGui.BeginPopupContextItem($"member-actions-{user.UID}"))
        {
            return;
        }

        var pair = _pairManager.GetPairByUID(user.UID);
        if (pair != null && ImGui.MenuItem("Open profile"))
        {
            Mediator.Publish(new ProfileOpenStandaloneMessage(user, pair));
        }

        if (ImGui.MenuItem("Edit note"))
        {
            _noteUid = user.UID;
            _noteDraft = _notesStore.GetNoteForUid(user.UID) ?? string.Empty;
            _openNoteEditor = true;
        }

        if (pair?.UserPair != null)
        {
            if (ImGui.MenuItem("Change permissions"))
            {
                Mediator.Publish(new OpenPermissionWindow(pair));
            }

            if (ImGui.MenuItem(pair.IsPaused ? "Resume pairing" : "Pause pairing"))
            {
                Mediator.Publish(new CyclePauseMessage(pair.UserData));
            }
        }
        else if (!string.Equals(user.UID, _identityResolver.SelfUid, StringComparison.Ordinal)
                 && ImGui.MenuItem("Add pair"))
        {
            Queue(_apiController.UserAddPair(new(new(user.UID))), nameof(ApiController.UserAddPair));
        }

        if (syncshell != null && pair != null
            && !string.Equals(user.UID, _identityResolver.SelfUid, StringComparison.Ordinal))
        {
            var actorIsOwner = string.Equals(syncshell.OwnerUID, _identityResolver.SelfUid, StringComparison.Ordinal);
            var actorIsModerator = syncshell.GroupUserInfo.IsModerator();
            var targetIsOwner = string.Equals(syncshell.OwnerUID, user.UID, StringComparison.Ordinal);
            var targetInfo = pair.GroupPair.TryGetValue(syncshell, out var membership)
                ? membership.GroupPairStatusInfo
                : GroupUserInfo.None;
            var targetIsModerator = targetInfo.IsModerator();
            var canModerate = !targetIsOwner && (actorIsOwner || (actorIsModerator && !targetIsModerator));

            if (actorIsOwner && !targetIsOwner)
            {
                ImGui.Separator();
                if (ImGui.MenuItem(targetIsModerator ? "Remove moderator" : "Make moderator"))
                {
                    targetInfo.SetModerator(!targetIsModerator);
                    Queue(_apiController.GroupSetUserInfo(new GroupPairUserInfoDto(syncshell.Group, user, targetInfo)),
                        nameof(ApiController.GroupSetUserInfo));
                }
            }

            if (canModerate)
            {
                var controlHeld = ElezenImgui.CtrlPressed();
                if (ImGui.MenuItem("Remove from syncshell", "Hold CTRL", false, controlHeld))
                {
                    Queue(_apiController.GroupRemoveUser(new GroupPairDto(syncshell.Group, user)),
                        nameof(ApiController.GroupRemoveUser));
                }

                if (ImGui.MenuItem("Ban from syncshell", "Hold CTRL", false, controlHeld))
                {
                    Mediator.Publish(new OpenBanUserPopupMessage(pair, syncshell));
                }
            }
        }

        if (room != null && actorRole >= RoomRole.Moderator
            && role < actorRole && !string.Equals(user.UID, _identityResolver.SelfUid, StringComparison.Ordinal))
        {
            if (actorRole == RoomRole.Owner && ImGui.BeginMenu("Set role"))
            {
                foreach (var targetRole in Enum.GetValues<RoomRole>())
                {
                    var label = targetRole == RoomRole.Owner ? "Transfer ownership" : targetRole.ToString();
                    if (ImGui.MenuItem(label, string.Empty, role == targetRole))
                    {
                        Queue(_chatService.SetRoleAsync(room, user, targetRole), nameof(ChatClientService.SetRoleAsync));
                    }
                }

                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Kick"))
            {
                Queue(_chatService.KickAsync(room, user), nameof(ChatClientService.KickAsync));
            }

            if (ImGui.MenuItem("Ban"))
            {
                Queue(_chatService.BanAsync(room, user), nameof(ChatClientService.BanAsync));
            }
        }

        if (syncshell != null && actorRole >= RoomRole.Moderator)
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Open syncshell admin"))
            {
                Mediator.Publish(new OpenSyncshellAdminPanel(syncshell));
            }
        }

        ImGui.EndPopup();
    }

    private void DrawNoteEditor()
    {
        if (_openNoteEditor)
        {
            _openNoteEditor = false;
            ImGui.OpenPopup("Edit member note");
        }

        if (!ImGui.BeginPopupModal("Edit member note", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.SetNextItemWidth(360f * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Note", ref _noteDraft, 255);
        if (ImGui.Button("Save") && _noteUid != null)
        {
            _notesStore.SetNoteForUid(_noteUid, _noteDraft);
            _noteUid = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            _noteUid = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawRoomBrowser()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var viewport = ImGui.GetMainViewport();
        var desiredSize = Vector2.Min(new Vector2(700f, 460f) * scale, viewport.WorkSize - new Vector2(32f, 48f) * scale);
        ImGui.SetNextWindowSize(desiredSize, ImGuiCond.Appearing);
        using var popupBackground = ImRaii.PushColor(ImGuiCol.PopupBg, SnowcloakColours.CompactBg);
        using var popupPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (!ImGui.BeginPopupModal("Rooms browser"))
        {
            return;
        }

        if (_closeRoomBrowser)
        {
            _closeRoomBrowser = false;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        DrawRoomBrowserHeader();

        var contentPadding = 18f * scale;
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(contentPadding));
        var available = ImGui.GetContentRegionAvail() - new Vector2(contentPadding, contentPadding);
        using (var contentBackground = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactBg))
        using (var content = ImRaii.Child("room-browser-content", new Vector2(available.X, available.Y), false))
        {
            if (_showRoomCreation)
            {
                DrawRoomCreationPanel();
            }
            else
            {
                DrawRoomDirectory();
            }
        }

        ImGui.EndPopup();
    }

    private void DrawRoomBrowserHeader()
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var headerBackground = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var headerPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(18f, 10f) * scale);
        using var header = ImRaii.Child("room-browser-header", new Vector2(-1f, 52f * scale), false,
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(SnowcloakColours.OnlineBlue, FontAwesomeIcon.Comments.ToIconString());
        }

        ImGui.SameLine(0f, 10f * scale);
        var titleX = ImGui.GetCursorPosX();
        ImGui.TextUnformatted(_showRoomCreation ? "Create a room" : "Room directory");
        ImGui.SetCursorPosX(titleX);
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            _showRoomCreation ? "Set up a permanent room" : "Find public rooms and rooms shared with you");

        var closeSize = new Vector2(28f) * scale;
        ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - closeSize.X, 14f * scale));
        if (DrawCompactIconButton(FontAwesomeIcon.Times, closeSize, "room-browser-close"))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawRoomCreationPanel()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var formWidth = Math.Min(480f * scale, ImGui.GetContentRegionAvail().X);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (ImGui.GetContentRegionAvail().X - formWidth) * 0.5f));
        using var panelBackground = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var panelPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(18f, 16f) * scale);
        using var panel = ImRaii.Child("room-create-panel", new Vector2(formWidth, -1f), true);

        DrawRoomBrowserSectionTitle(FontAwesomeIcon.PlusCircle, "Room details");
        ImGuiHelpers.ScaledDummy(10f);

        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "NAME");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##new-room-name", "Room name", ref _roomName, 40);
        var roomNameValid = IsRoomNameValid(_roomName);
        var roomNameColour = string.IsNullOrWhiteSpace(_roomName) || roomNameValid
            ? SnowcloakColours.CompactTextMuted
            : ImGuiColors.DalamudRed;
        ElezenImgui.ColouredWrappedText(
            roomNameValid || string.IsNullOrWhiteSpace(_roomName)
                ? "1–40 characters: letters, numbers, underscores and hyphens."
                : "Start with a letter or number and do not use spaces or other symbols.",
            roomNameColour);
        ImGuiHelpers.ScaledDummy(7f);

        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "TOPIC");
        ImGui.InputTextMultiline("##new-room-topic", ref _roomTopic, 200, new Vector2(-1f, 76f * scale));
        ImGuiHelpers.ScaledDummy(10f);

        ImGui.Checkbox("Private room", ref _privateRoom);
        ElezenImgui.ColouredWrappedText(
            _privateRoom ? "Only invited users can find and join." : "Visible to everyone in the room directory.",
            SnowcloakColours.CompactTextMuted);

        if (!string.IsNullOrWhiteSpace(_roomStatus))
        {
            ImGuiHelpers.ScaledDummy(8f);
            ElezenImgui.ColouredWrappedText(_roomStatus,
                _creatingRoom ? SnowcloakColours.OnlineBlue : ImGuiColors.DalamudRed);
        }
        ImGuiHelpers.ScaledDummy(14f);

        var buttonHeight = 32f * scale;
        var spacing = 8f * scale;
        var buttonWidth = (ImGui.GetContentRegionAvail().X - spacing) * 0.5f;
        if (DrawRoomBrowserButton(FontAwesomeIcon.ArrowLeft, "Back", "cancel-create-room",
                new Vector2(buttonWidth, buttonHeight), false))
        {
            _showRoomCreation = false;
            _roomStatus = string.Empty;
        }
        ImGui.SameLine(0f, spacing);
        var canCreate = roomNameValid && !_creatingRoom;
        if (DrawRoomBrowserButton(FontAwesomeIcon.Plus, "Create room", "create-room",
                new Vector2(buttonWidth, buttonHeight), true, canCreate))
        {
            _creatingRoom = true;
            _roomStatus = "Creating room...";
            Queue(CreateRoomAsync(_roomName.Trim(), _roomTopic, _privateRoom), nameof(ChatClientService.CreateRoomAsync));
        }
    }

    private static bool IsRoomNameValid(string value)
    {
        var name = value.Trim();
        if (name.Length is < 1 or > 40 || !char.IsAsciiLetterOrDigit(name[0]))
        {
            return false;
        }

        return name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private void DrawRoomDirectory()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = 8f * scale;
        var refreshSize = new Vector2(28f) * scale;
        var createWidth = 116f * scale;
        ImGui.SetNextItemWidth(Math.Max(120f * scale,
            ImGui.GetContentRegionAvail().X - refreshSize.X - createWidth - spacing * 2f));
        ImGui.InputTextWithHint("##room-search", "Search room names and topics", ref _roomSearch, 80);
        ImGui.SameLine(0f, spacing);
        if (DrawCompactIconButton(FontAwesomeIcon.SyncAlt, refreshSize, "room-browser-refresh"))
        {
            _roomStatus = "Refreshing rooms...";
            Queue(RefreshRoomsAsync(), nameof(ChatClientService.RefreshAsync));
        }
        ElezenImgui.AttachTooltip("Refresh room directory");
        ImGui.SameLine(0f, spacing);
        if (DrawRoomBrowserButton(FontAwesomeIcon.Plus, "New room", "show-create-room",
                new Vector2(createWidth, refreshSize.Y), true))
        {
            _showRoomCreation = true;
            _roomStatus = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(_roomStatus))
        {
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.TextColored(SnowcloakColours.OnlineBlue, _roomStatus);
        }
        ImGuiHelpers.ScaledDummy(8f);

        using var list = ImRaii.Child("room-directory-list", new Vector2(-1f, -1f), false);
        var rooms = _chatService.ListRooms()
            .Where(room => string.IsNullOrWhiteSpace(_roomSearch)
                           || room.Name.Contains(_roomSearch, StringComparison.OrdinalIgnoreCase)
                           || (room.Topic?.Contains(_roomSearch, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToArray();
        if (rooms.Length == 0)
        {
            DrawEmptyRoomDirectory(!string.IsNullOrWhiteSpace(_roomSearch));
            return;
        }

        var counts = _chatService.SnapshotRoomCounts();
        var joinedRooms = _chatService.Store.Snapshot.Conversations
            .Where(conversation => conversation.Key.Kind == ConversationKind.Room)
            .Select(conversation => conversation.Key.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            DrawRoomDirectoryCard(room, counts.GetValueOrDefault(room.RoomId), joinedRooms.Contains(room.RoomId));
            ImGuiHelpers.ScaledDummy(6f);
        }
    }

    private static void DrawRoomBrowserSectionTitle(FontAwesomeIcon icon, string title)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(SnowcloakColours.OnlineBlue, icon.ToIconString());
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(title);
    }

    private void DrawRoomDirectoryCard(RoomData room, int memberCount, bool joined)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var id = ImRaii.PushId(room.RoomId);
        using var cardBackground = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanelAlt);
        using var cardBorder = ImRaii.PushColor(ImGuiCol.Border, SnowcloakColours.CompactBorderSubtle);
        using var cardPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 9f) * scale);
        using var card = ImRaii.Child("room-card", new Vector2(-1f, 76f * scale), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        var actionWidth = 86f * scale;
        var textWidth = Math.Max(80f * scale, ImGui.GetContentRegionAvail().X - actionWidth - 14f * scale);
        var roomIcon = room.IsPrivate ? FontAwesomeIcon.Lock : FontAwesomeIcon.DoorOpen;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(room.IsPrivate ? ImGuiColors.DalamudYellow : SnowcloakColours.OnlineBlue, roomIcon.ToIconString());
        }

        ImGui.SameLine();
        var memberLabel = string.Format(CultureInfo.InvariantCulture, "{0} {1}", memberCount, memberCount == 1 ? "member" : "members");
        var memberWidth = ImGui.CalcTextSize(memberLabel).X;
        ImGui.TextUnformatted(TrimToWidth(room.Name, Math.Max(40f * scale, textWidth - memberWidth - 30f * scale)));
        ImGui.SameLine();
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, memberLabel);

        var topic = string.IsNullOrWhiteSpace(room.Topic) ? "No topic set" : room.Topic;
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, TrimToWidth(topic, textWidth));

        ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - actionWidth, 22f * scale));
        if (joined)
        {
            if (DrawRoomBrowserButton(FontAwesomeIcon.ArrowRight, "Open", "open-room", new Vector2(actionWidth, 30f * scale), false))
            {
                Select(new ConversationKey(ConversationKind.Room, room.RoomId));
                _closeRoomBrowser = true;
            }
        }
        else if (DrawRoomBrowserButton(FontAwesomeIcon.Plus, "Join", "join-room", new Vector2(actionWidth, 30f * scale), true))
        {
            _roomStatus = string.Format(CultureInfo.InvariantCulture, "Joining {0}...", room.Name);
            Queue(JoinRoomAsync(room), nameof(ChatClientService.JoinRoomAsync));
        }
    }

    private void DrawEmptyRoomDirectory(bool filtered)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var title = filtered ? "No matching rooms" : "No rooms yet";
        var message = filtered ? "Try a different name or topic." : "Create the first room, or refresh to check again.";
        var available = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(20f * scale, available.Y * 0.28f));
        var icon = filtered ? FontAwesomeIcon.Search : FontAwesomeIcon.Comments;
        var iconText = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconSize = ImGui.CalcTextSize(iconText);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (available.X - iconSize.X) * 0.5f));
            ImGui.TextColored(SnowcloakColours.OnlineBlue, iconText);
        }

        var titleSize = ImGui.CalcTextSize(title);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (available.X - titleSize.X) * 0.5f));
        ImGui.TextUnformatted(title);
        var messageSize = ImGui.CalcTextSize(message);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (available.X - messageSize.X) * 0.5f));
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, message);
        ImGuiHelpers.ScaledDummy(10f);

        var buttonWidth = 150f * scale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (available.X - buttonWidth) * 0.5f));
        if (DrawRoomBrowserButton(filtered ? FontAwesomeIcon.Times : FontAwesomeIcon.Plus,
                filtered ? "Clear search" : "Create a room", "empty-room-action",
                new Vector2(buttonWidth, 30f * scale), !filtered))
        {
            if (filtered)
            {
                _roomSearch = string.Empty;
            }
            else
            {
                _showRoomCreation = true;
            }
        }
    }

    private static bool DrawRoomBrowserButton(FontAwesomeIcon icon, string label, string id, Vector2 requestedSize,
        bool primary, bool enabled = true)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = requestedSize.X < 0f ? ImGui.GetContentRegionAvail().X : requestedSize.X;
        var size = new Vector2(width, requestedSize.Y);
        var min = ImGui.GetCursorScreenPos();
        using var disabled = ImRaii.Disabled(!enabled);
        ImGui.InvisibleButton("##room-browser-button-" + id, size);
        var clicked = enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = enabled && ImGui.IsItemHovered();
        var active = enabled && ImGui.IsItemActive();
        var background = !enabled
            ? new Vector4(0.070f, 0.100f, 0.130f, 0.72f)
            : primary
                ? active
                    ? new Vector4(0.230f, 0.480f, 0.760f, 1f)
                    : hovered ? new Vector4(0.190f, 0.400f, 0.650f, 1f) : new Vector4(0.145f, 0.290f, 0.470f, 1f)
                : hovered ? new Vector4(0.120f, 0.190f, 0.285f, 0.92f) : new Vector4(0.060f, 0.105f, 0.155f, 0.82f);
        var border = hovered ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactBorderSubtle;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + size, Colour.Vector4ToColour(background), 4f * scale);
        drawList.AddRect(min, min + size, Colour.Vector4ToColour(border), 4f * scale, ImDrawFlags.None, scale);

        var iconText = icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var labelSize = ImGui.CalcTextSize(label);
        var gap = 7f * scale;
        var contentWidth = iconSize.X + gap + labelSize.X;
        var iconPosition = min + new Vector2((size.X - contentWidth) * 0.5f, (size.Y - iconSize.Y) * 0.5f);
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(iconPosition, Colour.Vector4ToColour(enabled ? Vector4.One : SnowcloakColours.CompactTextMuted), iconText);
        }

        drawList.AddText(new Vector2(iconPosition.X + iconSize.X + gap, min.Y + (size.Y - labelSize.Y) * 0.5f),
            Colour.Vector4ToColour(enabled ? Vector4.One : SnowcloakColours.CompactTextMuted), label);
        return clicked;
    }

    private async Task RefreshRoomsAsync()
    {
        try
        {
            await _chatService.RefreshAsync().ConfigureAwait(false);
            _uiUpdates.Enqueue(() => _roomStatus = string.Empty);
        }
        catch (HubException ex)
        {
            _uiUpdates.Enqueue(() => _roomStatus = ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _uiUpdates.Enqueue(() => _roomStatus = ex.Message);
        }
    }

    private async Task CreateRoomAsync(string name, string topic, bool isPrivate)
    {
        try
        {
            var room = await _chatService.CreateRoomAsync(name, topic, isPrivate).ConfigureAwait(false);
            _uiUpdates.Enqueue(() =>
            {
                _roomName = string.Empty;
                _roomTopic = string.Empty;
                _roomStatus = string.Empty;
                _creatingRoom = false;
                Select(new ConversationKey(ConversationKind.Room, room.RoomId));
                _closeRoomBrowser = true;
            });
        }
        catch (HubException ex)
        {
            _uiUpdates.Enqueue(() =>
            {
                _roomStatus = ex.Message;
                _creatingRoom = false;
            });
        }
        catch (InvalidOperationException ex)
        {
            _uiUpdates.Enqueue(() =>
            {
                _roomStatus = ex.Message;
                _creatingRoom = false;
            });
        }
    }

    private async Task JoinRoomAsync(RoomData room)
    {
        try
        {
            var status = await _chatService.JoinRoomAsync(room).ConfigureAwait(false)
                ? string.Empty
                : "Unable to join this room.";
            _uiUpdates.Enqueue(() =>
            {
                _roomStatus = status;
                if (string.IsNullOrEmpty(status))
                {
                    Select(new ConversationKey(ConversationKind.Room, room.RoomId));
                    _closeRoomBrowser = true;
                }
            });
        }
        catch (HubException ex)
        {
            _uiUpdates.Enqueue(() => _roomStatus = ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _uiUpdates.Enqueue(() => _roomStatus = ex.Message);
        }
    }

    private void Select(ConversationKey? key)
    {
        _chatService.Store.SetActive(key);
        if (key.HasValue)
        {
            Queue(_chatService.Store.EnsureHistory(key.Value), nameof(ChatStore.EnsureHistory));
        }
    }

    private void Queue(Task task, string operation)
    {
        _ = _backgroundTasks.Run(() => task, operation);
    }
}
