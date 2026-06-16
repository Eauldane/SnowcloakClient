using System.Numerics;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using ElezenTools.UI.Mvu;
using Snowcloak.API.Dto.User;
using Snowcloak.Core.Pairing;
using Snowcloak.Services.Pairing;
using Snowcloak.UI.Components;

namespace Snowcloak.UI.PairingAvailability;

public sealed class PairingAvailabilityView : IView<AvailabilityViewState>
{
    private const double HookCycleSeconds = 5.0;
    private const double HookFadeSeconds = 0.75;

    public void Draw(AvailabilityViewState state, IDispatcher dispatch)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(dispatch);

        using var palette = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.065f, 0.095f, 0.34f))
            .Push(ImGuiCol.TableHeaderBg, new Vector4(0.035f, 0.080f, 0.120f, 0.94f));
        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f) * ImGuiHelpers.GlobalScale);
        using var itemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2f * ImGuiHelpers.GlobalScale));

        DrawProfileFilterControls(state, dispatch);

        if (state.TotalCount == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No nearby Frostbrand users are currently open to pairing.");
            if (state.AutoRejectedCount > 0)
                ImGui.TextColored(ImGuiColors.DalamudGrey,
                    string.Format(CultureInfo.InvariantCulture, "({0} nearby players filtered by auto-reject settings)", state.AutoRejectedCount));
            return;
        }

        if (state.VisibleCount == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No nearby Frostbrand users match the current profile filters.");
            return;
        }

        if (state.UseProfileCards)
            DrawPlayerCards(state, dispatch);
        else
            DrawPlayerTable(state, dispatch);
    }

    private static void DrawPlayerTable(AvailabilityViewState state, IDispatcher dispatch)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var footerHeight = ImGui.GetTextLineHeightWithSpacing() + 12f * scale;
        var avail = ImGui.GetContentRegionAvail();
        var tableHeight = MathF.Max(ImGui.GetTextLineHeightWithSpacing() * 2f, avail.Y - footerHeight);
        var cellPadY = 8f * scale;
        var rowHeight = ImGui.GetTextLineHeight() + cellPadY * 2f;

        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(ImGui.GetStyle().CellPadding.X, cellPadY));
        using (var table = ImRaii.Table("pairing-availability-table", 10,
                   ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Resizable,
                   new Vector2(avail.X, tableHeight)))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 0.18f);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 0.12f);
                ImGui.TableSetupColumn("Tagline", ImGuiTableColumnFlags.WidthStretch, 0.24f);
                ImGui.TableSetupColumn("Pronouns", ImGuiTableColumnFlags.WidthStretch, 0.1f);
                ImGui.TableSetupColumn("Game Gender", ImGuiTableColumnFlags.WidthFixed, 85f);
                ImGui.TableSetupColumn("Tribe", ImGuiTableColumnFlags.WidthFixed, 105f);
                ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthFixed, 65f);
                ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("Approach", ImGuiTableColumnFlags.WidthStretch, 0.12f);
                ImGui.TableSetupColumn("Homeworld", ImGuiTableColumnFlags.WidthStretch, 0.12f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();
                ImGuiClip.ClippedDraw(state.VisibleRows, row => DrawPlayer(row, dispatch), rowHeight);
            }
        }

        DrawTableFooter(state.VisibleCount, state.TotalCount);
    }

    private static void DrawTableFooter(int visibleCount, int totalCount)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(min, min with { X = min.X + width },
            Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.40f)), 1f * scale);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
            ImGui.TextUnformatted($"Showing {visibleCount} of {totalCount} Frostbrand users");
    }

    private static void DrawProfileFilterControls(AvailabilityViewState state, IDispatcher dispatch)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var startCursor = ImGui.GetCursorPos();
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        var row1Height = ImGui.GetFrameHeight();
        var fieldHeight = ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.Y * 2f;
        var rowGap = 10f * scale;

        var countText = $"{state.VisibleCount}/{state.TotalCount} Frostbrand users shown";
        if (state.AutoRejectedCount > 0)
            countText += $" ({state.AutoRejectedCount} filtered)";

        // Row 1: view-mode + profile-only checkboxes, count text.
        ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y));
        var useCards = state.UseProfileCards;
        if (ImGui.Checkbox("Profile cards", ref useCards))
            dispatch.Dispatch(new SetUseProfileCardsIntent(useCards));

        ImGui.SameLine(0f, 18f * scale);
        var onlyWithProfiles = state.OnlyWithProfiles;
        if (ImGui.Checkbox("Only with profiles", ref onlyWithProfiles))
            dispatch.Dispatch(new SetOnlyWithProfilesIntent(onlyWithProfiles));

        ImGui.SameLine(0f, 16f * scale);
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
            ImGui.TextUnformatted(countText);

        // Row 2: search + tag filter fields.
        var row2Y = min.Y + row1Height + rowGap;
        var tagWidth = MathF.Min(300f * scale, MathF.Max(170f * scale, width * 0.30f));
        var searchWidth = MathF.Max(170f * scale, width - tagWidth - 10f * scale);
        var transparent = new Vector4(0f, 0f, 0f, 0f);

        var search = state.SearchQuery;
        var searchPos = new Vector2(min.X, row2Y);
        DrawFieldChrome(searchPos, searchWidth, scale);
        using (ImRaii.PushColor(ImGuiCol.FrameBg, transparent).Push(ImGuiCol.FrameBgHovered, transparent).Push(ImGuiCol.FrameBgActive, transparent))
        {
            ImGui.SetCursorScreenPos(searchPos);
            ImGui.SetNextItemWidth(searchWidth);
            if (ImGui.InputTextWithHint("##frostbrand-profile-search", "Search name, profile, race, tribe, class, world...", ref search, 160))
                dispatch.Dispatch(new SetSearchQueryIntent(search));
        }

        var requiredTag = state.TagQuery;
        var tagPos = new Vector2(min.X + searchWidth + 10f * scale, row2Y);
        DrawFieldChrome(tagPos, tagWidth, scale);
        using (ImRaii.PushColor(ImGuiCol.FrameBg, transparent).Push(ImGuiCol.FrameBgHovered, transparent).Push(ImGuiCol.FrameBgActive, transparent))
        {
            ImGui.SetCursorScreenPos(tagPos);
            ImGui.SetNextItemWidth(tagWidth);
            if (ImGui.InputTextWithHint("##frostbrand-profile-tag-filter", "Tag filter...", ref requiredTag, 80))
                dispatch.Dispatch(new SetTagQueryIntent(requiredTag));
        }

        ImGui.SetCursorPos(new Vector2(startCursor.X, startCursor.Y + row1Height + rowGap + fieldHeight + 10f * scale));
    }

    private static void DrawFieldChrome(Vector2 min, float width, float scale)
    {
        var height = ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.Y * 2f;
        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(new Vector4(0.050f, 0.090f, 0.125f, 0.86f)), 3f * scale);
        drawList.AddRect(min, max, Colour.Vector4ToColour(SnowcloakColours.CompactBorderSubtle), 3f * scale, ImDrawFlags.None, 1f * scale);
    }

    private static void DrawPlayerCards(AvailabilityViewState state, IDispatcher dispatch)
    {
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.018f, 0.048f, 0.072f, 0.22f));
        using var listPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(6f, 4f) * ImGuiHelpers.GlobalScale);
        using var child = ImRaii.Child("pairing-availability-cards", Vector2.Zero, false);
        if (!child)
            return;

        foreach (var row in state.VisibleRows)
        {
            DrawPlayerCard(row, dispatch);
            ImGuiHelpers.ScaledDummy(new Vector2(0, 2));
        }
    }

    private static void DrawPlayerCard(AvailabilityRow row, IDispatcher dispatch)
    {
        using var id = ImRaii.PushId($"frostbrand-card-{row.Ident}");
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var startScreen = ImGui.GetCursorScreenPos();
        var fullWidth = ImGui.GetContentRegionAvail().X;
        var padding = new Vector2(14f, 12f) * scale;
        var innerWidth = fullWidth - padding.X * 2f;

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(startScreen + padding);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + innerWidth);

        DrawPlayerCardHeader(row, innerWidth, dispatch);

        DrawDashedSeparator(innerWidth, scale);
        ImGui.Dummy(new Vector2(0f, 3f * scale));

        if (!string.IsNullOrWhiteSpace(row.Profile?.Tagline))
            ImGui.TextWrapped(row.Profile.Tagline);
        else if (!AvailabilityFilter.HasMeaningfulProfile(row))
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No RP profile summary published yet.");

        DrawCardLabelValue("Pronouns:", row.Profile?.Pronouns);
        DrawCardLabelValue("Approach:", row.Profile?.Approachability);
        DrawRotatingHook(row);
        DrawCardTags(row.VisibleTags);

        ImGui.Dummy(new Vector2(0f, 5f * scale));
        DrawCardActions(row, dispatch);

        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var contentMax = ImGui.GetItemRectMax();
        var cardMin = startScreen;
        var cardMax = new Vector2(startScreen.X + fullWidth, contentMax.Y + padding.Y);
        var rounding = 4f * scale;

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(cardMin, cardMax, Colour.Vector4ToColour(new Vector4(0.030f, 0.075f, 0.108f, 0.62f)), rounding);
        drawList.AddRectFilled(cardMin with { X = cardMin.X + rounding }, new Vector2(cardMax.X - rounding, cardMin.Y + 2.5f * scale), Colour.Vector4ToColour(SnowcloakColours.OnlineBlue), 0f);
        drawList.AddRect(cardMin, cardMax, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.55f)), rounding, ImDrawFlags.None, 1f * scale);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(startScreen.X, cardMax.Y));
    }

    private static void DrawCardActions(AvailabilityRow row, IDispatcher dispatch)
    {
        if (DrawCardActionButton(FontAwesomeIcon.User, "View Profile", "profile"))
            dispatch.Dispatch(new ViewProfileIntent(row.Ident));
        ImGui.SameLine();
        if (DrawCardActionButton(FontAwesomeIcon.Handshake, "Pair Request", "pair-request"))
            dispatch.Dispatch(new SendPairRequestIntent(row.Ident));
        ImGui.SameLine();
        if (DrawCardActionButton(FontAwesomeIcon.Search, "Examine", "examine"))
            dispatch.Dispatch(new ExaminePlayerIntent(row.Ident, row.DisplayName));
        ImGui.SameLine();
        if (DrawCardActionButton(FontAwesomeIcon.IdCard, "Plate", "plate"))
            dispatch.Dispatch(new OpenAdventurerPlateIntent(row.Ident, row.DisplayName));
    }

    private static void DrawPlayerCardHeader(AvailabilityRow row, float innerWidth, IDispatcher dispatch)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();

        var badgeSize = DrawJobBadge(row, min, scale);
        var textX = min.X + badgeSize.X + 12f * scale;

        var name = row.DisplayName;
        var nameSize = ImGui.CalcTextSize(name);
        var namePos = new Vector2(textX, min.Y + 2f * scale);
        drawList.AddText(namePos, Colour.Vector4ToColour(Vector4.One), name);

        var metadata = $"{row.GenderText}  |  {row.TribeName}  |  {row.ClassName} {row.LevelText}  |  {row.HomeWorldName}";
        drawList.AddText(new Vector2(textX, namePos.Y + nameSize.Y + 6f * scale), Colour.Vector4ToColour(SnowcloakColours.CompactTextMuted), metadata);

        var status = row.Status;
        var statusSize = ImGui.CalcTextSize(status);
        var statusTextX = min.X + innerWidth - statusSize.X;
        drawList.AddText(new Vector2(statusTextX, namePos.Y), Colour.Vector4ToColour(ImGuiColors.HealerGreen), status);

        ImGui.SetCursorScreenPos(namePos);
        ImGui.Dummy(nameSize);
        DrawContextMenu(row, dispatch);

        ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + badgeSize.Y + 10f * scale));
    }

    private static Vector2 DrawJobBadge(AvailabilityRow row, Vector2 min, float scale)
    {
        var size = new Vector2(48f, 42f) * scale;
        var label = row.ClassName;
        var color = row.ClassColor;
        var textSize = ImGui.CalcTextSize(label);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, min + size, Colour.Vector4ToColour(new Vector4(color.X, color.Y, color.Z, 0.16f)), 4f * scale);
        drawList.AddRect(min, min + size, Colour.Vector4ToColour(new Vector4(color.X, color.Y, color.Z, 0.42f)), 4f * scale, ImDrawFlags.None, 1f * scale);
        drawList.AddText(min + (size - textSize) * 0.5f, Colour.Vector4ToColour(color), label);
        return size;
    }

    private static bool DrawCardActionButton(FontAwesomeIcon icon, string label, string id)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushFont(UiBuilder.IconFont);
        var iconStr = icon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();
        var textSize = ImGui.CalcTextSize(label);
        var gap = 8f * scale;
        var padX = 14f * scale;
        var size = new Vector2(padX * 2f + iconSize.X + gap + textSize.X, 30f * scale);
        var min = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton($"##frostbrand-action-{id}", size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var drawList = ImGui.GetWindowDrawList();

        var fill = active
            ? new Vector4(0.160f, 0.300f, 0.480f, 0.95f)
            : hovered
                ? new Vector4(0.100f, 0.170f, 0.245f, 0.85f)
                : new Vector4(0.050f, 0.100f, 0.150f, 0.62f);
        var border = hovered || active ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactBorderSubtle;
        drawList.AddRectFilled(min, min + size, Colour.Vector4ToColour(fill), 4f * scale);
        drawList.AddRect(min, min + size, Colour.Vector4ToColour(border), 4f * scale, ImDrawFlags.None, 1f * scale);

        var textColor = hovered || active ? Vector4.One : SnowcloakColours.CompactTextMuted;
        var contentWidth = iconSize.X + gap + textSize.X;
        var iconPos = min + new Vector2((size.X - contentWidth) * 0.5f, (size.Y - iconSize.Y) * 0.5f);
        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(iconPos, Colour.Vector4ToColour(textColor), iconStr);
        ImGui.PopFont();
        drawList.AddText(new Vector2(iconPos.X + iconSize.X + gap, min.Y + (size.Y - textSize.Y) * 0.5f), Colour.Vector4ToColour(textColor), label);

        return clicked;
    }

    private static void DrawDashedSeparator(float width, float scale)
    {
        var p = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var color = Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.45f));
        var dash = 5f * scale;
        var gap = 4f * scale;
        var x = p.X;
        var endX = p.X + width;
        while (x < endX)
        {
            var segmentEnd = MathF.Min(x + dash, endX);
            drawList.AddLine(new Vector2(x, p.Y), new Vector2(segmentEnd, p.Y), color, 1f * scale);
            x += dash + gap;
        }

        ImGui.Dummy(new Vector2(width, 1f * scale));
    }

    private static void DrawCardLabelValue(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.SameLine();
        ImGui.TextWrapped(value);
    }

    private static void DrawRotatingHook(AvailabilityRow row)
    {
        var hooks = row.Profile?.Hooks
            .Where(hook => !string.IsNullOrWhiteSpace(hook.Title) || !string.IsNullOrWhiteSpace(hook.Description))
            .ToList();
        if (hooks == null || hooks.Count == 0)
            return;

        var (hook, alpha, index) = SelectDisplayedHook(row.Ident, hooks);
        ImGui.Spacing();
        ImGui.TextColored(Colour.WithAlpha(ImGuiColors.DalamudGrey, alpha), hooks.Count == 1 ? "RP Hook:" : $"RP Hook {index + 1}/{hooks.Count}:");
        using (ImRaii.PushColor(ImGuiCol.Text, Colour.WithAlpha(ImGuiColors.HealerGreen, alpha)))
        {
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(hook.Title) ? "Hook" : hook.Title);
        }

        if (!string.IsNullOrWhiteSpace(hook.Description))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Colour.WithAlpha(ImGuiColors.DalamudWhite, alpha)))
            {
                ImGui.TextWrapped(hook.Description);
            }
        }
    }

    private static (CharacterProfileHookDto Hook, float Alpha, int Index) SelectDisplayedHook(string ident, List<CharacterProfileHookDto> hooks)
    {
        if (hooks.Count == 1)
            return (hooks[0], 1f, 0);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var offset = GetStablePhaseOffset(ident);
        var cyclePosition = (now + offset) % (hooks.Count * HookCycleSeconds);
        var index = (int)(cyclePosition / HookCycleSeconds);
        var localPosition = cyclePosition - index * HookCycleSeconds;
        var fadeIn = localPosition / HookFadeSeconds;
        var fadeOut = (HookCycleSeconds - localPosition) / HookFadeSeconds;
        var alpha = (float)Math.Clamp(Math.Min(fadeIn, fadeOut), 0.08, 1.0);
        return (hooks[index], alpha, index);
    }

    private static double GetStablePhaseOffset(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in value)
                hash = (hash ^ character) * 16777619u;
            return hash % 1000 / 1000.0 * HookCycleSeconds;
        }
    }

    private static void DrawCardTags(IReadOnlyList<UserProfileTagDto> tags)
    {
        if (tags.Count == 0)
            return;

        var visibleTags = tags.Take(6).Select(tag => $"{ProfileTagChipRenderer.GetTypeLabel(tag.Type)}: {tag.Value}");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Tags:");
        ImGui.SameLine();
        ImGui.TextWrapped(string.Join("   ", visibleTags));
    }

    private static void DrawPlayer(AvailabilityRow row, IDispatcher dispatch)
    {
        ImGui.TableNextColumn();
        DrawCellContextTarget(row, "character", dispatch);
        ImGui.TextUnformatted(row.DisplayName);
        ImGui.TableNextColumn();
        DrawCellContextTarget(row, "status", dispatch);
        ImGui.TextColored(ImGuiColors.HealerGreen, row.Status);
        ImGui.TableNextColumn();
        DrawCellContextTarget(row, "tagline", dispatch);
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(row.Profile?.Tagline) ? "-" : row.Profile.Tagline);
        ImGui.TableNextColumn();
        DrawCellContextTarget(row, "pronouns", dispatch);
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(row.Profile?.Pronouns) ? "-" : row.Profile.Pronouns);
        ImGui.TableNextColumn();
        DrawCellContextTarget(row, "gender", dispatch);
        ImGui.TextUnformatted(row.GenderText);
        ImGui.TableNextColumn();
        DrawCellContextTarget(row, "tribe", dispatch);
        ImGui.TextUnformatted(row.TribeName);
        ImGui.TableNextColumn();
        DrawCellContextTarget(row, "class", dispatch);
        ImGui.TextColored(row.ClassColor, row.ClassName);
        ImGui.TableNextColumn();
        DrawCellContextTarget(row, "level", dispatch);
        ImGui.TextUnformatted(row.LevelText);
        ImGui.TableNextColumn();
        DrawCellContextTarget(row, "approach", dispatch);
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(row.Profile?.Approachability) ? "-" : row.Profile.Approachability);
        ImGui.TableNextColumn();
        DrawCellContextTarget(row, "homeworld", dispatch);
        ImGui.TextUnformatted(row.HomeWorldName);
    }

    private static void DrawCellContextTarget(AvailabilityRow row, string column, IDispatcher dispatch)
    {
        var cursor = ImGui.GetCursorScreenPos();
        var id = $"##availability-cell-{row.Ident}-{column}";
        ImGui.InvisibleButton(id, new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight()));
        DrawContextMenu(row, dispatch, column);
        ImGui.SetCursorScreenPos(cursor);
    }

    private static void DrawContextMenu(AvailabilityRow row, IDispatcher dispatch, string? popupScope = null)
    {
        var worldName = string.Equals(row.HomeWorldName, "-", StringComparison.Ordinal) ? string.Empty : row.HomeWorldName;
        var scale = ImGuiHelpers.GlobalScale;

        using var colors = ImRaii.PushColor(ImGuiCol.PopupBg, SnowcloakColours.CompactPanel)
            .Push(ImGuiCol.Border, SnowcloakColours.CompactBorderSubtle)
            .Push(ImGuiCol.Text, Vector4.One);
        using var styleVars = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(8f, 8f) * scale)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 3f * scale))
            .Push(ImGuiStyleVar.PopupRounding, 5f * scale)
            .Push(ImGuiStyleVar.WindowBorderSize, 1f);

        using var popupContext = ImRaii.ContextPopupItem($"##SCPopupCX-{row.Ident}-{popupScope ?? "default"}");
        if (!popupContext)
            return;

        var menuWidth = ComputeContextMenuWidth(row, worldName, scale);
        DrawContextMenuHeader(row, worldName, menuWidth, scale);

        if (DrawContextMenuItem(FontAwesomeIcon.Search, "Examine", "examine", menuWidth))
            dispatch.Dispatch(new ExaminePlayerIntent(row.Ident, row.DisplayName));
        if (DrawContextMenuItem(FontAwesomeIcon.IdCard, "Adventurer Plate", "plate", menuWidth))
            dispatch.Dispatch(new OpenAdventurerPlateIntent(row.Ident, row.DisplayName));
        if (DrawContextMenuItem(FontAwesomeIcon.User, "View Snowcloak Profile", "profile", menuWidth))
            dispatch.Dispatch(new ViewProfileIntent(row.Ident));
        if (DrawContextMenuItem(FontAwesomeIcon.Handshake, "Send Snowcloak Pair Request", "pair-request", menuWidth))
            dispatch.Dispatch(new SendPairRequestIntent(row.Ident));
    }

    private static readonly string[] ContextMenuLabels = { "Examine", "Adventurer Plate", "View Snowcloak Profile", "Send Snowcloak Pair Request" };
    private const float ContextMenuLeftPad = 6f;
    private const float ContextMenuIconSlot = 20f;
    private const float ContextMenuGap = 10f;
    private const float ContextMenuRightPad = 14f;

    private static float ComputeContextMenuWidth(AvailabilityRow row, string worldName, float scale)
    {
        var maxText = 0f;
        foreach (var label in ContextMenuLabels)
            maxText = MathF.Max(maxText, ImGui.CalcTextSize(label).X);

        var itemsWidth = (ContextMenuLeftPad + ContextMenuIconSlot + ContextMenuGap + ContextMenuRightPad) * scale + maxText;

        var headerWidth = ImGui.CalcTextSize(row.DisplayName).X;
        if (!string.IsNullOrWhiteSpace(worldName))
            headerWidth += 6f * scale + ImGui.CalcTextSize(worldName).X;
        headerWidth += ContextMenuRightPad * scale;

        return MathF.Max(itemsWidth, headerWidth);
    }

    private static void DrawContextMenuHeader(AvailabilityRow row, string worldName, float menuWidth, float scale)
    {
        ImGui.TextUnformatted(row.DisplayName);
        if (!string.IsNullOrWhiteSpace(worldName))
        {
            ImGui.SameLine(0f, 6f * scale);
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, worldName);
        }

        ImGuiHelpers.ScaledDummy(new Vector2(0f, 2f));
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(p, p with { X = p.X + menuWidth },
            Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.55f)), 1f * scale);
        ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));
    }

    private static bool DrawContextMenuItem(FontAwesomeIcon icon, string label, string id, float menuWidth)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushFont(UiBuilder.IconFont);
        var iconStr = icon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();
        var textSize = ImGui.CalcTextSize(label);
        var leftPad = ContextMenuLeftPad * scale;
        var iconSlot = ContextMenuIconSlot * scale;
        var gap = ContextMenuGap * scale;
        var rowHeight = ImGui.GetTextLineHeight() + 9f * scale;
        var size = new Vector2(menuWidth, rowHeight);
        var min = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton($"##ctx-{id}", size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        if (hovered)
            drawList.AddRectFilled(min, min + size, Colour.Vector4ToColour(new Vector4(0.100f, 0.170f, 0.245f, 0.85f)), 4f * scale);

        var color = hovered ? Vector4.One : SnowcloakColours.CompactTextMuted;
        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(new Vector2(min.X + leftPad + (iconSlot - iconSize.X) * 0.5f, min.Y + (rowHeight - iconSize.Y) * 0.5f), Colour.Vector4ToColour(color), iconStr);
        ImGui.PopFont();
        drawList.AddText(new Vector2(min.X + leftPad + iconSlot + gap, min.Y + (rowHeight - textSize.Y) * 0.5f), Colour.Vector4ToColour(color), label);

        if (clicked)
            ImGui.CloseCurrentPopup();
        return clicked;
    }

}
