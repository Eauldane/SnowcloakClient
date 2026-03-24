using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Dto.Group;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using System.Numerics;

namespace Snowcloak.UI.Components;

internal sealed class SyncshellBudgetPanel
{
    private readonly SyncshellBudgetService _budgetService;

    public SyncshellBudgetPanel(SyncshellBudgetService budgetService)
    {
        _budgetService = budgetService;
    }

    public void Draw(GroupFullInfoDto group, IReadOnlyCollection<Pair> groupPairs)
    {
        var snapshot = _budgetService.GetSnapshot(group, groupPairs);
        using var id = ImRaii.PushId($"syncshell-budget-{group.GID}");

        ElezenImgui.ShowIcon(FontAwesomeIcon.ChartBar);
        ImGui.SameLine();
        ImGui.TextUnformatted("Shell Budget");
        ElezenImgui.AttachTooltip("Local-only summary based on what your client currently sees and has applied.");

        DrawMetricGrid(snapshot);

        ImGui.Spacing();
        DrawTopOffenders(snapshot);

        if (snapshot.AutoBlockedMembers.Count > 0)
        {
            ImGui.Spacing();
            DrawAutoBlockedMembers(snapshot);
        }
    }

    private static void DrawMetricGrid(SyncshellBudgetSnapshot snapshot)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg;
        if (!ImGui.BeginTable("budget-metrics", 3, flags))
        {
            return;
        }

        DrawMetricCell("Visible", snapshot.VisibleCount.ToString(), "On screen now", ElezenColours.SnowcloakBlue);
        DrawMetricCell("Online", snapshot.OnlineCount.ToString(), "Nearby, not visible", ImGuiColors.ParsedGreen);
        DrawMetricCell("Offline", snapshot.OfflineCount.ToString(), "Offline or paused", ImGuiColors.DalamudGrey);

        DrawMetricCell("Auto-Blocked", snapshot.AutoBlockedCount.ToString(), "Local threshold holds", ImGuiColors.DalamudRed);
        DrawMetricCell("Visible VRAM", UiSharedService.ByteToString(snapshot.VisibleVramBytes, true), "Currently applied visible load", SyncshellBudgetService.GetMetricColor(SyncshellBudgetMetric.Vram));
        DrawMetricCell("Visible Tris", SyncshellBudgetService.FormatTriangles(snapshot.VisibleTriangleCount), "Currently applied visible geometry", SyncshellBudgetService.GetMetricColor(SyncshellBudgetMetric.Triangles));

        ImGui.EndTable();
    }

    private static void DrawMetricCell(string label, string value, string hint, Vector4 valueColor)
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        using (ImRaii.PushColor(ImGuiCol.Text, valueColor))
        {
            ImGui.TextUnformatted(value);
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(hint);
        }
    }

    private static void DrawTopOffenders(SyncshellBudgetSnapshot snapshot)
    {
        if (!ImGui.BeginTable("budget-offenders", 2, ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableNextColumn();
        DrawMemberColumn("Top VRAM", snapshot.TopVramMembers, SyncshellBudgetMetric.Vram);

        ImGui.TableNextColumn();
        DrawMemberColumn("Top Triangles", snapshot.TopTriangleMembers, SyncshellBudgetMetric.Triangles);

        ImGui.EndTable();
    }

    private static void DrawMemberColumn(string title, IReadOnlyList<SyncshellBudgetMemberSnapshot> members, SyncshellBudgetMetric metric)
    {
        ImGui.TextUnformatted(title);
        ImGui.Separator();

        if (members.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted("No current offenders");
            }
            return;
        }

        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            using var rowId = ImRaii.PushId($"{metric}-{member.Pair.UserData.UID}");
            DrawMemberRow(index + 1, member, metric);
            if (index < members.Count - 1)
            {
                ImGui.Spacing();
            }
        }
    }

    private static void DrawMemberRow(int rank, SyncshellBudgetMemberSnapshot member, SyncshellBudgetMetric metric)
    {
        var label = $"{rank}. {GetDisplayName(member.Pair)}";
        var metricValue = GetMemberMetricValue(member, metric);
        var context = BuildMemberContext(member, metric);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);

        if (!string.IsNullOrEmpty(metricValue))
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, SyncshellBudgetService.GetMetricColor(metric)))
            {
                ImGui.TextUnformatted(metricValue);
            }
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(context);
        }

        ElezenImgui.AttachTooltip(BuildMemberTooltip(member));
    }

    private static void DrawAutoBlockedMembers(SyncshellBudgetSnapshot snapshot)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, SyncshellBudgetService.GetMetricColor(SyncshellBudgetMetric.AutoBlocked)))
        {
            ImGui.TextUnformatted("Auto-Blocked Members");
        }
        ImGui.Separator();

        foreach (var member in snapshot.AutoBlockedMembers)
        {
            using var rowId = ImRaii.PushId($"blocked-{member.Pair.UserData.UID}");
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(GetDisplayName(member.Pair));
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(BuildBlockedContext(member));
            }

            ElezenImgui.AttachTooltip(BuildMemberTooltip(member));
        }

        var hiddenBlockedCount = snapshot.AutoBlockedCount - snapshot.AutoBlockedMembers.Count;
        if (hiddenBlockedCount > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted($"+ {hiddenBlockedCount} more auto-blocked member(s)");
            }
        }
    }

    private static string GetDisplayName(Pair pair)
    {
        var displayName = pair.GetNoteOrName();
        return string.IsNullOrEmpty(displayName) ? pair.UserData.AliasOrUID : displayName;
    }

    private static string GetMemberMetricValue(SyncshellBudgetMemberSnapshot member, SyncshellBudgetMetric metric)
    {
        return metric switch
        {
            SyncshellBudgetMetric.Vram when member.DisplayVramBytes > 0 => member.UsesReportedVram
                ? $"{UiSharedService.ByteToString(member.DisplayVramBytes, true)} reported"
                : UiSharedService.ByteToString(member.DisplayVramBytes, true),
            SyncshellBudgetMetric.Triangles when member.DisplayTriangleCount > 0 => member.UsesReportedTriangles
                ? $"{SyncshellBudgetService.FormatTriangles(member.DisplayTriangleCount)} reported"
                : SyncshellBudgetService.FormatTriangles(member.DisplayTriangleCount),
            _ => string.Empty,
        };
    }

    private static string BuildMemberContext(SyncshellBudgetMemberSnapshot member, SyncshellBudgetMetric metric)
    {
        var parts = new List<string> { GetPresenceLabel(member.Presence) };

        if (metric == SyncshellBudgetMetric.Vram && member.UsesReportedVram)
        {
            parts.Add("reported before apply");
        }

        if (metric == SyncshellBudgetMetric.Triangles && member.UsesReportedTriangles)
        {
            parts.Add("reported before apply");
        }

        if (member.IsAutoBlocked)
        {
            parts.Add("auto-blocked");
        }

        return string.Join(" | ", parts);
    }

    private static string BuildBlockedContext(SyncshellBudgetMemberSnapshot member)
    {
        var parts = new List<string>();
        if (member.DisplayVramBytes > 0)
        {
            parts.Add(member.UsesReportedVram
                ? $"reported VRAM {UiSharedService.ByteToString(member.DisplayVramBytes, true)}"
                : $"VRAM {UiSharedService.ByteToString(member.DisplayVramBytes, true)}");
        }

        if (member.DisplayTriangleCount > 0)
        {
            parts.Add(member.UsesReportedTriangles
                ? $"reported tris {SyncshellBudgetService.FormatTriangles(member.DisplayTriangleCount)}"
                : $"tris {SyncshellBudgetService.FormatTriangles(member.DisplayTriangleCount)}");
        }

        return parts.Count > 0
            ? string.Join(" | ", parts)
            : "Blocked by your local performance thresholds";
    }

    private static string BuildMemberTooltip(SyncshellBudgetMemberSnapshot member)
    {
        var lines = new List<string> { member.Pair.UserData.AliasOrUID };

        if (!string.IsNullOrEmpty(member.Pair.PlayerName) && !string.Equals(member.Pair.PlayerName, member.Pair.UserData.AliasOrUID, StringComparison.Ordinal))
        {
            lines.Add($"Character: {member.Pair.PlayerName}");
        }

        lines.Add($"Presence: {GetPresenceLabel(member.Presence)}");

        if (member.DisplayVramBytes > 0)
        {
            lines.Add($"{(member.UsesReportedVram ? "Reported" : "Applied")} VRAM: {UiSharedService.ByteToString(member.DisplayVramBytes, true)}");
        }

        if (member.DisplayTriangleCount > 0)
        {
            lines.Add($"{(member.UsesReportedTriangles ? "Reported" : "Applied")} triangles: {SyncshellBudgetService.FormatTriangles(member.DisplayTriangleCount)}");
        }

        if (member.IsAutoBlocked && !string.IsNullOrEmpty(member.Pair.AutoPauseTooltip))
        {
            lines.Add(string.Empty);
            lines.Add(member.Pair.AutoPauseTooltip);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string GetPresenceLabel(SyncshellMemberPresence presence)
    {
        return presence switch
        {
            SyncshellMemberPresence.Visible => "Visible",
            SyncshellMemberPresence.Online => "Online",
            _ => "Offline/unknown",
        };
    }
}
