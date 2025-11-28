using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;
using Snowcloak.Utils;

namespace Snowcloak.Services;

public sealed class PairAnalyzer : DisposableMediatorSubscriberBase
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly XivDataAnalyzer _xivDataAnalyzer;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource _baseAnalysisCts = new();
    private string _lastDataHash = string.Empty;

    public PairAnalyzer(ILogger<PairAnalyzer> logger, Pair pair, SnowMediator mediator, FileCacheManager fileCacheManager, XivDataAnalyzer modelAnalyzer)
        : base(logger, mediator)
    {
        Pair = pair;
#if DEBUG
        Mediator.SubscribeKeyed<PairDataAppliedMessage>(this, pair.UserData.UID, (msg) =>
        {
            _baseAnalysisCts = _baseAnalysisCts.CancelRecreate();
            var token = _baseAnalysisCts.Token;
            if (msg.CharacterData != null)
            {
                _ = BaseAnalysis(msg.CharacterData, token);
            }
            else
            {
                LastAnalysis.Clear();
                _lastDataHash = string.Empty;
            }
        });
#endif
        _fileCacheManager = fileCacheManager;
        _xivDataAnalyzer = modelAnalyzer;

#if DEBUG
        var lastReceivedData = pair.LastReceivedCharacterData;
        if (lastReceivedData != null)
            _ = BaseAnalysis(lastReceivedData, _baseAnalysisCts.Token);
#endif
    }

    public Pair Pair { get; init; }
    public int CurrentFile { get; internal set; }
    public bool IsAnalysisRunning => _analysisCts != null;
    public int TotalFiles { get; internal set; }
    internal Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>> LastAnalysis { get; } = [];
    internal string LastPlayerName { get; set; } = string.Empty;

    public void CancelAnalyze()
    {
        _analysisCts?.CancelDispose();
        _analysisCts = null;
    }

    public async Task ComputeAnalysis(bool print = true, bool recalculate = false)
    {
        Logger.LogDebug("=== Calculating Character Analysis ===");

        _analysisCts = _analysisCts?.CancelRecreate() ?? new();

        var cancelToken = _analysisCts.Token;

        var allFiles = LastAnalysis.SelectMany(v => v.Value.Select(d => d.Value)).ToList();
        if (allFiles.Exists(c => !c.IsComputed || recalculate))
        {
            var remaining = allFiles.Where(c => !c.IsComputed || recalculate).ToList();
            TotalFiles = remaining.Count;
            CurrentFile = 1;
            Logger.LogDebug("=== Computing {amount} remaining files ===", remaining.Count);

            Mediator.Publish(new HaltScanMessage(nameof(PairAnalyzer)));
            try
            {
                foreach (var file in remaining)
                {
                    Logger.LogDebug("Computing file {file}", file.FilePaths[0]);
                    await file.ComputeSizes(_fileCacheManager, cancelToken, ignoreCacheEntries: false).ConfigureAwait(false);
                    CurrentFile++;
                }

                _fileCacheManager.WriteOutFullCsv();

            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to analyze files");
            }
            finally
            {
                Mediator.Publish(new ResumeScanMessage(nameof(PairAnalyzer)));
            }
        }

        LastPlayerName = Pair.PlayerName ?? string.Empty;
        Mediator.Publish(new PairDataAnalyzedMessage(Pair.UserData.UID));

        _analysisCts.CancelDispose();
        _analysisCts = null;

        if (print) PrintAnalysis();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        _analysisCts?.CancelDispose();
        _baseAnalysisCts.CancelDispose();
    }

    private async Task BaseAnalysis(CharacterData charaData, CancellationToken token)
    {
        if (string.Equals(charaData.DataHash.Value, _lastDataHash, StringComparison.Ordinal)) return;

        LastAnalysis.Clear();

        foreach (var obj in charaData.FileReplacements)
        {
            Dictionary<string, CharacterAnalyzer.FileDataEntry> data = new(StringComparer.OrdinalIgnoreCase);
            foreach (var fileEntry in obj.Value)
            {
                token.ThrowIfCancellationRequested();

                var fileCacheEntries = _fileCacheManager.GetAllFileCachesByHash(fileEntry.Hash, ignoreCacheEntries: false, validate: false).ToList();
                if (fileCacheEntries.Count == 0) continue;

                var filePath = fileCacheEntries[^1].ResolvedFilepath;
                FileInfo fi = new(filePath);
                string ext = "unk?";
                try
                {
                    ext = fi.Extension[1..];
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not identify extension for {path}", filePath);
                }

                var tris = await Task.Run(() => _xivDataAnalyzer.GetTrianglesByHash(fileEntry.Hash)).ConfigureAwait(false);

                foreach (var entry in fileCacheEntries)
                {
                    data[fileEntry.Hash] = new CharacterAnalyzer.FileDataEntry(fileEntry.Hash, ext,
                        [.. fileEntry.GamePaths],
                        fileCacheEntries.Select(c => c.ResolvedFilepath).Distinct(StringComparer.Ordinal).ToList(),
                        entry.Size > 0 ? entry.Size.Value : 0,
                        entry.CompressedSize > 0 ? entry.CompressedSize.Value : 0,
                        tris);
                }
            }

            LastAnalysis[obj.Key] = data;
        }

        Mediator.Publish(new PairDataAnalyzedMessage(Pair.UserData.UID));

        _lastDataHash = charaData.DataHash.Value;
    }

    private void PrintAnalysis()
    {
        if (LastAnalysis.Count == 0) return;
        foreach (var kvp in LastAnalysis)
        {
            int fileCounter = 1;
            int totalFiles = kvp.Value.Count;
            Logger.LogInformation("=== Analysis for {uid}:{obj} ===", Pair.UserData.UID, kvp.Key);

            foreach (var entry in kvp.Value.OrderBy(b => b.Value.GamePaths.OrderBy(p => p, StringComparer.Ordinal).First(), StringComparer.Ordinal))
            {
                Logger.LogInformation("File {x}/{y}: {hash}", fileCounter++, totalFiles, entry.Key);
                foreach (var path in entry.Value.GamePaths)
                {
                    Logger.LogInformation("  Game Path: {path}", path);
                }
                if (entry.Value.FilePaths.Count > 1) Logger.LogInformation("  Multiple fitting files detected for {key}", entry.Key);
                foreach (var filePath in entry.Value.FilePaths)
                {
                    Logger.LogInformation("  File Path: {path}", filePath);
                }
                Logger.LogInformation("  Size: {size}, Compressed: {compressed}", UiSharedService.ByteToString(entry.Value.OriginalSize),
                    UiSharedService.ByteToString(entry.Value.CompressedSize));
            }
        }
        foreach (var kvp in LastAnalysis)
        {
            Logger.LogInformation("=== Detailed summary by file type for {obj} ===", kvp.Key);
            foreach (var entry in kvp.Value.Select(v => v.Value).GroupBy(v => v.FileType, StringComparer.Ordinal))
            {
                Logger.LogInformation("{ext} files: {count}, size extracted: {size}, size compressed: {sizeComp}", entry.Key, entry.Count(),
                    UiSharedService.ByteToString(entry.Sum(v => v.OriginalSize)), UiSharedService.ByteToString(entry.Sum(v => v.CompressedSize)));
            }
            Logger.LogInformation("=== Total summary for {obj} ===", kvp.Key);
            Logger.LogInformation("Total files: {count}, size extracted: {size}, size compressed: {sizeComp}", kvp.Value.Count,
            UiSharedService.ByteToString(kvp.Value.Sum(v => v.Value.OriginalSize)), UiSharedService.ByteToString(kvp.Value.Sum(v => v.Value.CompressedSize)));
        }

        Logger.LogInformation("=== Total summary for all currently present objects ===");
        Logger.LogInformation("Total files: {count}, size extracted: {size}, size compressed: {sizeComp}",
            LastAnalysis.Values.Sum(v => v.Values.Count),
            UiSharedService.ByteToString(LastAnalysis.Values.Sum(c => c.Values.Sum(v => v.OriginalSize))),
            UiSharedService.ByteToString(LastAnalysis.Values.Sum(c => c.Values.Sum(v => v.CompressedSize))));
    }
}