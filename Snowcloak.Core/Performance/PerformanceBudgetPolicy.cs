namespace Snowcloak.Core.Performance;

public sealed record AutoPausePolicySettings(
    bool Enabled,
    bool IgnoreDirectPairs,
    int VramThresholdMiB,
    int TriangleThresholdThousands,
    int FallbackVramThresholdMiB,
    int FallbackTriangleThreshold);

public sealed record PairUsageContext(
    bool IsDirectPair,
    bool IsWhitelisted,
    long? VramBytes,
    long? TriangleCount);

public sealed record AutoPauseThresholds(
    long VramBytes,
    long TriangleCount);

public sealed record AutoPauseDecision(
    bool ShouldPauseVram,
    bool ShouldPauseTriangles,
    bool ShouldClearExisting,
    AutoPauseThresholds Thresholds)
{
    public bool Passed => !ShouldPauseVram && !ShouldPauseTriangles;
}

public sealed record CrowdBudgetThresholds(
    int VisibleMemberCount,
    int VramThresholdMiB,
    int TriangleThresholdThousands);

public sealed record CrowdBudgetUsage(
    int VisibleMemberCount,
    long VramBytes,
    long TriangleCount);

public sealed record CrowdBudgetThresholdState(
    bool VisibleMembersExceeded,
    bool VramExceeded,
    bool TrianglesExceeded)
{
    public bool AnyExceeded => VisibleMembersExceeded || VramExceeded || TrianglesExceeded;
}

public sealed record PerformanceRecommendedDefaults(
    int Version,
    int VramAutoPauseThresholdMiB,
    int TriangleAutoPauseThresholdThousands,
    int CrowdVisibleMembersThreshold,
    int CrowdVramThresholdMiB,
    int CrowdTriangleThresholdThousands,
    bool CrowdPriorityModeEnabled);

public sealed record PerformanceDefaultThresholds(
    int VisibleMembersThreshold,
    int VramThresholdMiB,
    int TriangleThresholdThousands,
    int AutoBlockVramThresholdMiB,
    int AutoBlockTriangleThresholdThousands);

public sealed record PerformanceRecommendedDefaultsResult(
    PerformanceRecommendedDefaults Defaults,
    bool Changed);

public static class PerformanceBudgetPolicy
{
    public const int RecommendedVisibleMembersThreshold = 100;
    public const int RecommendedTrianglesThresholdThousands = 20000;
    public const int FallbackRecommendedVramThresholdMiB = 8192;
    public const int LegacyAutoBlockVramThresholdMiB = 500;
    public const int LegacyAutoBlockTrianglesThresholdThousands = 400;
    public const int LegacyCrowdVisibleMembersThreshold = 20;
    public const int LegacyCrowdVramThresholdMiB = 2048;
    public const int LegacyCrowdTrianglesThresholdThousands = 1500;

    private const double RecommendedVramUsageFraction = 0.75d;

    public static AutoPauseDecision EvaluateAutoPause(AutoPausePolicySettings settings, PairUsageContext usage)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(usage);

        var enabledForPair = settings.Enabled && !(usage.IsDirectPair && settings.IgnoreDirectPairs);
        var thresholds = new AutoPauseThresholds(
            (enabledForPair ? settings.VramThresholdMiB : settings.FallbackVramThresholdMiB) * 1024L * 1024L,
            enabledForPair ? settings.TriangleThresholdThousands * 1000L : settings.FallbackTriangleThreshold);

        if (usage.IsWhitelisted)
        {
            return new AutoPauseDecision(false, false, true, thresholds);
        }

        var shouldPauseVram = usage.VramBytes.HasValue && usage.VramBytes.Value > thresholds.VramBytes;
        var shouldPauseTriangles = usage.TriangleCount.HasValue && usage.TriangleCount.Value > thresholds.TriangleCount;

        return new AutoPauseDecision(
            shouldPauseVram,
            shouldPauseTriangles,
            !enabledForPair && !shouldPauseVram && !shouldPauseTriangles,
            thresholds);
    }

    public static CrowdBudgetThresholdState EvaluateCrowdBudget(CrowdBudgetThresholds thresholds, CrowdBudgetUsage usage)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(usage);

        return new CrowdBudgetThresholdState(
            thresholds.VisibleMemberCount > 0 && usage.VisibleMemberCount > thresholds.VisibleMemberCount,
            thresholds.VramThresholdMiB > 0 && usage.VramBytes > thresholds.VramThresholdMiB * 1024L * 1024L,
            thresholds.TriangleThresholdThousands > 0 && usage.TriangleCount > thresholds.TriangleThresholdThousands * 1000L);
    }

    public static double CalculateCrowdBurden(CrowdBudgetThresholds thresholds, long pairVramBytes, long pairTriangleCount)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        var burden = 0d;
        if (thresholds.VisibleMemberCount > 0)
        {
            burden += 1d / thresholds.VisibleMemberCount;
        }

        if (thresholds.VramThresholdMiB > 0)
        {
            burden += pairVramBytes / (thresholds.VramThresholdMiB * 1024d * 1024d);
        }

        if (thresholds.TriangleThresholdThousands > 0)
        {
            burden += pairTriangleCount / (thresholds.TriangleThresholdThousands * 1000d);
        }

        return burden;
    }

    public static int GetRecommendedVramThresholdMiB(long totalBytes, long budgetBytes, long availableBytes, bool useReservedFraction = true)
    {
        var sourceBytes = totalBytes;
        if (sourceBytes <= 0)
        {
            sourceBytes = budgetBytes;
        }
        if (sourceBytes <= 0)
        {
            sourceBytes = availableBytes;
        }

        if (sourceBytes <= 0)
        {
            return FallbackRecommendedVramThresholdMiB;
        }

        var recommendedBytes = useReservedFraction
            ? (long)Math.Floor(sourceBytes * RecommendedVramUsageFraction)
            : sourceBytes;

        return (int)Math.Clamp(recommendedBytes / (1024L * 1024L), 512L, int.MaxValue);
    }

    public static PerformanceDefaultThresholds GetRecommendedThresholds(long totalBytes, long budgetBytes, long availableBytes)
    {
        return new PerformanceDefaultThresholds(
            RecommendedVisibleMembersThreshold,
            GetRecommendedVramThresholdMiB(totalBytes, budgetBytes, availableBytes),
            RecommendedTrianglesThresholdThousands,
            LegacyAutoBlockVramThresholdMiB,
            LegacyAutoBlockTrianglesThresholdThousands);
    }

    public static PerformanceRecommendedDefaultsResult ApplyRecommendedDefaults(
        PerformanceRecommendedDefaults current,
        int targetVersion,
        long totalBytes,
        long budgetBytes,
        long availableBytes)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.Version >= targetVersion)
        {
            return new PerformanceRecommendedDefaultsResult(current, false);
        }

        var recommendedVramThresholdMiB = GetRecommendedVramThresholdMiB(totalBytes, budgetBytes, availableBytes);
        var legacyRecommendedVramThresholdMiB = GetRecommendedVramThresholdMiB(totalBytes, budgetBytes, availableBytes, useReservedFraction: false);
        var result = current;

        if (current.Version >= 4 && (result.VramAutoPauseThresholdMiB == recommendedVramThresholdMiB
            || result.VramAutoPauseThresholdMiB == legacyRecommendedVramThresholdMiB))
        {
            result = result with { VramAutoPauseThresholdMiB = LegacyAutoBlockVramThresholdMiB };
        }

        if (current.Version >= 4 && result.TriangleAutoPauseThresholdThousands == RecommendedTrianglesThresholdThousands)
        {
            result = result with { TriangleAutoPauseThresholdThousands = LegacyAutoBlockTrianglesThresholdThousands };
        }

        if (result.CrowdVisibleMembersThreshold == LegacyCrowdVisibleMembersThreshold)
        {
            result = result with { CrowdVisibleMembersThreshold = RecommendedVisibleMembersThreshold };
        }

        if (result.CrowdVramThresholdMiB == LegacyCrowdVramThresholdMiB
            || result.CrowdVramThresholdMiB == legacyRecommendedVramThresholdMiB
            || (current.Version < 8 && result.CrowdVramThresholdMiB == FallbackRecommendedVramThresholdMiB))
        {
            result = result with { CrowdVramThresholdMiB = recommendedVramThresholdMiB };
        }

        if (result.CrowdTriangleThresholdThousands == LegacyCrowdTrianglesThresholdThousands)
        {
            result = result with { CrowdTriangleThresholdThousands = RecommendedTrianglesThresholdThousands };
        }

        if (!result.CrowdPriorityModeEnabled)
        {
            result = result with { CrowdPriorityModeEnabled = true };
        }

        if (result.Version != targetVersion)
        {
            result = result with { Version = targetVersion };
        }

        return new PerformanceRecommendedDefaultsResult(result, !result.Equals(current));
    }
}
