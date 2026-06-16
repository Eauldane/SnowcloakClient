using System.Numerics;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.Data;
using ElezenTools.UI;
using ElezenTools.UI.Mvu;
using Snowcloak.Services;
using Snowcloak.Services.Pairing;
using Snowcloak.UI.Handlers;
using Snowcloak.UI.PairingAvailability;

namespace Snowcloak.UI.Components;

public static class PendingPairRequestSection
{
    public static void Draw(IReadOnlyList<PendingPairRequestRow> pendingRequests, IDispatcher dispatch,
        TagHandler? tagHandler, string localisationPrefix, bool indent = false, bool collapsibleWhenNoTag = true)
    {
        ArgumentNullException.ThrowIfNull(pendingRequests);
        ArgumentNullException.ThrowIfNull(dispatch);

        var pending = pendingRequests
            .OrderBy(request => request.RequestedAt)
            .ToList();

        if (pending.Count == 0)
            return;

        var title = $"Pair Requests ({pending.Count})";
        var isOpen = tagHandler?.IsTagOpen(TagHandler.CustomPairRequestsTag) ?? true;
        var usedCollapsingHeader = false;

        if (tagHandler == null && collapsibleWhenNoTag)
        {
            usedCollapsingHeader = true;
            isOpen = ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen);
        }
        else if (tagHandler != null)
        {
            var icon = isOpen ? FontAwesomeIcon.CaretSquareDown : FontAwesomeIcon.CaretSquareRight;
            ElezenImgui.ShowIcon(icon);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                isOpen = !isOpen;
                tagHandler.SetTagOpen(TagHandler.CustomPairRequestsTag, isOpen);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(title);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                isOpen = !isOpen;
                tagHandler.SetTagOpen(TagHandler.CustomPairRequestsTag, isOpen);
            }
        }

        if (!isOpen)
        {
            ImGui.Separator();
            return;
        }

        if (indent)
            ImGui.Indent(20 * ImGuiHelpers.GlobalScale);

        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
        {
            ImGui.TextWrapped("Notes will be auto-filled with the sender's name when you accept.");
        }

        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, 7f * ImGuiHelpers.GlobalScale)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2f * ImGuiHelpers.GlobalScale)))
        {
            foreach (var request in pending)
                DrawRequestRow(request, dispatch);
        }

        if (indent)
            ImGui.Unindent(20 * ImGuiHelpers.GlobalScale);

        DrawSoftSeparator();

        if (!usedCollapsingHeader && tagHandler == null)
            ImGuiHelpers.ScaledDummy(new Vector2(0, 2));
    }

    private static void DrawRequestRow(PendingPairRequestRow request, IDispatcher dispatch)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var metadataText = request.MetadataText;
        var hasMetadata = !string.IsNullOrWhiteSpace(metadataText);
        var nameSize = ImGui.CalcTextSize(request.DisplayName);
        var metaSize = hasMetadata ? ImGui.CalcTextSize(metadataText) : Vector2.Zero;
        var buttonSize = DrawPairBase.RowActionButtonSize;
        var height = MathF.Max(buttonSize.Y + 8f * scale, nameSize.Y + (hasMetadata ? metaSize.Y + 4f * scale : 0f) + 14f * scale);
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(ImGui.GetContentRegionAvail().X, height);
        var drawList = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        var fill = hovered
            ? new Vector4(0.075f, 0.130f, 0.185f, 0.42f)
            : new Vector4(0.030f, 0.070f, 0.100f, 0.10f);

        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(fill), 0f);
        drawList.AddLine(min with { Y = max.Y }, max, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.18f)), 1f * scale);

        using var id = ImRaii.PushId(request.RequestId.ToString());
        var leftX = min.X + 4f * scale;
        var textX = leftX;
        if (request.CharacterSnapshot is { ClassJobId: > 0 } snapshot)
        {
            var badgeSize = DrawJobBadge(snapshot, min + new Vector2(4f * scale, (height - 24f * scale) * 0.5f));
            textX += badgeSize.X + 9f * scale;
        }

        var buttonsWidth = buttonSize.X * 2f + ImGui.GetStyle().ItemSpacing.X;
        var textWidth = MathF.Max(1f, max.X - textX - buttonsWidth - 12f * scale);
        ImGui.SetCursorScreenPos(new Vector2(textX, min.Y + (height - nameSize.Y - (hasMetadata ? metaSize.Y + 4f * scale : 0f)) * 0.5f));
        ImGui.PushTextWrapPos(ImGui.GetCursorScreenPos().X + textWidth);
        ImGui.TextUnformatted(request.DisplayName);
        ImGui.PopTextWrapPos();

        if (request.ShowAlias)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
                ImGui.TextUnformatted($"({request.AliasOrUid})");
        }

        if (hasMetadata)
        {
            ImGui.SetCursorScreenPos(new Vector2(textX, ImGui.GetCursorScreenPos().Y + 3f * scale));
            using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
                ImGui.TextUnformatted(metadataText);
        }

        if (hovered)
            ImGui.SetTooltip($"Requested at {request.RequestedAt:HH:mm:ss}");

        ImGui.SetCursorScreenPos(new Vector2(max.X - buttonsWidth, min.Y + (height - buttonSize.Y) * 0.5f));
        if (DrawPairBase.DrawRowActionButton(FontAwesomeIcon.UserPlus, "accept", SnowcloakColours.OnlineBlue))
            dispatch.Dispatch(new RespondPairRequestIntent(request.RequestId, true));

        ImGui.SameLine();
        if (DrawPairBase.DrawRowActionButton(FontAwesomeIcon.Times, "reject", SnowcloakColours.CompactTextMuted))
            dispatch.Dispatch(new RespondPairRequestIntent(request.RequestId, false));

        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y + 2f * scale));
    }

    private static Vector2 DrawJobBadge(PairRequesterCharacterSnapshot snapshot, Vector2 min)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var job = ElezenData.Jobs.GetById(snapshot.ClassJobId);
        var label = string.IsNullOrWhiteSpace(job?.Abbreviation)
            ? snapshot.ClassJobId.ToString(CultureInfo.InvariantCulture)
            : job.Value.Abbreviation;
        var textSize = ImGui.CalcTextSize(label);
        var size = new Vector2(MathF.Max(34f * scale, textSize.X + 12f * scale), 24f * scale);
        var color = job?.ClassColour ?? SnowcloakColours.CompactTextMuted;
        var fill = new Vector4(color.X, color.Y, color.Z, 0.18f);

        ImGui.GetWindowDrawList().AddRectFilled(min, min + size, Colour.Vector4ToColour(fill), 3f * scale);
        ImGui.GetWindowDrawList().AddText(min + (size - textSize) * 0.5f, Colour.Vector4ToColour(color), label);
        return size;
    }

    private static void DrawSoftSeparator()
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min with { X = min.X + ImGui.GetContentRegionAvail().X };
        ImGui.GetWindowDrawList().AddLine(min, max, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.26f)), 1f * ImGuiHelpers.GlobalScale);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
    }
}
