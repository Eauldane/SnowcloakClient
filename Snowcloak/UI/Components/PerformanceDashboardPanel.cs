using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Configurations;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI.Components;

internal sealed class PerformanceDashboardPanel
{
    private readonly GpuMemoryBudgetService _gpuMemoryBudgetService;
    private readonly PairManager _pairManager;
    private readonly PlayerPerformanceConfigService _performanceConfigService;
    private readonly PlayerPerformanceService _playerPerformanceService;

    public PerformanceDashboardPanel(
        PairManager pairManager,
        PlayerPerformanceService playerPerformanceService,
        PlayerPerformanceConfigService performanceConfigService,
        GpuMemoryBudgetService gpuMemoryBudgetService)
    {
        _pairManager = pairManager;
        _playerPerformanceService = playerPerformanceService;
        _performanceConfigService = performanceConfigService;
        _gpuMemoryBudgetService = gpuMemoryBudgetService;
    }

    public void Draw()
    {
        var allKnownPairs = _pairManager.DirectPairs
            .Concat(_pairManager.GroupPairs.SelectMany(entry => entry.Value))
            .Distinct()
            .ToList();
        var onlinePairs = _pairManager.GetOnlineUserPairs();
        var visiblePairs = onlinePairs.Where(pair => pair.IsVisible).ToList();
        var autoBlockedPairs = onlinePairs.Where(pair => pair.IsAutoPaused).ToList();
        var gpuBudget = _gpuMemoryBudgetService.GetCurrentBudget();
        var crowdPrioritySnapshot = _playerPerformanceService.GetCrowdPrioritySnapshot();
        var config = _performanceConfigService.Current;

        var visibleVramBytes = visiblePairs.Sum(GetDisplayVramBytes);
        var visibleTriangleCount = visiblePairs.Sum(GetDisplayTriangleCount);
        var topVramPairs = GetTopPairs(onlinePairs, static pair => GetDisplayVramBytes(pair));
        var topTrianglePairs = GetTopPairs(onlinePairs, static pair => GetDisplayTriangleCount(pair));

        ElezenImgui.ShowIcon(FontAwesomeIcon.ChartBar);
        ImGui.SameLine();
        ImGui.TextUnformatted("Performance");
        ElezenImgui.AttachTooltip("Local-only dashboard for your current visible load, local GPU VRAM, and syncshell crowd-control pressure.");

        DrawOverviewGrid(allKnownPairs.Count, visiblePairs.Count, autoBlockedPairs.Count, visibleVramBytes, visibleTriangleCount);

        ImGui.Spacing();
        DrawGpuBudget(gpuBudget, visibleVramBytes);

        ImGui.Spacing();
        DrawThresholdSummary(config, crowdPrioritySnapshot);

        ImGui.Spacing();
        DrawTopOffenders(topVramPairs, topTrianglePairs);

        if (autoBlockedPairs.Count > 0)
        {
            ImGui.Spacing();
            DrawAutoBlocked(autoBlockedPairs);
        }
    }

    private static void DrawOverviewGrid(int trackedPairs, int visiblePairs, int autoBlockedPairs, long visibleVramBytes, long visibleTriangleCount)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg;
        if (!ImGui.BeginTable("performance-overview", 3, flags))
        {
            return;
        }

        DrawMetricCell("Tracked", trackedPairs.ToString(CultureInfo.InvariantCulture), "Known direct and syncshell members", ElezenColours.SnowcloakBlue);
        DrawMetricCell("Visible", visiblePairs.ToString(CultureInfo.InvariantCulture), "Currently on screen", ElezenColours.SnowcloakBlue);
        DrawMetricCell("Auto-Blocked", autoBlockedPairs.ToString(CultureInfo.InvariantCulture), "Held by local performance rules", ImGuiColors.DalamudRed);

        DrawMetricCell("Visible VRAM", UiSharedService.ByteToString(visibleVramBytes, true), "Applied or reported visible load", SyncshellBudgetService.GetMetricColor(SyncshellBudgetMetric.Vram));
        DrawMetricCell("Visible Tris", SyncshellBudgetService.FormatTriangles(visibleTriangleCount), "Applied or reported visible geometry", SyncshellBudgetService.GetMetricColor(SyncshellBudgetMetric.Triangles));
        DrawMetricCell("Priority Holds", string.Empty, "Syncshell crowd control appears below", ImGuiColors.DalamudGrey);

        ImGui.EndTable();
    }

    private static void DrawMetricCell(string label, string value, string hint, Vector4 valueColor)
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);

        if (!string.IsNullOrEmpty(value))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, valueColor))
            {
                ImGui.TextUnformatted(value);
            }
        }
        else
        {
            ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight()));
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped(hint);
        }
    }

    private static void DrawGpuBudget(GpuMemoryBudgetSnapshot? gpuBudget, long visibleVramBytes)
    {
        ImGui.TextUnformatted("Local GPU Memory");
        ImGui.Separator();

        if (gpuBudget == null)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted("No local GPU VRAM data is currently available.");
            }
            return;
        }

        var totalBytes = gpuBudget.TotalBytes > 0 ? gpuBudget.TotalBytes : gpuBudget.BudgetBytes;
        var usageRatio = totalBytes <= 0
            ? 0f
            : Math.Clamp((float)visibleVramBytes / totalBytes, 0f, 1f);
        var label = $"Snowcloak visible VRAM {UiSharedService.ByteToString(visibleVramBytes, true)} / {UiSharedService.ByteToString(totalBytes, true)}";

        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, SyncshellBudgetService.GetUsageColor(usageRatio)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0.11f, 0.11f, 0.13f, 1f)))
        {
            ImGui.ProgressBar(usageRatio, new Vector2(-1, 0), label);
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
        {
            ImGui.TextWrapped($"Snowcloak visible load: {UiSharedService.ByteToString(visibleVramBytes, true)} | Local GPU VRAM: {UiSharedService.ByteToString(totalBytes, true)} | Adapter: {gpuBudget.AdapterName}");
        }
    }

    private static void DrawThresholdSummary(PlayerPerformanceConfig config, CrowdPrioritySnapshot crowdPrioritySnapshot)
    {
        ImGui.TextUnformatted("Crowd Control");
        ImGui.Separator();
        ImGui.TextUnformatted(crowdPrioritySnapshot.Enabled ? "Enabled" : "Disabled");
        ImGui.TextUnformatted($"Visible syncshell members: {crowdPrioritySnapshot.VisibleMembers}/{config.CrowdPriorityVisibleMembersThreshold}");
        ImGui.TextUnformatted($"Syncshell VRAM: {UiSharedService.ByteToString(crowdPrioritySnapshot.ActiveVramBytes, true)} / {UiSharedService.ByteToString(config.CrowdPriorityVRAMThresholdMiB * 1024L * 1024L, true)}");
        ImGui.TextUnformatted($"Syncshell triangles: {SyncshellBudgetService.FormatTriangles(crowdPrioritySnapshot.ActiveTriangleCount)} / {SyncshellBudgetService.FormatTriangles(config.CrowdPriorityTrianglesThresholdThousands * 1000L)}");
        if (crowdPrioritySnapshot.CrowdPausedMembers > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
            {
                ImGui.TextWrapped($"{crowdPrioritySnapshot.CrowdPausedMembers} visible syncshell member(s) are currently held by crowd control.");
            }
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped("Crowd control only applies to visible syncshell members and restores them automatically when pressure drops.");
            }
        }
    }

    private static void DrawTopOffenders(IReadOnlyList<Pair> topVramPairs, IReadOnlyList<Pair> topTrianglePairs)
    {
        if (!ImGui.BeginTable("performance-offenders", 2, ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableNextColumn();
        DrawPairColumn("Top VRAM", topVramPairs, SyncshellBudgetMetric.Vram);

        ImGui.TableNextColumn();
        DrawPairColumn("Top Triangles", topTrianglePairs, SyncshellBudgetMetric.Triangles);

        ImGui.EndTable();
    }

    private static void DrawPairColumn(string title, IReadOnlyList<Pair> pairs, SyncshellBudgetMetric metric)
    {
        ImGui.TextUnformatted(title);
        ImGui.Separator();

        if (pairs.Count == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted("No current offenders");
            }
            return;
        }

        for (var index = 0; index < pairs.Count; index++)
        {
            var pair = pairs[index];
            using var rowId = ImRaii.PushId($"{title}-{pair.UserData.UID}");
            DrawPairRow(index + 1, pair, metric);
            if (index < pairs.Count - 1)
            {
                ImGui.Spacing();
            }
        }
    }

    private static void DrawPairRow(int rank, Pair pair, SyncshellBudgetMetric metric)
    {
        var displayName = GetDisplayName(pair);
        var metricValue = metric switch
        {
            SyncshellBudgetMetric.Vram when GetDisplayVramBytes(pair) > 0 => pair.LastAppliedApproximateVRAMBytes >= 0
                ? UiSharedService.ByteToString(GetDisplayVramBytes(pair), true)
                : $"{UiSharedService.ByteToString(GetDisplayVramBytes(pair), true)} reported",
            SyncshellBudgetMetric.Triangles when GetDisplayTriangleCount(pair) > 0 => pair.LastAppliedDataTris >= 0
                ? SyncshellBudgetService.FormatTriangles(GetDisplayTriangleCount(pair))
                : $"{SyncshellBudgetService.FormatTriangles(GetDisplayTriangleCount(pair))} reported",
            _ => string.Empty
        };

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{rank}. {displayName}");
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
            ImGui.TextWrapped(BuildContext(pair));
        }

        ElezenImgui.AttachTooltip(BuildTooltip(pair));
    }

    private static void DrawAutoBlocked(IReadOnlyList<Pair> autoBlockedPairs)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, SyncshellBudgetService.GetMetricColor(SyncshellBudgetMetric.AutoBlocked)))
        {
            ImGui.TextUnformatted("Auto-Blocked Members");
        }
        ImGui.Separator();

        foreach (var pair in autoBlockedPairs
                     .OrderByDescending(GetDisplayVramBytes)
                     .ThenByDescending(GetDisplayTriangleCount)
                     .ThenBy(pair => pair.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
                     .Take(SyncshellBudgetService.DefaultTopMemberCount))
        {
            using var rowId = ImRaii.PushId($"blocked-{pair.UserData.UID}");
            ImGui.TextUnformatted(GetDisplayName(pair));
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextWrapped(BuildAutoBlockedContext(pair));
            }

            ElezenImgui.AttachTooltip(BuildTooltip(pair));
        }

        var hiddenCount = autoBlockedPairs.Count - SyncshellBudgetService.DefaultTopMemberCount;
        if (hiddenCount > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                ImGui.TextUnformatted($"+ {hiddenCount} more auto-blocked member(s)");
            }
        }
    }

    private static IReadOnlyList<Pair> GetTopPairs(IReadOnlyCollection<Pair> pairs, Func<Pair, long> metricSelector)
    {
        return pairs
            .Where(pair => metricSelector(pair) > 0 && (pair.IsVisible || pair.IsAutoPaused))
            .OrderByDescending(metricSelector)
            .ThenBy(pair => pair.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
            .Take(SyncshellBudgetService.DefaultTopMemberCount)
            .ToList();
    }

    private static string BuildContext(Pair pair)
    {
        var parts = new List<string>
        {
            pair.IsVisible ? "Visible" : "Online"
        };

        if (pair.UserPair != null)
        {
            parts.Add("direct pair");
        }

        if (pair.GroupPair.Count > 0)
        {
            parts.Add(pair.UserPair != null ? "syncshell member" : "syncshell only");
        }

        if (pair.IsAutoPaused)
        {
            parts.Add("auto-blocked");
        }

        return string.Join(" | ", parts);
    }

    private static string BuildAutoBlockedContext(Pair pair)
    {
        var parts = new List<string>();
        if (GetDisplayVramBytes(pair) > 0)
        {
            parts.Add($"VRAM {UiSharedService.ByteToString(GetDisplayVramBytes(pair), true)}");
        }

        if (GetDisplayTriangleCount(pair) > 0)
        {
            parts.Add($"tris {SyncshellBudgetService.FormatTriangles(GetDisplayTriangleCount(pair))}");
        }

        return parts.Count > 0
            ? string.Join(" | ", parts)
            : "Blocked by local performance rules";
    }

    private static string BuildTooltip(Pair pair)
    {
        var lines = new List<string> { pair.UserData.AliasOrUID };
        if (!string.IsNullOrEmpty(pair.PlayerName) && !string.Equals(pair.PlayerName, pair.UserData.AliasOrUID, StringComparison.Ordinal))
        {
            lines.Add($"Character: {pair.PlayerName}");
        }

        lines.Add($"Status: {(pair.IsVisible ? "Visible" : pair.IsOnline ? "Online" : "Offline")}");

        if (GetDisplayVramBytes(pair) > 0)
        {
            lines.Add($"{(pair.LastAppliedApproximateVRAMBytes >= 0 ? "Applied" : "Reported")} VRAM: {UiSharedService.ByteToString(GetDisplayVramBytes(pair), true)}");
        }

        if (GetDisplayTriangleCount(pair) > 0)
        {
            lines.Add($"{(pair.LastAppliedDataTris >= 0 ? "Applied" : "Reported")} triangles: {SyncshellBudgetService.FormatTriangles(GetDisplayTriangleCount(pair))}");
        }

        if (pair.IsAutoPaused && !string.IsNullOrEmpty(pair.AutoPauseTooltip))
        {
            lines.Add(string.Empty);
            lines.Add(pair.AutoPauseTooltip);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string GetDisplayName(Pair pair)
    {
        var displayName = pair.GetNoteOrName();
        return string.IsNullOrEmpty(displayName) ? pair.UserData.AliasOrUID : displayName;
    }

    private static long GetDisplayVramBytes(Pair pair)
    {
        return pair.LastAppliedApproximateVRAMBytes >= 0
            ? pair.LastAppliedApproximateVRAMBytes
            : pair.LastReportedApproximateVRAMBytes ?? 0;
    }

    private static long GetDisplayTriangleCount(Pair pair)
    {
        return pair.LastAppliedDataTris >= 0
            ? pair.LastAppliedDataTris
            : pair.LastReportedTriangles ?? 0;
    }
}
