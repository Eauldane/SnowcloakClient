using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Localisation;
using System.Globalization;

namespace Snowcloak.Services;

public class PlayerPerformanceService : DisposableMediatorSubscriberBase
{
    // Limits that will still be enforced when no limits are enabled
    public const int MaxVRAMUsageThreshold = 2000; // 2GB
    public const int MaxTriUsageThreshold = 2000000; // 2 million triangles

    private readonly FileCacheManager _fileCacheManager;
    private readonly XivDataAnalyzer _xivDataAnalyzer;
    private readonly ILogger<PlayerPerformanceService> _logger;
    private readonly SnowMediator _mediator;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly Dictionary<string, bool> _warnedForPlayers = new(StringComparer.Ordinal);
    private readonly LocalisationService _localisationService;

    public PlayerPerformanceService(ILogger<PlayerPerformanceService> logger, SnowMediator mediator,
        ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService, FileCacheManager fileCacheManager,
        XivDataAnalyzer xivDataAnalyzer, LocalisationService localisationService)
        : base(logger, mediator)
    {
        _logger = logger;
        _mediator = mediator;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _fileCacheManager = fileCacheManager;
        _xivDataAnalyzer = xivDataAnalyzer;
        _localisationService = localisationService;
    }

    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"Services.PlayerPerformanceService.{key}", fallback);
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

        if (!autoPause || _serverConfigurationManager.IsUidWhitelisted(pair.UserData.UID))
        {
            triUsageThreshold = MaxTriUsageThreshold;
            vramUsageThreshold = MaxVRAMUsageThreshold * 1024L * 1024L;
            pair.ClearAutoPaused();
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
                    L("ReportedVramTooltip", "Auto-paused: reported VRAM usage {0} exceeds limit of {1}MiB."),
                    UiSharedService.ByteToString(reportedVramBytes.Value, addSuffix: true), config.VRAMSizeAutoPauseThresholdMiB);
                pair.SetAutoPaused(Pair.AutoPauseReason.Vram, tooltip);
                passed = false;

                if (!hadVramAutoPause)
                {
                    newlyBlockedReasons.Add(string.Format(CultureInfo.InvariantCulture,
                        L("ReportedVramReason", "VRAM usage {0}/{1}MiB"),
                        UiSharedService.ByteToString(reportedVramBytes.Value, addSuffix: true), config.VRAMSizeAutoPauseThresholdMiB));
                }

                _mediator.Publish(new EventMessage(new Event(pair.PlayerName, pair.UserData, nameof(PlayerPerformanceService), EventSeverity.Warning,
                    string.Format(CultureInfo.InvariantCulture,
                        L("ReportedVramEvent", "Reported VRAM exceeds threshold: ({0}/{1} MiB)"),
                        UiSharedService.ByteToString(reportedVramBytes.Value, addSuffix: true), config.VRAMSizeAutoPauseThresholdMiB))));
            }
            else
            {
                pair.ClearAutoPaused(Pair.AutoPauseReason.Vram);
            }

            if (reportedTriangles.HasValue && reportedTriangles.Value > triUsageThreshold)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    L("ReportedTrianglesTooltip", "Auto-paused: reported triangle count {0} exceeds limit of {1}."),
                    reportedTriangles.Value, triUsageThreshold);
                pair.SetAutoPaused(Pair.AutoPauseReason.Triangles, tooltip);
                passed = false;

                if (!hadTriangleAutoPause)
                {
                    newlyBlockedReasons.Add(string.Format(CultureInfo.InvariantCulture,
                        L("ReportedTrianglesReason", "triangle usage {0}/{1}"), reportedTriangles, triUsageThreshold));
                }

                _mediator.Publish(new EventMessage(new Event(pair.PlayerName, pair.UserData, nameof(PlayerPerformanceService), EventSeverity.Warning,
                    string.Format(CultureInfo.InvariantCulture,
                        L("ReportedTrianglesEvent", "Reported triangle usage exceeds threshold: ({0}/{1} triangles)"),
                        reportedTriangles, triUsageThreshold))));
            }
            else
            {
                pair.ClearAutoPaused(Pair.AutoPauseReason.Triangles);
            }
            
            if (notify && newlyBlockedReasons.Count > 0 && !wasBlocked)
            {
                var reasonSummary = string.Join("; ", newlyBlockedReasons);
                _mediator.Publish(new NotificationMessage(
                    AutoBlockedTitle(pair),
                    AutoBlockedSummaryBody(pair, reasonSummary),
                    Configuration.Models.NotificationType.Warning));
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

        if (!autoPause || _serverConfigurationManager.IsUidWhitelisted(pair.UserData.UID))
            triUsageThreshold = MaxTriUsageThreshold;

        if (triUsage > triUsageThreshold)
        {
            if (autoPause)
            {
                var hadAutoPause = pair.HasAutoPauseReason(Pair.AutoPauseReason.Triangles);
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    L("AppliedTriangleTooltip", "Auto-paused: triangle count {0} exceeds limit of {1}."),
                    triUsage, triUsageThreshold);
                pair.SetAutoPaused(Pair.AutoPauseReason.Triangles, tooltip);

                if (notify && !wasBlocked && !hadAutoPause)
                {
                    _mediator.Publish(new NotificationMessage(AutoBlockedTitle(pair),
                        AutoBlockedTriangleBody(pair, triUsage, triUsageThreshold),
                        Configuration.Models.NotificationType.Warning));
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
                    L("TriangleThresholdEvent", "Exceeds triangle threshold: ({0}/{1} triangles)"), triUsage, triUsageThreshold))));

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

        if (!autoPause || _serverConfigurationManager.IsUidWhitelisted(pair.UserData.UID))
            vramUsageThreshold = MaxVRAMUsageThreshold;

        if (vramUsage > vramUsageThreshold * 1024 * 1024)
        {
            if (!affect)
                return false;

            var hadAutoPause = pair.HasAutoPauseReason(Pair.AutoPauseReason.Vram);

            if (autoPause)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    L("AppliedVramTooltip", "Auto-paused: VRAM usage {0} exceeds limit of {1}MiB."),
                    UiSharedService.ByteToString(vramUsage, addSuffix: true), vramUsageThreshold);
                pair.SetAutoPaused(Pair.AutoPauseReason.Vram, tooltip);

                if (notify && !wasBlocked && !hadAutoPause)
                {
                    _mediator.Publish(new NotificationMessage($"{pair.PlayerName} ({pair.UserData.AliasOrUID}) automatically blocked",
                        $"Player {pair.PlayerName} ({pair.UserData.AliasOrUID}) exceeded your configured VRAM auto block threshold (" +
                        $"{UiSharedService.ByteToString(vramUsage, addSuffix: true)}/{vramUsageThreshold}MiB)" +
                        $" and has been automatically blocked.",
                        Configuration.Models.NotificationType.Warning));
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
                    L("VramThresholdEvent", "Exceeds VRAM threshold: ({0}/{1} MiB)"),
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

        if (_serverConfigurationManager.IsUidWhitelisted(pair.UserData.UID))
        {
            triUsageThreshold = MaxTriUsageThreshold;
            vramUsageThreshold = MaxVRAMUsageThreshold * 1024L * 1024L;
            pair.ClearAutoPaused();
        }

        // Re-run reported checks so newly raised thresholds can clear holds without waiting for fresh DTOs.
        CheckReportedThresholds(pairHandler, pair.LastReportedTriangles, pair.LastReportedApproximateVRAMBytes);

        if (pair.LastAppliedApproximateVRAMBytes >= 0)
        {
            if (pair.LastAppliedApproximateVRAMBytes > vramUsageThreshold)
            {
                var tooltip = string.Format(CultureInfo.InvariantCulture,
                    L("ReevaluationVramTooltip", "Auto-paused: VRAM usage {0} exceeds limit of {1}MiB."),
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
                    L("ReevaluationTriangleTooltip", "Auto-paused: triangle count {0} exceeds limit of {1}."),
                    pair.LastAppliedDataTris, triUsageThreshold);
                pair.SetAutoPaused(Pair.AutoPauseReason.Triangles, tooltip);
            }
            else
            {
                pair.ClearAutoPaused(Pair.AutoPauseReason.Triangles);
            }
        }
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

    private string AutoBlockedTitle(Pair pair)
    {
        return string.Format(CultureInfo.InvariantCulture,
            L("AutoBlockedTitle", "{0} ({1}) automatically blocked"), pair.PlayerName, pair.UserData.AliasOrUID);
    }

    private string AutoBlockedSummaryBody(Pair pair, string reasonSummary)
    {
        return string.Format(CultureInfo.InvariantCulture,
            L("AutoBlockedSummaryBody", "Player {0} ({1}) exceeded your configured auto block threshold(s): {2}. Based on reported usage they have been automatically blocked."),
            pair.PlayerName, pair.UserData.AliasOrUID, reasonSummary);
    }

    private string AutoBlockedTriangleBody(Pair pair, long triUsage, long triUsageThreshold)
    {
        return string.Format(CultureInfo.InvariantCulture,
            L("AutoBlockedTriangleBody", "Player {0} ({1}) exceeded your configured triangle auto block threshold ({2}/{3} triangles) and has been automatically blocked."),
            pair.PlayerName, pair.UserData.AliasOrUID, triUsage, triUsageThreshold);
    }

    private string AutoBlockedVramBody(Pair pair, long vramUsageBytes, long vramThresholdMiB)
    {
        return string.Format(CultureInfo.InvariantCulture,
            L("AutoBlockedVramBody", "Player {0} ({1}) exceeded your configured VRAM auto block threshold ({2}/{3}MiB) and has been automatically blocked."),
            pair.PlayerName, pair.UserData.AliasOrUID, UiSharedService.ByteToString(vramUsageBytes, addSuffix: true), vramThresholdMiB);
    }
}