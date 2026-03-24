using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using ElezenTools.UI;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.UI;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.Services;

public sealed class SyncshellBudgetService
{
    public const int DefaultTopMemberCount = 3;

    public SyncshellBudgetSnapshot GetSnapshot(GroupFullInfoDto group, IReadOnlyCollection<Pair> groupPairs, int topMemberCount = DefaultTopMemberCount)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(groupPairs);

        var visibleCount = 0;
        var onlineCount = 0;
        var offlineCount = 0;
        var autoBlockedCount = 0;
        var visibleVramBytes = 0L;
        var visibleTriangleCount = 0L;
        var memberSnapshots = new List<SyncshellBudgetMemberSnapshot>(groupPairs.Count);

        foreach (var pair in groupPairs)
        {
            if (!pair.GroupPair.TryGetValue(group, out var groupPairInfo))
            {
                continue;
            }

            var presence = GetPresence(pair, group, groupPairInfo);
            switch (presence)
            {
                case SyncshellMemberPresence.Visible:
                    visibleCount++;
                    break;
                case SyncshellMemberPresence.Online:
                    onlineCount++;
                    break;
                default:
                    offlineCount++;
                    break;
            }

            var isAutoBlocked = pair.IsAutoPaused;
            if (isAutoBlocked)
            {
                autoBlockedCount++;
            }

            var appliedVramBytes = Math.Max(-1L, pair.LastAppliedApproximateVRAMBytes);
            var appliedTriangleCount = Math.Max(-1L, pair.LastAppliedDataTris);
            var displayVramBytes = appliedVramBytes >= 0 ? appliedVramBytes : pair.LastReportedApproximateVRAMBytes ?? -1L;
            var displayTriangleCount = appliedTriangleCount >= 0 ? appliedTriangleCount : pair.LastReportedTriangles ?? -1L;
            var usesReportedVram = appliedVramBytes < 0 && pair.LastReportedApproximateVRAMBytes.HasValue;
            var usesReportedTriangles = appliedTriangleCount < 0 && pair.LastReportedTriangles.HasValue;

            if (presence == SyncshellMemberPresence.Visible)
            {
                if (!pair.IsApplicationBlocked && appliedVramBytes > 0)
                {
                    visibleVramBytes += appliedVramBytes;
                }

                if (!pair.IsApplicationBlocked && appliedTriangleCount > 0)
                {
                    visibleTriangleCount += appliedTriangleCount;
                }
            }

            memberSnapshots.Add(new SyncshellBudgetMemberSnapshot(
                pair,
                presence,
                isAutoBlocked,
                displayVramBytes,
                displayTriangleCount,
                usesReportedVram,
                usesReportedTriangles));
        }

        var topVramMembers = memberSnapshots
            .Where(m => m.DisplayVramBytes > 0 && (m.Presence == SyncshellMemberPresence.Visible || m.IsAutoBlocked))
            .OrderByDescending(m => m.DisplayVramBytes)
            .ThenBy(m => m.Pair.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
            .Take(topMemberCount)
            .ToList();

        var topTriangleMembers = memberSnapshots
            .Where(m => m.DisplayTriangleCount > 0 && (m.Presence == SyncshellMemberPresence.Visible || m.IsAutoBlocked))
            .OrderByDescending(m => m.DisplayTriangleCount)
            .ThenBy(m => m.Pair.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
            .Take(topMemberCount)
            .ToList();

        var autoBlockedMembers = memberSnapshots
            .Where(m => m.IsAutoBlocked)
            .OrderByDescending(m => m.DisplayVramBytes)
            .ThenByDescending(m => m.DisplayTriangleCount)
            .ThenBy(m => m.Pair.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
            .Take(topMemberCount)
            .ToList();

        return new SyncshellBudgetSnapshot(
            visibleCount,
            onlineCount,
            offlineCount,
            autoBlockedCount,
            visibleVramBytes,
            visibleTriangleCount,
            memberSnapshots,
            topVramMembers,
            topTriangleMembers,
            autoBlockedMembers);
    }

    public static string FormatTriangles(long triangleCount)
    {
        if (triangleCount < 0) return "Unknown";
        if (triangleCount >= 1_000_000) return (triangleCount / 1_000_000d).ToString("0.0'M'", CultureInfo.InvariantCulture);
        if (triangleCount >= 1_000) return (triangleCount / 1_000d).ToString("0.0'k'", CultureInfo.InvariantCulture);
        return triangleCount.ToString(CultureInfo.InvariantCulture);
    }

    public static Vector4 GetUsageColor(float usageRatio)
    {
        return Vector4.Lerp(ImGuiColors.HealerGreen, ImGuiColors.DalamudRed, Math.Clamp(usageRatio, 0f, 1f));
    }

    public static Vector4 GetMetricColor(SyncshellBudgetMetric metric)
    {
        return metric switch
        {
            SyncshellBudgetMetric.Vram => ElezenColours.SnowcloakBlue,
            SyncshellBudgetMetric.Triangles => ImGuiColors.ParsedGreen,
            SyncshellBudgetMetric.AutoBlocked => ImGuiColors.DalamudRed,
            _ => ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
        };
    }

    private static SyncshellMemberPresence GetPresence(Pair pair, GroupFullInfoDto group, GroupPairFullInfoDto groupPairInfo)
    {
        bool pausedByYou;
        bool pausedByOther;
        if (pair.UserPair != null)
        {
            pausedByYou = pair.UserPair.OwnPermissions.IsPaused();
            pausedByOther = pair.UserPair.OtherPermissions.IsPaused();
        }
        else
        {
            pausedByYou = group.GroupUserPermissions.IsPaused();
            pausedByOther = groupPairInfo.GroupUserPermissions.IsPaused();
        }

        var showAsOffline = pausedByOther && !pausedByYou;
        if (!showAsOffline && pair.IsVisible)
        {
            return SyncshellMemberPresence.Visible;
        }

        if (!showAsOffline && pair.IsOnline)
        {
            return SyncshellMemberPresence.Online;
        }

        return SyncshellMemberPresence.Offline;
    }
}

public enum SyncshellMemberPresence
{
    Visible,
    Online,
    Offline,
}

public enum SyncshellBudgetMetric
{
    Vram,
    Triangles,
    AutoBlocked,
}

public sealed record SyncshellBudgetMemberSnapshot(
    Pair Pair,
    SyncshellMemberPresence Presence,
    bool IsAutoBlocked,
    long DisplayVramBytes,
    long DisplayTriangleCount,
    bool UsesReportedVram,
    bool UsesReportedTriangles);

public sealed record SyncshellBudgetSnapshot(
    int VisibleCount,
    int OnlineCount,
    int OfflineCount,
    int AutoBlockedCount,
    long VisibleVramBytes,
    long VisibleTriangleCount,
    IReadOnlyList<SyncshellBudgetMemberSnapshot> Members,
    IReadOnlyList<SyncshellBudgetMemberSnapshot> TopVramMembers,
    IReadOnlyList<SyncshellBudgetMemberSnapshot> TopTriangleMembers,
    IReadOnlyList<SyncshellBudgetMemberSnapshot> AutoBlockedMembers);
