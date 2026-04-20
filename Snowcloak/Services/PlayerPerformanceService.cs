using Snowcloak.API.Data;
using Snowcloak.API.Data.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Snowcloak.FileCache;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Configurations;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.PlayerData.Pairs;
using System.Globalization;

namespace Snowcloak.Services;

public class PlayerPerformanceService : DisposableMediatorSubscriberBase
{
    public const int CurrentPerformanceConfigVersion = 5;
    public const int RecommendedVisibleMembersThreshold = 100;
    public const int RecommendedTrianglesThresholdThousands = 20000;
    public const int FallbackRecommendedVramThresholdMiB = 8192;
    private const double RecommendedVramUsageFraction = 0.75d;

    // Limits that will still be enforced when no limits are enabled
    public const int MaxVRAMUsageThreshold = 2000; // 2GB
    public const int MaxTriUsageThreshold = 2000000; // 2 million triangles
    private const int LegacyAutoBlockVramThresholdMiB = 500;
    private const int LegacyAutoBlockTrianglesThresholdThousands = 400;
    private const int LegacyCrowdVisibleMembersThreshold = 20;
    private const int LegacyCrowdVramThresholdMiB = 2048;
    private const int LegacyCrowdTrianglesThresholdThousands = 1500;

    private readonly FileCacheManager _fileCacheManager;
    private readonly GpuMemoryBudgetService _gpuMemoryBudgetService;
    private readonly XivDataAnalyzer _xivDataAnalyzer;
    private readonly ILogger<PlayerPerformanceService> _logger;
    private readonly SnowMediator _mediator;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly Lock _crowdPrioritySync = new();
    private readonly Dictionary<string, bool> _warnedForPlayers = new(StringComparer.Ordinal);
    private DateTime _lastCrowdPriorityEvaluationUtc = DateTime.MinValue;
    private HashSet<uint> _partyMemberIds = [];
    private PairManager? _pairManager;
    private static readonly TimeSpan CrowdPriorityEvaluationInterval = TimeSpan.FromMilliseconds(500);

    public PlayerPerformanceService(ILogger<PlayerPerformanceService> logger, SnowMediator mediator,
        ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService, FileCacheManager fileCacheManager,
        XivDataAnalyzer xivDataAnalyzer, IServiceScopeFactory serviceScopeFactory, DalamudUtilService dalamudUtilService,
        GpuMemoryBudgetService gpuMemoryBudgetService)
        : base(logger, mediator)
    {
        _logger = logger;
        _mediator = mediator;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _fileCacheManager = fileCacheManager;
        _xivDataAnalyzer = xivDataAnalyzer;
        _serviceScopeFactory = serviceScopeFactory;
        _dalamudUtilService = dalamudUtilService;
        _gpuMemoryBudgetService = gpuMemoryBudgetService;

        EnsureRecommendedDefaults();

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, _ =>
        {
            UpdatePartyMemberCache();
            if (DateTime.UtcNow - _lastCrowdPriorityEvaluationUtc >= CrowdPriorityEvaluationInterval)
            {
                ReevaluateCrowdPriority(force: true);
            }
        });
        Mediator.Subscribe<RecalculatePerformanceMessage>(this, _ => ReevaluateCrowdPriority(force: true));
        Mediator.Subscribe<DisconnectedMessage>(this, _ => ClearAllCrowdPriorityAutoPauses());
    }

    private void EnsureRecommendedDefaults()
    {
        var config = _playerPerformanceConfigService.Current;
        if (config.Version >= CurrentPerformanceConfigVersion)
        {
            return;
        }

        var recommendedVramThresholdMiB = GetRecommendedVramThresholdMiB();
        var legacyRecommendedVramThresholdMiB = GetRecommendedVramThresholdMiB(useReservedFraction: false);
        var changed = false;

        if (config.Version >= 4 && (config.VRAMSizeAutoPauseThresholdMiB == recommendedVramThresholdMiB
            || config.VRAMSizeAutoPauseThresholdMiB == legacyRecommendedVramThresholdMiB))
        {
            config.VRAMSizeAutoPauseThresholdMiB = LegacyAutoBlockVramThresholdMiB;
            changed = true;
        }
        if (config.Version >= 4 && config.TrisAutoPauseThresholdThousands == RecommendedTrianglesThresholdThousands)
        {
            config.TrisAutoPauseThresholdThousands = LegacyAutoBlockTrianglesThresholdThousands;
            changed = true;
        }

        if (config.CrowdPriorityVisibleMembersThreshold == LegacyCrowdVisibleMembersThreshold)
        {
            config.CrowdPriorityVisibleMembersThreshold = RecommendedVisibleMembersThreshold;
            changed = true;
        }

        if (config.CrowdPriorityVRAMThresholdMiB == LegacyCrowdVramThresholdMiB
            || config.CrowdPriorityVRAMThresholdMiB == legacyRecommendedVramThresholdMiB)
        {
            config.CrowdPriorityVRAMThresholdMiB = recommendedVramThresholdMiB;
            changed = true;
        }

        if (config.CrowdPriorityTrianglesThresholdThousands == LegacyCrowdTrianglesThresholdThousands)
        {
            config.CrowdPriorityTrianglesThresholdThousands = RecommendedTrianglesThresholdThousands;
            changed = true;
        }

        if (!config.CrowdPriorityModeEnabled)
        {
            config.CrowdPriorityModeEnabled = true;
            changed = true;
        }

        if (config.Version != CurrentPerformanceConfigVersion)
        {
            config.Version = CurrentPerformanceConfigVersion;
            changed = true;
        }

        if (changed)
        {
            _playerPerformanceConfigService.Save();
        }
    }

    private int GetRecommendedVramThresholdMiB(bool useReservedFraction = true)
    {
        var budgetSnapshot = _gpuMemoryBudgetService.GetCurrentBudget();
        if (budgetSnapshot == null)
        {
            return FallbackRecommendedVramThresholdMiB;
        }

        var budgetBytes = budgetSnapshot.TotalBytes > 0
            ? budgetSnapshot.TotalBytes
            : budgetSnapshot.BudgetBytes > 0
                ? budgetSnapshot.BudgetBytes
                : budgetSnapshot.AvailableBytes;
        if (budgetBytes <= 0)
        {
            return FallbackRecommendedVramThresholdMiB;
        }

        var recommendedBytes = useReservedFraction
            ? (long)Math.Floor(budgetBytes * RecommendedVramUsageFraction)
            : budgetBytes;

        return (int)Math.Clamp(recommendedBytes / (1024L * 1024L), 512L, int.MaxValue);
    }

    public CrowdPrioritySnapshot GetCrowdPrioritySnapshot()
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
        var thresholdState = GetCrowdPriorityThresholdState(config, activeShellPairs.Count, activeVramBytes, activeTriangleCount);

        return new CrowdPrioritySnapshot(
            config.CrowdPriorityModeEnabled,
            visibleShellPairs.Count,
            activeShellPairs.Count,
            visibleShellPairs.Count(pair => pair.HasAutoPauseReason(Pair.AutoPauseReason.CrowdPriority)),
            activeVramBytes,
            activeTriangleCount,
            thresholdState);
    }

    public void ReevaluateCrowdPriority(bool force = false)
    {
        lock (_crowdPrioritySync)
        {
            if (!force && DateTime.UtcNow - _lastCrowdPriorityEvaluationUtc < CrowdPriorityEvaluationInterval)
            {
                return;
            }

            _lastCrowdPriorityEvaluationUtc = DateTime.UtcNow;

            var config = _playerPerformanceConfigService.Current;
            if (!config.CrowdPriorityModeEnabled || !HasAnyCrowdPriorityThresholdEnabled(config))
            {
                ClearAllCrowdPriorityAutoPauses();
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
                ClearCrowdPriorityAutoPause(pair);
            }

            foreach (var pair in visibleShellPairs.Where(pair => pair.HasBlockingReasonsOtherThanCrowdPriority()))
            {
                ClearCrowdPriorityAutoPause(pair);
            }

            if (activeCandidates.Count == 0)
            {
                return;
            }

            var activeCount = activeCandidates.Count;
            var activeVramBytes = activeCandidates.Sum(GetEstimatedVisibleVramBytes);
            var activeTriangleCount = activeCandidates.Sum(GetEstimatedVisibleTriangleCount);
            var initialThresholdState = GetCrowdPriorityThresholdState(config, activeCount, activeVramBytes, activeTriangleCount);

            var keepPairs = new HashSet<Pair>(activeCandidates);
            if (initialThresholdState.AnyExceeded)
            {
                var removalOrder = activeCandidates
                    .Select(pair => new CrowdPriorityCandidate(
                        pair,
                        ClassifyCrowdPriority(pair, _partyMemberIds),
                        GetEstimatedVisibleVramBytes(pair),
                        GetEstimatedVisibleTriangleCount(pair),
                        GetCrowdPriorityBurden(config, pair)))
                    .OrderByDescending(candidate => candidate.Classification.Tier)
                    .ThenByDescending(candidate => candidate.Burden)
                    .ThenByDescending(candidate => candidate.EstimatedVramBytes)
                    .ThenByDescending(candidate => candidate.EstimatedTriangleCount)
                    .ThenBy(candidate => candidate.Pair.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var candidate in removalOrder)
                {
                    if (!GetCrowdPriorityThresholdState(config, activeCount, activeVramBytes, activeTriangleCount).AnyExceeded)
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
                    var classification = ClassifyCrowdPriority(pair, _partyMemberIds);
                    pair.SetAutoPaused(
                        Pair.AutoPauseReason.CrowdPriority,
                        BuildCrowdPriorityTooltip(pair, classification, config, initialThresholdState, activeCandidates.Count, activeCandidates.Sum(GetEstimatedVisibleVramBytes), activeCandidates.Sum(GetEstimatedVisibleTriangleCount)));
                }
                else
                {
                    ClearCrowdPriorityAutoPause(pair);
                }
            }
        }
    }
    
    public bool CheckReportedThresholds(PairHandler pairHandler, long? reportedTriangles, long? reportedVramBytes)
    {
        var config = _playerPerformanceConfigService.Current;
        var pair = pairHandler.Pair;

        bool isDirect = pair.UserPair != null;
        bool autoPause = config.AutoPausePlayersExceedingThresholds;
        bool notify = isDirect ? config.NotifyAutoPauseDirectPairs : config.NotifyAutoPauseGroupPairs;
        bool wasBlocked = pair.IsApplicationBlocked;
        
        if (autoPause && isDirect && config.IgnoreDirectPairs)
            autoPause = false;

        long triUsageThreshold = config.TrisAutoPauseThresholdThousands * 1000;
        long vramUsageThreshold = config.VRAMSizeAutoPauseThresholdMiB * 1024L * 1024L;

        if (_serverConfigurationManager.IsUserWhitelisted(pair.UserData))
        {
            ClearThresholdAutoPaused(pair);
            return true;
        }

        if (!autoPause)
        {
            triUsageThreshold = MaxTriUsageThreshold;
            vramUsageThreshold = MaxVRAMUsageThreshold * 1024L * 1024L;
            ClearThresholdAutoPaused(pair);
        }

        bool passed = true;

        if (autoPause)
        {
            var hadVramAutoPause = pair.HasAutoPauseReason(Pair.AutoPauseReason.Vram);
            var hadTriangleAutoPause = pair.HasAutoPauseReason(Pair.AutoPauseReason.Triangles);
            var newlyBlockedReasons = new List<string>();

            if (reportedVramBytes.HasValue && reportedVramBytes.Value > vramUsageThreshold)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: reported VRAM usage {0} exceeds limit of {1}MiB.",
                    UiSharedService.ByteToString(reportedVramBytes.Value, addSuffix: true), config.VRAMSizeAutoPauseThresholdMiB);
                pair.SetAutoPaused(Pair.AutoPauseReason.Vram, tooltip);
                passed = false;

                if (!hadVramAutoPause)
                {
                    newlyBlockedReasons.Add(string.Format(CultureInfo.InvariantCulture,
                        "VRAM usage {0}/{1}MiB",
                        UiSharedService.ByteToString(reportedVramBytes.Value, addSuffix: true), config.VRAMSizeAutoPauseThresholdMiB));
                }

                _mediator.Publish(new EventMessage(new Event(pair.PlayerName, pair.UserData, nameof(PlayerPerformanceService), EventSeverity.Warning,
                    string.Format(CultureInfo.InvariantCulture,
                        "Reported VRAM exceeds threshold: ({0}/{1} MiB)",
                        UiSharedService.ByteToString(reportedVramBytes.Value, addSuffix: true), config.VRAMSizeAutoPauseThresholdMiB))));
            }
            else
            {
                pair.ClearAutoPaused(Pair.AutoPauseReason.Vram);
            }

            if (reportedTriangles.HasValue && reportedTriangles.Value > triUsageThreshold)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: reported triangle count {0} exceeds limit of {1}.",
                    reportedTriangles.Value, triUsageThreshold);
                pair.SetAutoPaused(Pair.AutoPauseReason.Triangles, tooltip);
                passed = false;

                if (!hadTriangleAutoPause)
                {
                    newlyBlockedReasons.Add(string.Format(CultureInfo.InvariantCulture,
                        "triangle usage {0}/{1}", reportedTriangles, triUsageThreshold));
                }

                _mediator.Publish(new EventMessage(new Event(pair.PlayerName, pair.UserData, nameof(PlayerPerformanceService), EventSeverity.Warning,
                    string.Format(CultureInfo.InvariantCulture,
                        "Reported triangle usage exceeds threshold: ({0}/{1} triangles)",
                        reportedTriangles, triUsageThreshold))));
            }
            else
            {
                pair.ClearAutoPaused(Pair.AutoPauseReason.Triangles);
            }
            
            if (notify && newlyBlockedReasons.Count > 0 && !wasBlocked && !pair.AutoPauseNotificationShown)
            {
                var reasonSummary = string.Join("; ", newlyBlockedReasons);
                _mediator.Publish(new NotificationMessage(
                    AutoBlockedTitle(pair),
                    AutoBlockedSummaryBody(pair, reasonSummary),
                    Configuration.Models.NotificationType.Warning));
                pair.MarkAutoPauseNotificationShown();
            }
        }

        return passed;
    }

    public async Task<bool> CheckBothThresholds(PairHandler pairHandler, CharacterData charaData)
    {
        bool notPausedAfterVram = ComputeAndAutoPauseOnVRAMUsageThresholds(pairHandler, charaData, [], affect: true);
        if (!notPausedAfterVram) return false;
        bool notPausedAfterTris = await CheckTriangleUsageThresholds(pairHandler, charaData).ConfigureAwait(false);
        if (!notPausedAfterTris) return false;

        return true;
    }

    public async Task<bool> CheckTriangleUsageThresholds(PairHandler pairHandler, CharacterData charaData)
    {
        var config = _playerPerformanceConfigService.Current;
        var pair = pairHandler.Pair;

        long triUsage = 0;

        var moddedModelHashes = charaData.FileReplacements.SelectMany(k => k.Value)
            .Where(p => string.IsNullOrEmpty(p.FileSwapPath) && p.GamePaths.Any(g => g.EndsWith("mdl", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var hash in moddedModelHashes)
        {
            triUsage += await Task.Run(() => _xivDataAnalyzer.GetTrianglesByHash(hash)).ConfigureAwait(false);
        }

        pair.LastAppliedDataTris = triUsage;

        _logger.LogDebug("Calculated Triangle usage for {p}", pairHandler);

        long triUsageThreshold = config.TrisAutoPauseThresholdThousands * 1000;
        bool isDirect = pair.UserPair != null;
        bool autoPause = config.AutoPausePlayersExceedingThresholds;
        bool notify = isDirect ? config.NotifyAutoPauseDirectPairs : config.NotifyAutoPauseGroupPairs;
        bool wasBlocked = pair.IsApplicationBlocked;
        
        if (autoPause && isDirect && config.IgnoreDirectPairs)
            autoPause = false;

        if (_serverConfigurationManager.IsUserWhitelisted(pair.UserData))
        {
            ClearThresholdAutoPaused(pair);
            return true;
        }

        if (!autoPause)
            triUsageThreshold = MaxTriUsageThreshold;

        if (triUsage > triUsageThreshold)
        {
            if (autoPause)
            {
                var hadAutoPause = pair.HasAutoPauseReason(Pair.AutoPauseReason.Triangles);
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: triangle count {0} exceeds limit of {1}.",
                    triUsage, triUsageThreshold);
                pair.SetAutoPaused(Pair.AutoPauseReason.Triangles, tooltip);

                if (notify && !wasBlocked && !hadAutoPause && !pair.AutoPauseNotificationShown)
                {
                    _mediator.Publish(new NotificationMessage(AutoBlockedTitle(pair),
                        AutoBlockedTriangleBody(pair, triUsage, triUsageThreshold),
                        Configuration.Models.NotificationType.Warning));
                    pair.MarkAutoPauseNotificationShown();
                }
            }
            else if (notify && !wasBlocked)
            {
                _mediator.Publish(new NotificationMessage(AutoBlockedTitle(pair),
                    AutoBlockedTriangleBody(pair, triUsage, triUsageThreshold),
                    Configuration.Models.NotificationType.Warning));
            }

            _mediator.Publish(new EventMessage(new Event(pair.PlayerName, pair.UserData, nameof(PlayerPerformanceService), EventSeverity.Warning,
                string.Format(CultureInfo.InvariantCulture,
                    "Exceeds triangle threshold: ({0}/{1} triangles)", triUsage, triUsageThreshold))));

            return false;
        }
        pair.ClearAutoPaused(Pair.AutoPauseReason.Triangles);
        
        return true;
    }

    public bool ComputeAndAutoPauseOnVRAMUsageThresholds(PairHandler pairHandler, CharacterData charaData, List<DownloadFileTransfer> toDownloadFiles, bool affect = false)
    {
        var config = _playerPerformanceConfigService.Current;
        var pair = pairHandler.Pair;

        long vramUsage = 0;

        var moddedTextureHashes = charaData.FileReplacements.SelectMany(k => k.Value)
            .Where(p => string.IsNullOrEmpty(p.FileSwapPath) && p.GamePaths.Any(g => g.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var hash in moddedTextureHashes)
        {
            long fileSize = 0;

            var download = toDownloadFiles.Find(f => string.Equals(hash, f.Hash, StringComparison.OrdinalIgnoreCase));
            if (download != null)
            {
                fileSize = download.TotalRaw;
            }
            else
            {
                var fileEntry = _fileCacheManager.GetFileCacheByHash(hash, preferSubst: true);
                if (fileEntry == null) continue;

                if (fileEntry.Size == null)
                {
                    fileEntry.Size = new FileInfo(fileEntry.ResolvedFilepath).Length;
                    _fileCacheManager.UpdateHashedFile(fileEntry, computeProperties: true);
                }

                fileSize = fileEntry.Size.Value;
            }

            vramUsage += fileSize;
        }

        pair.LastAppliedApproximateVRAMBytes = vramUsage;

        _logger.LogDebug("Calculated VRAM usage for {p}", pairHandler);

        long vramUsageThreshold = config.VRAMSizeAutoPauseThresholdMiB;
        bool isDirect = pair.UserPair != null;
        bool autoPause = config.AutoPausePlayersExceedingThresholds;
        bool notify = isDirect ? config.NotifyAutoPauseDirectPairs : config.NotifyAutoPauseGroupPairs;
        bool wasBlocked = pair.IsApplicationBlocked;
        
        if (autoPause && isDirect && config.IgnoreDirectPairs)
            autoPause = false;

        if (_serverConfigurationManager.IsUserWhitelisted(pair.UserData))
        {
            ClearThresholdAutoPaused(pair);
            return true;
        }

        if (!autoPause)
            vramUsageThreshold = MaxVRAMUsageThreshold;

        if (vramUsage > vramUsageThreshold * 1024 * 1024)
        {
            if (!affect)
                return false;

            var hadAutoPause = pair.HasAutoPauseReason(Pair.AutoPauseReason.Vram);

            if (autoPause)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: VRAM usage {0} exceeds limit of {1}MiB.",
                    UiSharedService.ByteToString(vramUsage, addSuffix: true), vramUsageThreshold);
                pair.SetAutoPaused(Pair.AutoPauseReason.Vram, tooltip);

                if (notify && !wasBlocked && !hadAutoPause && !pair.AutoPauseNotificationShown)
                {
                    _mediator.Publish(new NotificationMessage($"{pair.PlayerName} ({pair.UserData.AliasOrUID}) automatically blocked",
                        $"Player {pair.PlayerName} ({pair.UserData.AliasOrUID}) exceeded your configured VRAM auto block threshold (" +
                        $"{UiSharedService.ByteToString(vramUsage, addSuffix: true)}/{vramUsageThreshold}MiB)" +
                        $" and has been automatically blocked.",
                        Configuration.Models.NotificationType.Warning));
                    pair.MarkAutoPauseNotificationShown();
                }
            }
            else if (notify && !wasBlocked)
            {
                _mediator.Publish(new NotificationMessage(AutoBlockedTitle(pair),
                    AutoBlockedVramBody(pair, vramUsage, vramUsageThreshold),
                    Configuration.Models.NotificationType.Warning));
            }

            _mediator.Publish(new EventMessage(new Event(pair.PlayerName, pair.UserData, nameof(PlayerPerformanceService), EventSeverity.Warning,
                string.Format(CultureInfo.InvariantCulture,
                    "Exceeds VRAM threshold: ({0}/{1} MiB)",
                    UiSharedService.ByteToString(vramUsage, addSuffix: true), vramUsageThreshold))));

            return false;
        }
        if (affect)
            pair.ClearAutoPaused(Pair.AutoPauseReason.Vram);

        return true;
    }

       public void ReevaluateAutoPause(PairHandler pairHandler)
    {
        var pair = pairHandler.Pair;
        var config = _playerPerformanceConfigService.Current;

        bool isDirect = pair.UserPair != null;
        bool autoPause = config.AutoPausePlayersExceedingThresholds;

        if (autoPause && isDirect && config.IgnoreDirectPairs)
            autoPause = false;

        long triUsageThreshold = (autoPause ? config.TrisAutoPauseThresholdThousands * 1000 : MaxTriUsageThreshold);
        long vramUsageThreshold = (autoPause ? config.VRAMSizeAutoPauseThresholdMiB : MaxVRAMUsageThreshold) * 1024L * 1024L;

        if (_serverConfigurationManager.IsUserWhitelisted(pair.UserData))
        {
            ClearThresholdAutoPaused(pair);
            return;
        }

        // Re-run reported checks so newly raised thresholds can clear holds without waiting for fresh DTOs.
        CheckReportedThresholds(pairHandler, pair.LastReportedTriangles, pair.LastReportedApproximateVRAMBytes);

        if (pair.LastAppliedApproximateVRAMBytes >= 0)
        {
            if (pair.LastAppliedApproximateVRAMBytes > vramUsageThreshold)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: VRAM usage {0} exceeds limit of {1}MiB.",
                    UiSharedService.ByteToString(pair.LastAppliedApproximateVRAMBytes, addSuffix: true), vramUsageThreshold / (1024L * 1024L));
                pair.SetAutoPaused(Pair.AutoPauseReason.Vram, tooltip);
            }
            else
            {
                pair.ClearAutoPaused(Pair.AutoPauseReason.Vram);
            }
        }

        if (pair.LastAppliedDataTris >= 0)
        {
            if (pair.LastAppliedDataTris > triUsageThreshold)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: triangle count {0} exceeds limit of {1}.",
                    pair.LastAppliedDataTris, triUsageThreshold);
                pair.SetAutoPaused(Pair.AutoPauseReason.Triangles, tooltip);
            }
            else
            {
                pair.ClearAutoPaused(Pair.AutoPauseReason.Triangles);
            }
        }
    }

    private void ClearThresholdAutoPaused(Pair pair)
    {
        pair.ClearAutoPaused(Pair.AutoPauseReason.Vram);
        pair.ClearAutoPaused(Pair.AutoPauseReason.Triangles);
    }

    private void ClearAllCrowdPriorityAutoPauses()
    {
        foreach (var pair in EnumerateAllGroupPairs())
        {
            ClearCrowdPriorityAutoPause(pair);
        }
    }

    private void ClearCrowdPriorityAutoPause(Pair pair)
    {
        var hadCrowdPriorityPause = pair.HasAutoPauseReason(Pair.AutoPauseReason.CrowdPriority);
        if (!hadCrowdPriorityPause)
        {
            return;
        }

        pair.ClearAutoPaused(Pair.AutoPauseReason.CrowdPriority);
        if (pair.IsVisible && !pair.IsPaused && !pair.IsApplicationBlocked)
        {
            pair.ApplyLastReceivedData(forced: true);
        }
    }

    private static bool HasAnyCrowdPriorityThresholdEnabled(PlayerPerformanceConfig config)
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

    private IEnumerable<Pair> EnumerateAllGroupPairs()
    {
        if (!TryGetPairManager(out var pairManager))
        {
            return [];
        }

        return pairManager.GroupPairs
            .SelectMany(entry => entry.Value)
            .Distinct()
            .ToList();
    }

    private bool TryGetPairManager(out PairManager pairManager)
    {
        if (_pairManager != null)
        {
            pairManager = _pairManager;
            return true;
        }

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            _pairManager = scope.ServiceProvider.GetRequiredService<PairManager>();
            pairManager = _pairManager;
            return true;
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "Skipping pair enumeration because the service provider is disposed");
            pairManager = null!;
            return false;
        }
    }

    private IEnumerable<Pair> GetVisibleShellPairs()
    {
        return EnumerateAllGroupPairs()
            .Where(pair => pair.IsVisible);
    }

    private static long GetEstimatedVisibleVramBytes(Pair pair)
    {
        return Math.Max(pair.LastAppliedApproximateVRAMBytes, pair.LastReportedApproximateVRAMBytes ?? 0);
    }

    private static long GetEstimatedVisibleTriangleCount(Pair pair)
    {
        return Math.Max(pair.LastAppliedDataTris, pair.LastReportedTriangles ?? 0);
    }

    private static CrowdPriorityThresholdState GetCrowdPriorityThresholdState(PlayerPerformanceConfig config, int activeCount, long activeVramBytes, long activeTriangleCount)
    {
        return new CrowdPriorityThresholdState(
            config.CrowdPriorityVisibleMembersThreshold > 0 && activeCount > config.CrowdPriorityVisibleMembersThreshold,
            config.CrowdPriorityVRAMThresholdMiB > 0 && activeVramBytes > config.CrowdPriorityVRAMThresholdMiB * 1024L * 1024L,
            config.CrowdPriorityTrianglesThresholdThousands > 0 && activeTriangleCount > config.CrowdPriorityTrianglesThresholdThousands * 1000L);
    }

    private static double GetCrowdPriorityBurden(PlayerPerformanceConfig config, Pair pair)
    {
        var burden = 0d;
        if (config.CrowdPriorityVisibleMembersThreshold > 0)
        {
            burden += 1d / config.CrowdPriorityVisibleMembersThreshold;
        }

        if (config.CrowdPriorityVRAMThresholdMiB > 0)
        {
            burden += (double)GetEstimatedVisibleVramBytes(pair) / (config.CrowdPriorityVRAMThresholdMiB * 1024d * 1024d);
        }

        if (config.CrowdPriorityTrianglesThresholdThousands > 0)
        {
            burden += (double)GetEstimatedVisibleTriangleCount(pair) / (config.CrowdPriorityTrianglesThresholdThousands * 1000d);
        }

        return burden;
    }

    private static CrowdPriorityClassification ClassifyCrowdPriority(Pair pair, IReadOnlySet<uint> partyMemberIds)
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

    private static string BuildCrowdPriorityTooltip(Pair pair, CrowdPriorityClassification classification, PlayerPerformanceConfig config,
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
                UiSharedService.ByteToString(totalVramBytes, addSuffix: true),
                UiSharedService.ByteToString(config.CrowdPriorityVRAMThresholdMiB * 1024L * 1024L, addSuffix: true)));
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
       
    public async Task<bool> ShrinkTextures(PairHandler pairHandler, CharacterData charaData, CancellationToken token)
    {
        var config = _playerPerformanceConfigService.Current;

        if (config.TextureShrinkMode == Configuration.Models.TextureShrinkMode.Never)
            return false;

        // XXX: Temporary
        if (config.TextureShrinkMode == Configuration.Models.TextureShrinkMode.Default)
            return false;

        var moddedTextureHashes = charaData.FileReplacements.SelectMany(k => k.Value)
            .Where(p => string.IsNullOrEmpty(p.FileSwapPath) && p.GamePaths.Any(g => g.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool shrunken = false;

        await Parallel.ForEachAsync(moddedTextureHashes,
            token,
            async (hash, token) => {
                var fileEntry = _fileCacheManager.GetFileCacheByHash(hash, preferSubst: true);
                if (fileEntry == null) return;
                if (fileEntry.IsSubstEntry) return;

                var texFormat = _xivDataAnalyzer.GetTexFormatByHash(hash);
                var filePath = fileEntry.ResolvedFilepath;
                var tmpFilePath = _fileCacheManager.GetSubstFilePath(Guid.NewGuid().ToString(), "tmp");
                var newFilePath = _fileCacheManager.GetSubstFilePath(hash, "tex");
                var mipLevel = 0;
                uint width = texFormat.Width;
                uint height = texFormat.Height;
                long offsetDelta = 0;

                uint bitsPerPixel = texFormat.Format switch
                {
                    0x1130 => 8, // L8
                    0x1131 => 8, // A8
                    0x1440 => 16, // A4R4G4B4
                    0x1441 => 16, // A1R5G5B5
                    0x1450 => 32, // A8R8G8B8
                    0x1451 => 32, // X8R8G8B8
                    0x2150 => 32, // R32F
                    0x2250 => 32, // G16R16F
                    0x2260 => 64, // R32G32F
                    0x2460 => 64, // A16B16G16R16F
                    0x2470 => 128, // A32B32G32R32F
                    0x3420 => 4, // DXT1
                    0x3430 => 8, // DXT3
                    0x3431 => 8, // DXT5
                    0x4140 => 16, // D16
                    0x4250 => 32, // D24S8
                    0x6120 => 4, // BC4
                    0x6230 => 8, // BC5
                    0x6432 => 8, // BC7
                    _ => 0
                };

                uint maxSize = (bitsPerPixel <= 8) ? (2048U * 2048U) : (1024U * 1024U);

                while (width * height > maxSize && mipLevel < texFormat.MipCount - 1)
                {
                    offsetDelta += width * height * bitsPerPixel / 8;
                    mipLevel++;
                    width /= 2;
                    height /= 2;
                }

                if (offsetDelta == 0)
                    return;

                _logger.LogDebug("Shrinking {hash} from from {a}x{b} to {c}x{d}",
                    hash, texFormat.Width, texFormat.Height, width, height);

                try
                {
                    var inFile = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                    using var reader = new BinaryReader(inFile);

                    var header = reader.ReadBytes(80);
                    reader.BaseStream.Position = 14;
                    byte mipByte = reader.ReadByte();
                    byte mipCount = (byte)(mipByte & 0x7F);

                    var outFile = new FileStream(tmpFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var writer = new BinaryWriter(outFile);
                    writer.Write(header);

                    // Update width/height
                    writer.BaseStream.Position = 8;
                    writer.Write((ushort)width);
                    writer.Write((ushort)height);

                    // Update the mip count
                    writer.BaseStream.Position = 14;
                    writer.Write((ushort)((mipByte & 0x80) | (mipCount - mipLevel)));

                    // Reset all of the LoD mips
                    writer.BaseStream.Position = 16;
                    for (int i = 0; i < 3; ++i)
                        writer.Write((uint)0);

                    // Reset all of the mip offsets
                    // (This data is garbage in a lot of modded textures, so its hard to fix it up correctly)
                    writer.BaseStream.Position = 28;
                    for (int i = 0; i < 13; ++i)
                        writer.Write((uint)80);

                    // Write the texture data shifted
                    outFile.Position = 80;
                    inFile.Position = 80 + offsetDelta;

                    await inFile.CopyToAsync(outFile, 81920, token).ConfigureAwait(false);

                    reader.Dispose();
                    writer.Dispose();

                    File.Move(tmpFilePath, newFilePath);
                    var substEntry = _fileCacheManager.CreateSubstEntry(newFilePath);
                    if (substEntry != null)
                        substEntry.CompressedSize = fileEntry.CompressedSize;
                    shrunken = true;

                    // Make sure its a cache file before trying to delete it !!
                    bool shouldDelete = fileEntry.IsCacheEntry && File.Exists(filePath);

                    if (_playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal && shouldDelete)
                    {
                        try
                        {
                            _logger.LogDebug("Deleting original texture: {filePath}", filePath);
                            File.Delete(filePath);
                        }
                        catch { }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Failed to shrink texture {hash}", hash);
                    if (File.Exists(tmpFilePath))
                        File.Delete(tmpFilePath);
                }
            }
        ).ConfigureAwait(false);

        return shrunken;
    }

    private static string AutoBlockedTitle(Pair pair)
    {
        return string.Format(CultureInfo.InvariantCulture,
           "{0} ({1}) automatically blocked", pair.PlayerName, pair.UserData.AliasOrUID);
    }

    private static string AutoBlockedSummaryBody(Pair pair, string reasonSummary)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "Player {0} ({1}) exceeded your configured auto block threshold(s): {2}. Based on reported usage they have been automatically blocked.",
            pair.PlayerName, pair.UserData.AliasOrUID, reasonSummary);
    }

    private static string AutoBlockedTriangleBody(Pair pair, long triUsage, long triUsageThreshold)
    {
        return string.Format(CultureInfo.InvariantCulture,
           "Player {0} ({1}) exceeded your configured triangle auto block threshold ({2}/{3} triangles) and has been automatically blocked.",
            pair.PlayerName, pair.UserData.AliasOrUID, triUsage, triUsageThreshold);
    }

    private static string AutoBlockedVramBody(Pair pair, long vramUsageBytes, long vramThresholdMiB)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "Player {0} ({1}) exceeded your configured VRAM auto block threshold ({2}/{3}MiB) and has been automatically blocked.",
            pair.PlayerName, pair.UserData.AliasOrUID, UiSharedService.ByteToString(vramUsageBytes, addSuffix: true), vramThresholdMiB);
    }
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
