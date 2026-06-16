using ElezenTools.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Extensions;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Configurations;
using Snowcloak.Core.Performance;
using Snowcloak.Core.PlayerData;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;
using System.Globalization;

namespace Snowcloak.Services.Performance;

public sealed partial class CrowdPriorityController : DisposableMediatorSubscriberBase
{
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromMilliseconds(500);

    private readonly Lock _sync = new();
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IFrameTickHandle _tick;
    private readonly ILogger<CrowdPriorityController> _logger;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private DateTime _lastEvaluationUtc = DateTime.MinValue;
    private HashSet<uint> _partyMemberIds = [];

    public CrowdPriorityController(
        ILogger<CrowdPriorityController> logger,
        SnowMediator mediator,
        PlayerPerformanceConfigService playerPerformanceConfigService,
        IServiceScopeFactory serviceScopeFactory,
        DalamudUtilService dalamudUtilService,
        IFrameScheduler frameScheduler)
        : base(logger, mediator)
    {
        ArgumentNullException.ThrowIfNull(playerPerformanceConfigService);
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        ArgumentNullException.ThrowIfNull(dalamudUtilService);
        ArgumentNullException.ThrowIfNull(frameScheduler);

        _logger = logger;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _serviceScopeFactory = serviceScopeFactory;
        _dalamudUtilService = dalamudUtilService;
        _tick = frameScheduler.Register("CrowdPriority", TickInterval.EveryMilliseconds(200), TickPriority.Normal, Tick,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
        Mediator.Subscribe<RecalculatePerformanceMessage>(this, _ => Reevaluate(force: true));
        Mediator.Subscribe<DisconnectedMessage>(this, _ => ClearAllAutoPauses());
    }

    public CrowdPrioritySnapshot GetSnapshot()
    {
        var config = _playerPerformanceConfigService.Current;
        var visibleShellPairs = GetVisibleShellPairs()
            .Where(pair => !pair.IsPaused)
            .ToList();
        var activeShellPairs = visibleShellPairs
            .Where(pair => !pair.IsApplicationBlocked)
            .ToList();

        var activeVramBytes = activeShellPairs.Sum(GetEstimatedVisibleVramBytes);
        var activeTriangleCount = activeShellPairs.Sum(GetEstimatedVisibleTriangleCount);
        var thresholdState = GetThresholdState(config, activeShellPairs.Count, activeVramBytes, activeTriangleCount);

        return new CrowdPrioritySnapshot(
            config.CrowdPriorityModeEnabled,
            visibleShellPairs.Count,
            activeShellPairs.Count,
            visibleShellPairs.Count(pair => pair.HasAutoPauseReason(AutoPauseReason.CrowdPriority)),
            activeVramBytes,
            activeTriangleCount,
            thresholdState);
    }

    public void Reevaluate(bool force = false)
    {
        lock (_sync)
        {
            if (!force && DateTime.UtcNow - _lastEvaluationUtc < EvaluationInterval)
            {
                return;
            }

            _lastEvaluationUtc = DateTime.UtcNow;

            var config = _playerPerformanceConfigService.Current;
            if (!config.CrowdPriorityModeEnabled || !HasAnyThresholdEnabled(config))
            {
                ClearAllAutoPauses();
                return;
            }

            var visibleShellPairs = GetVisibleShellPairs()
                .Where(pair => !pair.IsPaused)
                .ToList();
            var activeCandidates = visibleShellPairs
                .Where(pair => !pair.HasBlockingReasonsOtherThanCrowdPriority())
                .ToList();

            foreach (var pair in EnumerateAllGroupPairs().Except(visibleShellPairs))
            {
                ClearAutoPause(pair);
            }

            foreach (var pair in visibleShellPairs.Where(pair => pair.HasBlockingReasonsOtherThanCrowdPriority()))
            {
                ClearAutoPause(pair);
            }

            if (activeCandidates.Count == 0)
            {
                return;
            }

            var activeCount = activeCandidates.Count;
            var activeVramBytes = activeCandidates.Sum(GetEstimatedVisibleVramBytes);
            var activeTriangleCount = activeCandidates.Sum(GetEstimatedVisibleTriangleCount);
            var initialThresholdState = GetThresholdState(config, activeCount, activeVramBytes, activeTriangleCount);

            var keepPairs = new HashSet<Pair>(activeCandidates);
            if (initialThresholdState.AnyExceeded)
            {
                foreach (var candidate in CreateRemovalOrder(config, activeCandidates))
                {
                    if (!GetThresholdState(config, activeCount, activeVramBytes, activeTriangleCount).AnyExceeded)
                    {
                        break;
                    }

                    if (!keepPairs.Remove(candidate.Pair))
                    {
                        continue;
                    }

                    activeCount--;
                    activeVramBytes -= candidate.EstimatedVramBytes;
                    activeTriangleCount -= candidate.EstimatedTriangleCount;
                }
            }

            foreach (var pair in activeCandidates)
            {
                if (!keepPairs.Contains(pair))
                {
                    var classification = Classify(pair, _partyMemberIds);
                    pair.SetAutoPaused(
                        AutoPauseReason.CrowdPriority,
                        BuildTooltip(pair, classification, config, initialThresholdState, activeCandidates.Count,
                            activeCandidates.Sum(GetEstimatedVisibleVramBytes), activeCandidates.Sum(GetEstimatedVisibleTriangleCount)));
                }
                else
                {
                    ClearAutoPause(pair);
                }
            }
        }
    }

    public void ClearAllAutoPauses()
    {
        foreach (var pair in EnumerateAllGroupPairs())
        {
            ClearAutoPause(pair);
        }
    }

    protected override void Dispose(bool disposing)
    {
        _tick.Dispose();
        base.Dispose(disposing);
    }

    private void Tick()
    {
        UpdatePartyMemberCache();
        if (DateTime.UtcNow - _lastEvaluationUtc >= EvaluationInterval)
        {
            Reevaluate(force: true);
        }
    }

    private List<CrowdPriorityCandidate> CreateRemovalOrder(PlayerPerformanceConfig config, IReadOnlyCollection<Pair> activeCandidates)
    {
        var thresholds = new CrowdBudgetThresholds(
            config.CrowdPriorityVisibleMembersThreshold,
            config.CrowdPriorityVRAMThresholdMiB,
            config.CrowdPriorityTrianglesThresholdThousands);

        return activeCandidates
            .Select(pair => new CrowdPriorityCandidate(
                pair,
                Classify(pair, _partyMemberIds),
                GetEstimatedVisibleVramBytes(pair),
                GetEstimatedVisibleTriangleCount(pair),
                PerformanceBudgetPolicy.CalculateCrowdBurden(thresholds, GetEstimatedVisibleVramBytes(pair), GetEstimatedVisibleTriangleCount(pair))))
            .OrderByDescending(candidate => candidate.Classification.Tier)
            .ThenByDescending(candidate => candidate.Burden)
            .ThenByDescending(candidate => candidate.EstimatedVramBytes)
            .ThenByDescending(candidate => candidate.EstimatedTriangleCount)
            .ThenBy(candidate => candidate.Pair.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ClearAutoPause(Pair pair)
    {
        if (!pair.HasAutoPauseReason(AutoPauseReason.CrowdPriority))
        {
            return;
        }

        pair.ClearAutoPaused(AutoPauseReason.CrowdPriority);
        if (pair.IsVisible && !pair.IsPaused && !pair.IsApplicationBlocked)
        {
            pair.ApplyLastReceivedData(forced: true);
        }
    }

    private static bool HasAnyThresholdEnabled(PlayerPerformanceConfig config)
    {
        return config.CrowdPriorityVisibleMembersThreshold > 0
            || config.CrowdPriorityVRAMThresholdMiB > 0
            || config.CrowdPriorityTrianglesThresholdThousands > 0;
    }

    private void UpdatePartyMemberCache()
    {
        _partyMemberIds = _dalamudUtilService.GetPartyPlayerCharacters()
            .Select(member => member.EntityId)
            .Where(id => id != uint.MaxValue)
            .ToHashSet();
    }

    private List<Pair> EnumerateAllGroupPairs()
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var pairManager = scope.ServiceProvider.GetRequiredService<PairManager>();
            return pairManager.GroupPairs
                .SelectMany(entry => entry.Value)
                .Distinct()
                .ToList();
        }
        catch (ObjectDisposedException ex)
        {
            LogSkippingPairEnumeration(_logger, ex);
            return [];
        }
    }

    private IEnumerable<Pair> GetVisibleShellPairs()
    {
        return EnumerateAllGroupPairs().Where(pair => pair.IsVisible);
    }

    private static long GetEstimatedVisibleVramBytes(Pair pair)
    {
        return Math.Max(pair.LastAppliedApproximateVRAMBytes, pair.LastReportedApproximateVRAMBytes ?? 0);
    }

    private static long GetEstimatedVisibleTriangleCount(Pair pair)
    {
        return Math.Max(pair.LastAppliedDataTris, pair.LastReportedTriangles ?? 0);
    }

    private static CrowdPriorityThresholdState GetThresholdState(PlayerPerformanceConfig config, int activeCount, long activeVramBytes, long activeTriangleCount)
    {
        var state = PerformanceBudgetPolicy.EvaluateCrowdBudget(
            new CrowdBudgetThresholds(
                config.CrowdPriorityVisibleMembersThreshold,
                config.CrowdPriorityVRAMThresholdMiB,
                config.CrowdPriorityTrianglesThresholdThousands),
            new CrowdBudgetUsage(activeCount, activeVramBytes, activeTriangleCount));

        return new CrowdPriorityThresholdState(state.VisibleMembersExceeded, state.VramExceeded, state.TrianglesExceeded);
    }

    private static CrowdPriorityClassification Classify(Pair pair, HashSet<uint> partyMemberIds)
    {
        if (pair.UserPair != null)
        {
            return new CrowdPriorityClassification(CrowdPriorityTier.DirectPair, "direct pair");
        }

        if (partyMemberIds.Contains(pair.PlayerCharacterId))
        {
            return new CrowdPriorityClassification(CrowdPriorityTier.PartyMember, "party member");
        }

        if (pair.GroupPair.Keys.Any(group => string.Equals(group.OwnerUID, pair.UserData.UID, StringComparison.Ordinal)))
        {
            return new CrowdPriorityClassification(CrowdPriorityTier.SyncshellOwner, "syncshell owner");
        }

        if (pair.GroupPair.Values.Any(groupPair => groupPair.GroupPairStatusInfo.IsModerator()))
        {
            return new CrowdPriorityClassification(CrowdPriorityTier.SyncshellModerator, "syncshell moderator");
        }

        if (pair.GroupPair.Values.Any(groupPair => groupPair.GroupPairStatusInfo.IsPinned()))
        {
            return new CrowdPriorityClassification(CrowdPriorityTier.PinnedSyncshellMember, "pinned syncshell member");
        }

        return new CrowdPriorityClassification(CrowdPriorityTier.SyncshellMember, "syncshell member");
    }

    private static string BuildTooltip(Pair pair, CrowdPriorityClassification classification, PlayerPerformanceConfig config,
        CrowdPriorityThresholdState thresholdState, int visibleMemberCount, long totalVramBytes, long totalTriangleCount)
    {
        var exceededReasons = new List<string>();
        if (thresholdState.VisibleMembersExceeded && config.CrowdPriorityVisibleMembersThreshold > 0)
        {
            exceededReasons.Add(string.Format(CultureInfo.InvariantCulture,
                "visible syncshell members {0}/{1}",
                visibleMemberCount,
                config.CrowdPriorityVisibleMembersThreshold));
        }

        if (thresholdState.VramExceeded && config.CrowdPriorityVRAMThresholdMiB > 0)
        {
            exceededReasons.Add(string.Format(CultureInfo.InvariantCulture,
                "shell-visible VRAM {0}/{1}",
                ElezenImgui.ByteToString(totalVramBytes, addSuffix: true),
                ElezenImgui.ByteToString(config.CrowdPriorityVRAMThresholdMiB * 1024L * 1024L, addSuffix: true)));
        }

        if (thresholdState.TrianglesExceeded && config.CrowdPriorityTrianglesThresholdThousands > 0)
        {
            exceededReasons.Add(string.Format(CultureInfo.InvariantCulture,
                "shell-visible triangles {0}/{1}",
                SyncshellBudgetService.FormatTriangles(totalTriangleCount),
                SyncshellBudgetService.FormatTriangles(config.CrowdPriorityTrianglesThresholdThousands * 1000L)));
        }

        return string.Format(CultureInfo.InvariantCulture,
            "Local crowd-priority hold: {0} ({1}) is temporarily paused because {2} exceeded your local crowd thresholds. This is local only, not a server pause, and Snowcloak will restore them automatically when pressure drops.",
            pair.UserData.AliasOrUID,
            classification.Label,
            string.Join("; ", exceededReasons));
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Skipping pair enumeration because the service provider is disposed")]
    private static partial void LogSkippingPairEnumeration(ILogger logger, Exception exception);
}

public sealed record CrowdPrioritySnapshot(
    bool Enabled,
    int VisibleMembers,
    int ActiveMembers,
    int CrowdPausedMembers,
    long ActiveVramBytes,
    long ActiveTriangleCount,
    CrowdPriorityThresholdState ThresholdState);

public sealed record CrowdPriorityThresholdState(
    bool VisibleMembersExceeded,
    bool VramExceeded,
    bool TrianglesExceeded)
{
    public bool AnyExceeded => VisibleMembersExceeded || VramExceeded || TrianglesExceeded;
}

internal sealed record CrowdPriorityClassification(CrowdPriorityTier Tier, string Label);

internal sealed record CrowdPriorityCandidate(
    Pair Pair,
    CrowdPriorityClassification Classification,
    long EstimatedVramBytes,
    long EstimatedTriangleCount,
    double Burden);

internal enum CrowdPriorityTier
{
    DirectPair = 0,
    PartyMember = 1,
    SyncshellOwner = 2,
    SyncshellModerator = 3,
    PinnedSyncshellMember = 4,
    SyncshellMember = 5,
}
