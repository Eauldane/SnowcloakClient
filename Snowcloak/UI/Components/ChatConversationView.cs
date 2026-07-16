using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.Core.Chat;
using Snowcloak.Services.Chat;
using Snowcloak.Services.Mediator;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI.Components;

public sealed class ChatConversationView
{
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly ChatClientService _chatService;
    private readonly ImGuiChatRenderer _renderer;
    private readonly SnowMediator _mediator;
    private string _draft = string.Empty;
    private ConversationKey? _draftKey;

    public ChatConversationView(ILogger logger, ChatClientService chatService, ImGuiChatRenderer renderer,
        SnowMediator mediator)
    {
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _chatService = chatService;
        _renderer = renderer;
        _mediator = mediator;
    }

    public void Draw(ConversationKey key, bool showHeader = true)
    {
        var conversation = _chatService.Store.Snapshot.Conversations.FirstOrDefault(candidate => candidate.Key == key);
        if (conversation == null)
        {
            ImGui.TextDisabled("Conversation is no longer available.");
            return;
        }

        if (_draftKey != key)
        {
            _draftKey = key;
            _draft = conversation.Draft;
        }

        if (showHeader)
        {
            DrawHeader(conversation);
        }

        var inputHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        using (var log = ImRaii.Child($"chat-log-{key}", new Vector2(-1, -inputHeight), false))
        {
            DateTime? currentDate = null;
            foreach (var entry in conversation.Entries)
            {
                var localDate = entry.Timestamp.ToLocalTime().Date;
                if (currentDate != localDate)
                {
                    currentDate = localDate;
                    DrawDateSeparator(localDate);
                }

                using var id = ImRaii.PushId(entry.LocalId);
                var role = conversation.Members.GetValueOrDefault(entry.SenderUid);
                var labels = conversation.MemberLabels.GetValueOrDefault(entry.SenderUid);
                _renderer.Render(entry, ImGui.GetContentRegionAvail().X,
                    role is RoomRole.Owner or RoomRole.Moderator ? role : null, labels);
                if (entry.State == DeliveryState.Failed)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Retry"))
                    {
                        Queue(_chatService.Store.RetryAsync(key, entry.LocalId), nameof(ChatStore.RetryAsync));
                    }
                }
            }

            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20f * ImGuiHelpers.GlobalScale)
            {
                ImGui.SetScrollHereY(1f);
            }
        }

        using var inputDisabled = ImRaii.Disabled(!_chatService.CanSend);
        ImGui.SetNextItemWidth(-ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
        var submit = ImGui.InputText("##chat-message", ref _draft, 2000, ImGuiInputTextFlags.EnterReturnsTrue);
        _chatService.Store.SetDraft(key, _draft);
        ImGui.SameLine();
        if (DrawSendButton())
        {
            submit = true;
        }

        if (_chatService.CanSend && submit && !string.IsNullOrWhiteSpace(_draft))
        {
            var text = _draft;
            _draft = string.Empty;
            _chatService.Store.SetDraft(key, string.Empty);
            Queue(_chatService.Store.SendAsync(key, text), nameof(ChatStore.SendAsync));
            ImGui.SetKeyboardFocusHere(-1);
        }
    }

    private static void DrawDateSeparator(DateTime date)
    {
        var today = DateTime.Today;
        var label = date == today
            ? "Today"
            : date == today.AddDays(-1)
                ? "Yesterday"
                : date.ToString("D", CultureInfo.CurrentCulture);
        using var colour = ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted);
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextUnformatted(FontAwesomeIcon.ChevronDown.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);
    }

    private static bool DrawSendButton()
    {
        var baseColour = new Vector4(0.145f, 0.290f, 0.470f, 1f);
        var hoverColour = new Vector4(0.190f, 0.350f, 0.540f, 1f);
        var activeColour = new Vector4(0.230f, 0.410f, 0.620f, 1f);
        using var buttonColour = ImRaii.PushColor(ImGuiCol.Button, baseColour);
        using var buttonHoverColour = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverColour);
        using var buttonActiveColour = ImRaii.PushColor(ImGuiCol.ButtonActive, activeColour);
        bool clicked;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            clicked = ImGui.Button(FontAwesomeIcon.PaperPlane.ToIconString() + "##send-message",
                new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight()));
        }
        ElezenImgui.AttachTooltip("Send message");
        return clicked;
    }

    private void DrawHeader(ConversationSnapshot conversation)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(conversation.Title);
        ImGui.SameLine();
        var muteIcon = conversation.Muted ? FontAwesomeIcon.BellSlash : FontAwesomeIcon.Bell;
        if (ElezenImgui.ShowIconButton(muteIcon, conversation.Muted ? "Unmute conversation" : "Mute conversation"))
        {
            _chatService.SetMuted(conversation.Key, !conversation.Muted);
        }

        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ExternalLinkAlt, "Open conversation in a separate window"))
        {
            _mediator.Publish(new OpenChatPopoutMessage(conversation.Key));
        }

        ImGui.Separator();
    }

    private void Queue(Task task, string operation)
    {
        _ = _backgroundTasks.Run(() => task, operation);
    }
}
