using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Configuration;
using Snowcloak.Core.Performance;
using Snowcloak.Core.PlayerData;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Performance;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.PlayerData.Pairs;
using System.Globalization;

namespace Snowcloak.Services;

public partial class PlayerPerformanceService : DisposableMediatorSubscriberBase
{
    public const int CurrentPerformanceConfigVersion = 8;

    // Limits that will still be enforced when no limits are enabled
    public const int MaxVRAMUsageThreshold = 2000; // 2GB
    public const int MaxTriUsageThreshold = 2000000; // 2 million triangles

    private readonly FileCacheManager _fileCacheManager;
    private readonly GpuMemoryBudgetService _gpuMemoryBudgetService;
    private readonly XivDataAnalyzer _xivDataAnalyzer;
    private readonly ILogger<PlayerPerformanceService> _logger;
    private readonly SnowMediator _mediator;
    private readonly BlockListStore _blockListStore;
    private readonly CrowdPriorityController _crowdPriorityController;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly TextureShrinkService _textureShrinkService;

    public PlayerPerformanceService(ILogger<PlayerPerformanceService> logger, SnowMediator mediator,
        BlockListStore blockListStore,
        PlayerPerformanceConfigService playerPerformanceConfigService, FileCacheManager fileCacheManager,
        XivDataAnalyzer xivDataAnalyzer, GpuMemoryBudgetService gpuMemoryBudgetService,
        CrowdPriorityController crowdPriorityController, TextureShrinkService textureShrinkService)
        : base(logger, mediator)
    {
        _logger = logger;
        _mediator = mediator;
        _blockListStore = blockListStore;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _fileCacheManager = fileCacheManager;
        _xivDataAnalyzer = xivDataAnalyzer;
        _gpuMemoryBudgetService = gpuMemoryBudgetService;
        _crowdPriorityController = crowdPriorityController;
        _textureShrinkService = textureShrinkService;

        EnsureRecommendedDefaults();
    }

    private void EnsureRecommendedDefaults()
    {
        var config = _playerPerformanceConfigService.Current;
        var budget = _gpuMemoryBudgetService.GetCurrentBudget();
        var result = PerformanceBudgetPolicy.ApplyRecommendedDefaults(
            new PerformanceRecommendedDefaults(
                config.Version,
                config.VRAMSizeAutoPauseThresholdMiB,
                config.TrisAutoPauseThresholdThousands,
                config.CrowdPriorityVisibleMembersThreshold,
                config.CrowdPriorityVRAMThresholdMiB,
                config.CrowdPriorityTrianglesThresholdThousands,
                config.CrowdPriorityModeEnabled),
            CurrentPerformanceConfigVersion,
            budget?.TotalBytes ?? 0,
            budget?.BudgetBytes ?? 0,
            budget?.AvailableBytes ?? 0);

        if (result.Changed)
        {
            config.Version = result.Defaults.Version;
            config.VRAMSizeAutoPauseThresholdMiB = result.Defaults.VramAutoPauseThresholdMiB;
            config.TrisAutoPauseThresholdThousands = result.Defaults.TriangleAutoPauseThresholdThousands;
            config.CrowdPriorityVisibleMembersThreshold = result.Defaults.CrowdVisibleMembersThreshold;
            config.CrowdPriorityVRAMThresholdMiB = result.Defaults.CrowdVramThresholdMiB;
            config.CrowdPriorityTrianglesThresholdThousands = result.Defaults.CrowdTriangleThresholdThousands;
            config.CrowdPriorityModeEnabled = result.Defaults.CrowdPriorityModeEnabled;
            _playerPerformanceConfigService.Update(_ => { });
        }
    }

    public CrowdPrioritySnapshot GetCrowdPrioritySnapshot() => _crowdPriorityController.GetSnapshot();

    public void ReevaluateCrowdPriority(bool force = false) => _crowdPriorityController.Reevaluate(force);
    
    public bool CheckReportedThresholds(PairHandler pairHandler, long? reportedTriangles, long? reportedVramBytes)
    {
        ArgumentNullException.ThrowIfNull(pairHandler);

        var config = _playerPerformanceConfigService.Current;
        var pair = pairHandler.Pair;

        bool isDirect = pair.UserPair != null;
        var decision = PerformanceBudgetPolicy.EvaluateAutoPause(
            CreateAutoPausePolicySettings(config),
            new PairUsageContext(isDirect, _blockListStore.IsUserWhitelisted(pair.UserData), reportedVramBytes, reportedTriangles));
        bool autoPause = !decision.ShouldClearExisting;
        bool notify = isDirect ? config.NotifyAutoPauseDirectPairs : config.NotifyAutoPauseGroupPairs;
        bool wasBlocked = pair.IsApplicationBlocked;

        if (decision.ShouldClearExisting)
        {
            ClearThresholdAutoPaused(pair);
            return true;
        }

        long triUsageThreshold = decision.Thresholds.TriangleCount;
        long vramUsageThresholdMiB = decision.Thresholds.VramBytes / (1024L * 1024L);

        bool passed = true;

        if (autoPause)
        {
            var hadVramAutoPause = pair.HasAutoPauseReason(AutoPauseReason.Vram);
            var hadTriangleAutoPause = pair.HasAutoPauseReason(AutoPauseReason.Triangles);
            var newlyBlockedReasons = new List<string>();

            if (decision.ShouldPauseVram && reportedVramBytes is { } reportedVram)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: reported VRAM usage {0} exceeds limit of {1}MiB.",
                    ElezenImgui.ByteToString(reportedVram, addSuffix: true), vramUsageThresholdMiB);
                pair.SetAutoPaused(AutoPauseReason.Vram, tooltip);
                passed = false;

                if (!hadVramAutoPause)
                {
                    newlyBlockedReasons.Add(string.Format(CultureInfo.InvariantCulture,
                        "VRAM usage {0}/{1}MiB",
                        ElezenImgui.ByteToString(reportedVram, addSuffix: true), vramUsageThresholdMiB));
                }

                _mediator.Publish(new EventMessage(new Event(pair.PlayerName, pair.UserData, nameof(PlayerPerformanceService), EventSeverity.Warning,
                    string.Format(CultureInfo.InvariantCulture,
                        "Reported VRAM exceeds threshold: ({0}/{1} MiB)",
                        ElezenImgui.ByteToString(reportedVram, addSuffix: true), vramUsageThresholdMiB))));
            }
            else
            {
                pair.ClearAutoPaused(AutoPauseReason.Vram);
            }

            if (decision.ShouldPauseTriangles && reportedTriangles is { } reportedTriangleCount)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: reported triangle count {0} exceeds limit of {1}.",
                    reportedTriangleCount, triUsageThreshold);
                pair.SetAutoPaused(AutoPauseReason.Triangles, tooltip);
                passed = false;

                if (!hadTriangleAutoPause)
                {
                    newlyBlockedReasons.Add(string.Format(CultureInfo.InvariantCulture,
                        "triangle usage {0}/{1}", reportedTriangleCount, triUsageThreshold));
                }

                _mediator.Publish(new EventMessage(new Event(pair.PlayerName, pair.UserData, nameof(PlayerPerformanceService), EventSeverity.Warning,
                    string.Format(CultureInfo.InvariantCulture,
                        "Reported triangle usage exceeds threshold: ({0}/{1} triangles)",
                        reportedTriangleCount, triUsageThreshold))));
            }
            else
            {
                pair.ClearAutoPaused(AutoPauseReason.Triangles);
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
        ArgumentNullException.ThrowIfNull(pairHandler);
        ArgumentNullException.ThrowIfNull(charaData);

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

        LogCalculatedTriangleUsage(_logger, pairHandler);

        long triUsageThreshold = config.TrisAutoPauseThresholdThousands * 1000;
        bool isDirect = pair.UserPair != null;
        bool autoPause = config.AutoPausePlayersExceedingThresholds;
        bool notify = isDirect ? config.NotifyAutoPauseDirectPairs : config.NotifyAutoPauseGroupPairs;
        bool wasBlocked = pair.IsApplicationBlocked;
        
        if (autoPause && isDirect && config.IgnoreDirectPairs)
            autoPause = false;

        if (_blockListStore.IsUserWhitelisted(pair.UserData))
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
                var hadAutoPause = pair.HasAutoPauseReason(AutoPauseReason.Triangles);
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: triangle count {0} exceeds limit of {1}.",
                    triUsage, triUsageThreshold);
                pair.SetAutoPaused(AutoPauseReason.Triangles, tooltip);

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
        pair.ClearAutoPaused(AutoPauseReason.Triangles);
        
        return true;
    }

    public bool ComputeAndAutoPauseOnVRAMUsageThresholds(PairHandler pairHandler, CharacterData charaData, IReadOnlyList<DownloadFileTransfer> toDownloadFiles, bool affect = false)
    {
        ArgumentNullException.ThrowIfNull(pairHandler);
        ArgumentNullException.ThrowIfNull(charaData);
        ArgumentNullException.ThrowIfNull(toDownloadFiles);

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

            var download = toDownloadFiles.FirstOrDefault(f => string.Equals(hash, f.Hash, StringComparison.OrdinalIgnoreCase));
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

        LogCalculatedVramUsage(_logger, pairHandler);

        long vramUsageThreshold = config.VRAMSizeAutoPauseThresholdMiB;
        bool isDirect = pair.UserPair != null;
        bool autoPause = config.AutoPausePlayersExceedingThresholds;
        bool notify = isDirect ? config.NotifyAutoPauseDirectPairs : config.NotifyAutoPauseGroupPairs;
        bool wasBlocked = pair.IsApplicationBlocked;
        
        if (autoPause && isDirect && config.IgnoreDirectPairs)
            autoPause = false;

        if (_blockListStore.IsUserWhitelisted(pair.UserData))
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

            var hadAutoPause = pair.HasAutoPauseReason(AutoPauseReason.Vram);

            if (autoPause)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: VRAM usage {0} exceeds limit of {1}MiB.",
                    ElezenImgui.ByteToString(vramUsage, addSuffix: true), vramUsageThreshold);
                pair.SetAutoPaused(AutoPauseReason.Vram, tooltip);

                if (notify && !wasBlocked && !hadAutoPause && !pair.AutoPauseNotificationShown)
                {
                    _mediator.Publish(new NotificationMessage($"{pair.PlayerName} ({pair.UserData.AliasOrUID}) automatically blocked",
                        $"Player {pair.PlayerName} ({pair.UserData.AliasOrUID}) exceeded your configured VRAM auto block threshold (" +
                        $"{ElezenImgui.ByteToString(vramUsage, addSuffix: true)}/{vramUsageThreshold}MiB)" +
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
                    ElezenImgui.ByteToString(vramUsage, addSuffix: true), vramUsageThreshold))));

            return false;
        }
        if (affect)
            pair.ClearAutoPaused(AutoPauseReason.Vram);

        return true;
    }

    public void ReevaluateAutoPause(PairHandler pairHandler)
    {
        ArgumentNullException.ThrowIfNull(pairHandler);

        var pair = pairHandler.Pair;
        var config = _playerPerformanceConfigService.Current;

        bool isDirect = pair.UserPair != null;
        bool autoPause = config.AutoPausePlayersExceedingThresholds;

        if (autoPause && isDirect && config.IgnoreDirectPairs)
            autoPause = false;

        long triUsageThreshold = (autoPause ? config.TrisAutoPauseThresholdThousands * 1000 : MaxTriUsageThreshold);
        long vramUsageThreshold = (autoPause ? config.VRAMSizeAutoPauseThresholdMiB : MaxVRAMUsageThreshold) * 1024L * 1024L;

        if (_blockListStore.IsUserWhitelisted(pair.UserData))
        {
            ClearThresholdAutoPaused(pair);
            return;
        }

        CheckReportedThresholds(pairHandler, pair.LastReportedTriangles, pair.LastReportedApproximateVRAMBytes);

        if (pair.LastAppliedApproximateVRAMBytes >= 0)
        {
            if (pair.LastAppliedApproximateVRAMBytes > vramUsageThreshold)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: VRAM usage {0} exceeds limit of {1}MiB.",
                    ElezenImgui.ByteToString(pair.LastAppliedApproximateVRAMBytes, addSuffix: true), vramUsageThreshold / (1024L * 1024L));
                pair.SetAutoPaused(AutoPauseReason.Vram, tooltip);
            }
            else
            {
                pair.ClearAutoPaused(AutoPauseReason.Vram);
            }
        }

        if (pair.LastAppliedDataTris >= 0)
        {
            if (pair.LastAppliedDataTris > triUsageThreshold)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    "Auto-paused: triangle count {0} exceeds limit of {1}.",
                    pair.LastAppliedDataTris, triUsageThreshold);
                pair.SetAutoPaused(AutoPauseReason.Triangles, tooltip);
            }
            else
            {
                pair.ClearAutoPaused(AutoPauseReason.Triangles);
            }
        }
    }

    private static void ClearThresholdAutoPaused(Pair pair)
    {
        pair.ClearAutoPaused(AutoPauseReason.Vram);
        pair.ClearAutoPaused(AutoPauseReason.Triangles);
    }

    private static AutoPausePolicySettings CreateAutoPausePolicySettings(Configuration.Configurations.PlayerPerformanceConfig config)
    {
        return new AutoPausePolicySettings(
            config.AutoPausePlayersExceedingThresholds,
            config.IgnoreDirectPairs,
            config.VRAMSizeAutoPauseThresholdMiB,
            config.TrisAutoPauseThresholdThousands,
            MaxVRAMUsageThreshold,
            MaxTriUsageThreshold);
    }

    public Task<bool> ShrinkTextures(PairHandler pairHandler, CharacterData charaData, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(pairHandler);
        return _textureShrinkService.ShrinkTextures(charaData, token);
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
            pair.PlayerName, pair.UserData.AliasOrUID, ElezenImgui.ByteToString(vramUsageBytes, addSuffix: true), vramThresholdMiB);
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Calculated Triangle usage for {PairHandler}")]
    private static partial void LogCalculatedTriangleUsage(ILogger logger, PairHandler pairHandler);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Calculated VRAM usage for {PairHandler}")]
    private static partial void LogCalculatedVramUsage(ILogger logger, PairHandler pairHandler);
}
