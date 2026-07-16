using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Snowcloak.Core.Chat;
using Snowcloak.Services.Chat;
using Snowcloak.Services.Mediator;
using System.Numerics;

namespace Snowcloak.UI.Components;

public sealed class ImGuiChatRenderer
{
    private readonly BbCodeRenderService _bbCodeRenderer;
    private readonly ChatIdentityResolver _identityResolver;
    private readonly SnowMediator _mediator;

    public ImGuiChatRenderer(BbCodeRenderService bbCodeRenderer, ChatIdentityResolver identityResolver,
        SnowMediator mediator)
    {
        _bbCodeRenderer = bbCodeRenderer;
        _identityResolver = identityResolver;
        _mediator = mediator;
    }

    public void Render(ChatEntry entry, float width, RoomRole? role = null, IReadOnlyList<string>? memberLabels = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var name = entry.Display.Name;
        var timestamp = entry.Timestamp.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        var startX = ImGui.GetCursorPosX();
        var timestampWidth = ImGui.CalcTextSize(timestamp).X;
        var timestampColumnWidth = 52f * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(startX + Math.Max(0f, timestampColumnWidth - timestampWidth));
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
        {
            ImGui.TextUnformatted(timestamp);
        }

        ImGui.SameLine(startX + timestampColumnWidth + 8f * ImGuiHelpers.GlobalScale);
        if (entry.Kind is ChatEntryKind.MemberJoined or ChatEntryKind.MemberLeft)
        {
            RenderMembershipEvent(entry);
            DrawEntryContextMenu(entry);
            return;
        }

        if (role is RoomRole.Owner or RoomRole.Moderator)
        {
            var icon = role == RoomRole.Owner ? FontAwesomeIcon.Crown : FontAwesomeIcon.UserShield;
            DrawMemberBadge(icon, SnowcloakColours.OnlineBlue, role == RoomRole.Owner ? "Owner" : "Moderator");
        }

        if (SyncshellMemberLabelUi.TryGetPresenceOverride(memberLabels, out var labelIcon, out var labelColour, out var labelTooltip))
        {
            DrawMemberBadge(labelIcon, labelColour, labelTooltip);
        }

        ElezenImgui.ColouredText(entry.IsEmote ? $"* {name}" : $"{name}:",
            entry.Display.Colour ?? ImGuiColors.DalamudWhite, entry.Display.Glow);

        ImGui.SameLine();
        var first = true;
        foreach (var segment in entry.Segments)
        {
            if (entry.IsEmote && first && segment.Value.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
            {
                RenderText(segment.Value[4..]);
                first = false;
                continue;
            }

            if (!first)
            {
                ImGui.SameLine(0f, 0f);
            }

            RenderSegment(segment, width);
            first = false;
        }

        if (entry.State == DeliveryState.Pending)
        {
            ImGui.SameLine();
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, "sending");
        }
        else if (entry.State == DeliveryState.Failed)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudRed, "failed");
        }

        DrawEntryContextMenu(entry);
    }

    private static void RenderMembershipEvent(ChatEntry entry)
    {
        var joined = entry.Kind == ChatEntryKind.MemberJoined;
        DrawMemberBadge(joined ? FontAwesomeIcon.UserPlus : FontAwesomeIcon.UserMinus,
            joined ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactTextMuted,
            joined ? "Member joined" : "Member left");
        ElezenImgui.ColouredText(entry.Display.Name,
            entry.Display.Colour ?? ImGuiColors.DalamudWhite, entry.Display.Glow);
        ImGui.SameLine();
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, entry.RawText);
    }

    private static void DrawEntryContextMenu(ChatEntry entry)
    {
        if (ImGui.BeginPopupContextItem($"chat-entry-{entry.LocalId}"))
        {
            if (ImGui.MenuItem("Copy message"))
            {
                ImGui.SetClipboardText(entry.Kind == ChatEntryKind.Message
                    ? entry.RawText
                    : $"{entry.Display.Name} {entry.RawText}");
            }

            ImGui.EndPopup();
        }
    }

    private static void DrawMemberBadge(FontAwesomeIcon icon, Vector4 colour, string tooltip)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, colour))
        {
            ImGui.TextUnformatted(icon.ToIconString());
        }
        ElezenImgui.AttachTooltip(tooltip);
        ImGui.SameLine(0f, 5f * ImGuiHelpers.GlobalScale);
    }

    private void RenderSegment(ChatSegment segment, float width)
    {
        switch (segment)
        {
            case TextSegment text:
                RenderText(text.Text);
                break;
            case BbSegment bb:
                _bbCodeRenderer.Render(bb.Markup, width);
                break;
            case LinkSegment link:
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedBlue))
                {
                    ImGui.TextUnformatted(link.Text);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                }

                if (ImGui.IsItemClicked())
                {
                    _mediator.Publish(new OpenBbCodeLinkPopupMessage(link.Address));
                }
                break;
            case MentionSegment mention:
                var self = string.Equals(mention.Uid, _identityResolver.SelfUid, StringComparison.Ordinal);
                using (ImRaii.PushColor(ImGuiCol.Text, self ? ImGuiColors.DalamudYellow : SnowcloakColours.OnlineBlue))
                {
                    ImGui.TextUnformatted("@" + _identityResolver.ResolveName(mention.Uid));
                }
                break;
            case EmoteSegment emote:
                ImGui.TextUnformatted($":{emote.Name}:");
                break;
        }
    }

    private static void RenderText(string text)
    {
        ImGui.TextUnformatted(text);
    }
}
