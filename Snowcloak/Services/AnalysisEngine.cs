using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Tex.Buffers;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.Core.Analysis;
using Snowcloak.FileCache;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;
using Snowcloak.Utils;

namespace Snowcloak.Services;

internal sealed partial class AnalysisEngine : IDisposable, IAsyncDisposable
{
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly SingleFlightCts _analysisFlight = new();
    private readonly SingleFlightCts _baseAnalysisFlight = new();
    private readonly Lock _analysisLock = new();
    private readonly FileCacheManager _fileCacheManager;
    private readonly bool _ignoreCacheEntries;
    private readonly bool _includeImportantNotes;
    private readonly ILogger _logger;
    private readonly string? _logSubject;
    private readonly SnowMediator _mediator;
    private readonly string _ownerName;
    private readonly Action _publishAnalyzed;
    private readonly AnalysisCachePathChoice _cachePathChoice;
    private readonly XivDataAnalyzer _xivDataAnalyzer;
    private AnalysisSnapshot _lastAnalysis = AnalysisSnapshot.Empty;
    private int _activeAnalysisGeneration;
    private int _analysisGeneration;
    private int _disposed;
    private string _lastDataHash = string.Empty;

    public AnalysisEngine(
        ILogger logger,
        SnowMediator mediator,
        FileCacheManager fileCacheManager,
        XivDataAnalyzer xivDataAnalyzer,
        string ownerName,
        bool ignoreCacheEntries,
        AnalysisCachePathChoice cachePathChoice,
        Action publishAnalyzed,
        bool includeImportantNotes,
        string? logSubject = null)
    {
        _logger = logger;
        _mediator = mediator;
        _fileCacheManager = fileCacheManager;
        _xivDataAnalyzer = xivDataAnalyzer;
        _ownerName = ownerName;
        _ignoreCacheEntries = ignoreCacheEntries;
        _cachePathChoice = cachePathChoice;
        _publishAnalyzed = publishAnalyzed;
        _includeImportantNotes = includeImportantNotes;
        _logSubject = logSubject;
        _backgroundTasks = new BackgroundTaskTracker(logger);
    }

    public int CurrentFile { get; private set; }
    public bool IsAnalysisRunning => Volatile.Read(ref _activeAnalysisGeneration) != 0;
    public int TotalFiles { get; private set; }

    public void CancelAnalyze()
    {
        _analysisFlight.Cancel();
        Volatile.Write(ref _activeAnalysisGeneration, 0);
    }

    public void QueueBaseAnalysis(CharacterData charaData)
    {
        var scope = _baseAnalysisFlight.Begin();
        _ = _backgroundTasks.Run(async () =>
        {
            using (scope)
            {
                await BaseAnalysis(charaData, scope.Token).ConfigureAwait(false);
            }
        }, nameof(BaseAnalysis));
    }

    public async Task ComputeAnalysis(bool print = true, bool recalculate = false, Action? beforePublish = null)
    {
        LogCalculatingAnalysis(_logger);

        using var scope = _analysisFlight.Begin();
        var generation = Interlocked.Increment(ref _analysisGeneration);
        Volatile.Write(ref _activeAnalysisGeneration, generation);

        try
        {
            var cancelToken = scope.Token;
            var snapshot = GetLastAnalysisSnapshot();
            var allFiles = snapshot.Files.ToList();

            if (allFiles.Exists(file => !file.IsComputed || recalculate))
            {
                var remaining = allFiles.Where(file => !file.IsComputed || recalculate).ToList();
                TotalFiles = remaining.Count;
                CurrentFile = 1;
                LogComputingRemainingFiles(_logger, remaining.Count);

                var updates = new List<AnalysisFileEntry>(remaining.Count);
                _mediator.Publish(new HaltScanMessage(_ownerName));
                try
                {
                    foreach (var file in remaining)
                    {
                        var filePath = file.FilePaths.Count > 0 ? file.FilePaths[0] : string.Empty;
                        LogComputingFile(_logger, filePath);
                        updates.Add(await ComputeSizes(file, cancelToken).ConfigureAwait(false));
                        CurrentFile++;
                    }

                    _fileCacheManager.WriteOutFullIndex();
                }
                catch (OperationCanceledException ex) when (cancelToken.IsCancellationRequested)
                {
                    LogAnalysisCancelled(_logger, ex);
                }
                catch (IOException ex)
                {
                    LogFailedToAnalyzeFiles(_logger, ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogFailedToAnalyzeFiles(_logger, ex);
                }
                catch (InvalidOperationException ex)
                {
                    LogFailedToAnalyzeFiles(_logger, ex);
                }
                catch (ArgumentException ex)
                {
                    LogFailedToAnalyzeFiles(_logger, ex);
                }
                catch (NotSupportedException ex)
                {
                    LogFailedToAnalyzeFiles(_logger, ex);
                }
                catch (TimeoutException ex)
                {
                    LogFailedToAnalyzeFiles(_logger, ex);
                }
                finally
                {
                    if (updates.Count > 0)
                    {
                        lock (_analysisLock)
                        {
                            _lastAnalysis = _lastAnalysis.UpdateFiles(updates);
                        }
                    }

                    _mediator.Publish(new ResumeScanMessage(_ownerName));
                }
            }

            beforePublish?.Invoke();
            _publishAnalyzed();

            if (print) PrintAnalysis();
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeAnalysisGeneration, 0, generation);
        }
    }

    public AnalysisSnapshot GetLastAnalysisSnapshot()
    {
        lock (_analysisLock)
        {
            return _lastAnalysis;
        }
    }

    public void Reset()
    {
        _baseAnalysisFlight.Cancel();
        lock (_analysisLock)
        {
            _lastAnalysis = AnalysisSnapshot.Empty;
        }
        _lastDataHash = string.Empty;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _backgroundTasks.StopAccepting();
        _analysisFlight.Cancel();
        _baseAnalysisFlight.Cancel();
        _backgroundTasks.StopSynchronously(_logger, TimeSpan.FromSeconds(2), _ownerName);
        DisposeOwnedResources();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _backgroundTasks.StopAccepting();
        _analysisFlight.Cancel();
        _baseAnalysisFlight.Cancel();
        await _backgroundTasks.StopAsync(CancellationToken.None).ConfigureAwait(false);
        DisposeOwnedResources();
        GC.SuppressFinalize(this);
    }

    private async Task BaseAnalysis(CharacterData charaData, CancellationToken token)
    {
        if (string.Equals(charaData.DataHash.Value, _lastDataHash, StringComparison.Ordinal)) return;

        var objects = new List<AnalysisObjectSnapshot>();

        foreach (var obj in charaData.FileReplacements)
        {
            var data = new List<AnalysisFileEntry>();
            foreach (var fileEntry in obj.Value)
            {
                token.ThrowIfCancellationRequested();

                var fileCacheEntries = _fileCacheManager.GetAllFileCachesByHash(fileEntry.Hash, _ignoreCacheEntries, validate: false);
                if (fileCacheEntries.Count == 0) continue;

                var filePath = SelectExtensionPath(fileCacheEntries);
                var ext = GetExtension(filePath);
                var tris = await Task.Run(() => _xivDataAnalyzer.GetTrianglesByHash(fileEntry.Hash), token).ConfigureAwait(false);
                var filePaths = fileCacheEntries.Select(entry => entry.ResolvedFilepath).Distinct(StringComparer.Ordinal).ToList();
                var textureTraits = AnalyzeTexture(ext, filePaths, fileEntry.GamePaths);
                var entry = fileCacheEntries[^1];

                data.Add(new AnalysisFileEntry(
                    fileEntry.Hash,
                    ext,
                    fileEntry.GamePaths,
                    filePaths,
                    entry.Size > 0 ? entry.Size.Value : 0,
                    entry.CompressedSize > 0 ? entry.CompressedSize.Value : 0,
                    tris,
                    textureTraits));
            }

            objects.Add(new AnalysisObjectSnapshot(obj.Key, data));
        }

        lock (_analysisLock)
        {
            _lastAnalysis = new AnalysisSnapshot(objects);
        }

        _publishAnalyzed();

        _lastDataHash = charaData.DataHash.Value;
    }

    private async Task<AnalysisFileEntry> ComputeSizes(AnalysisFileEntry file, CancellationToken token)
    {
        var compressedData = await _fileCacheManager.GetCompressedFileData(file.Hash, token).ConfigureAwait(false);
        var compressedSize = compressedData.Item2;
        await using (compressedSize.ConfigureAwait(false))
        {
            var normalSize = new FileInfo(file.FilePaths[0]).Length;
            var entries = _fileCacheManager.GetAllFileCachesByHash(file.Hash, _ignoreCacheEntries, validate: false);
            foreach (var entry in entries)
            {
                entry.Size = normalSize;
                entry.CompressedSize = compressedSize.Length;
            }

            return file.WithSizes(normalSize, compressedSize.Length);
        }
    }

    private string SelectExtensionPath(List<FileCacheEntity> fileCacheEntries)
    {
        return _cachePathChoice == AnalysisCachePathChoice.First
            ? fileCacheEntries[0].ResolvedFilepath
            : fileCacheEntries[^1].ResolvedFilepath;
    }

    private static string GetExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Length > 1 ? ext[1..] : "unk?";
    }

    private static AnalysisTextureTraits? AnalyzeTexture(string fileType, List<string> filePaths, IEnumerable<string> gamePaths)
    {
        if (!string.Equals(fileType, "tex", StringComparison.OrdinalIgnoreCase) || filePaths.Count == 0)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(filePaths[0], FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new LuminaBinaryReader(stream);

            var header = reader.ReadStructure<TexFile.TexHeader>();
            var buffer = TextureBuffer.FromStream(header, reader);

            var rawData = buffer.RawData;
            var mipAllocations = buffer.MipmapAllocations;
            var mip0Length = mipAllocations != null && mipAllocations.Length > 0 ? mipAllocations[0] : rawData.Length;
            mip0Length = Math.Min(mip0Length, rawData.Length);

            var mip0Span = rawData.AsSpan(0, mip0Length);
            return AnalysisTextureTraits.Create(header.Format.ToString(), header.Width, header.Height, mip0Span, gamePaths);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private void PrintAnalysis()
    {
        var analysis = GetLastAnalysisSnapshot();
        if (analysis.IsEmpty) return;
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        foreach (var kvp in analysis.Objects)
        {
            var fileCounter = 1;
            var totalFiles = kvp.Value.Files.Count;
            var objectLabel = _logSubject == null ? kvp.Key.ToString() : _logSubject + ":" + kvp.Key;
            LogAnalysisFor(_logger, objectLabel);

            foreach (var entry in kvp.Value.Files.OrderBy(b => b.Value.GamePaths.OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty, StringComparer.Ordinal))
            {
                LogAnalysisFile(_logger, fileCounter++, totalFiles, entry.Key);
                foreach (var path in entry.Value.GamePaths)
                {
                    LogGamePath(_logger, path);
                }
                if (entry.Value.FilePaths.Count > 1) LogMultipleFittingFiles(_logger, entry.Key);
                foreach (var filePath in entry.Value.FilePaths)
                {
                    LogFilePath(_logger, filePath);
                }
                var originalSize = ElezenImgui.ByteToString(entry.Value.OriginalSize);
                var compressedSize = ElezenImgui.ByteToString(entry.Value.CompressedSize);
                LogFileSize(_logger, originalSize, compressedSize);
            }
        }
        foreach (var kvp in analysis.Objects)
        {
            LogDetailedSummary(_logger, kvp.Key);
            foreach (var entry in kvp.Value.Files.Select(v => v.Value).GroupBy(v => v.FileType, StringComparer.Ordinal))
            {
                var originalSize = ElezenImgui.ByteToString(entry.Sum(v => v.OriginalSize));
                var compressedSize = ElezenImgui.ByteToString(entry.Sum(v => v.CompressedSize));
                var count = entry.Count();
                LogFileTypeSummary(_logger, entry.Key, count, originalSize, compressedSize);
            }
            LogTotalSummaryHeader(_logger, kvp.Key);
            var totalOriginalSize = ElezenImgui.ByteToString(kvp.Value.Files.Sum(v => v.Value.OriginalSize));
            var totalCompressedSize = ElezenImgui.ByteToString(kvp.Value.Files.Sum(v => v.Value.CompressedSize));
            LogTotalSummary(_logger, kvp.Value.Files.Count, totalOriginalSize, totalCompressedSize);
        }

        LogAllObjectsSummaryHeader(_logger);
        var allObjectCount = analysis.Objects.Values.Sum(v => v.Files.Count);
        var allObjectOriginalSize = ElezenImgui.ByteToString(analysis.Objects.Values.Sum(c => c.Files.Values.Sum(v => v.OriginalSize)));
        var allObjectCompressedSize = ElezenImgui.ByteToString(analysis.Objects.Values.Sum(c => c.Files.Values.Sum(v => v.CompressedSize)));
        LogTotalSummary(_logger, allObjectCount, allObjectOriginalSize, allObjectCompressedSize);
        if (_includeImportantNotes)
        {
            LogImportantNotes(_logger);
        }
    }

    private void DisposeOwnedResources()
    {
        _analysisFlight.Dispose();
        _baseAnalysisFlight.Dispose();
    }

    [LoggerMessage(EventId = 29000, Level = LogLevel.Debug, Message = "=== Calculating Character Analysis ===")]
    private static partial void LogCalculatingAnalysis(ILogger logger);

    [LoggerMessage(EventId = 29001, Level = LogLevel.Debug, Message = "=== Computing {Amount} remaining files ===")]
    private static partial void LogComputingRemainingFiles(ILogger logger, int amount);

    [LoggerMessage(EventId = 29002, Level = LogLevel.Debug, Message = "Computing file {File}")]
    private static partial void LogComputingFile(ILogger logger, string file);

    [LoggerMessage(EventId = 29003, Level = LogLevel.Debug, Message = "Analysis cancelled")]
    private static partial void LogAnalysisCancelled(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 29004, Level = LogLevel.Warning, Message = "Failed to analyze files")]
    private static partial void LogFailedToAnalyzeFiles(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 29005, Level = LogLevel.Information, Message = "=== Analysis for {Obj} ===")]
    private static partial void LogAnalysisFor(ILogger logger, string obj);

    [LoggerMessage(EventId = 29006, Level = LogLevel.Information, Message = "File {X}/{Y}: {Hash}")]
    private static partial void LogAnalysisFile(ILogger logger, int x, int y, string hash);

    [LoggerMessage(EventId = 29007, Level = LogLevel.Information, Message = "  Game Path: {Path}")]
    private static partial void LogGamePath(ILogger logger, string path);

    [LoggerMessage(EventId = 29008, Level = LogLevel.Information, Message = "  Multiple fitting files detected for {Key}")]
    private static partial void LogMultipleFittingFiles(ILogger logger, string key);

    [LoggerMessage(EventId = 29009, Level = LogLevel.Information, Message = "  File Path: {Path}")]
    private static partial void LogFilePath(ILogger logger, string path);

    [LoggerMessage(EventId = 29010, Level = LogLevel.Information, Message = "  Size: {Size}, Compressed: {Compressed}")]
    private static partial void LogFileSize(ILogger logger, string size, string compressed);

    [LoggerMessage(EventId = 29011, Level = LogLevel.Information, Message = "=== Detailed summary by file type for {Obj} ===")]
    private static partial void LogDetailedSummary(ILogger logger, ObjectKind obj);

    [LoggerMessage(EventId = 29012, Level = LogLevel.Information, Message = "{Ext} files: {Count}, size extracted: {Size}, size compressed: {SizeComp}")]
    private static partial void LogFileTypeSummary(ILogger logger, string ext, int count, string size, string sizeComp);

    [LoggerMessage(EventId = 29013, Level = LogLevel.Information, Message = "=== Total summary for {Obj} ===")]
    private static partial void LogTotalSummaryHeader(ILogger logger, ObjectKind obj);

    [LoggerMessage(EventId = 29014, Level = LogLevel.Information, Message = "Total files: {Count}, size extracted: {Size}, size compressed: {SizeComp}")]
    private static partial void LogTotalSummary(ILogger logger, int count, string size, string sizeComp);

    [LoggerMessage(EventId = 29015, Level = LogLevel.Information, Message = "=== Total summary for all currently present objects ===")]
    private static partial void LogAllObjectsSummaryHeader(ILogger logger);

    [LoggerMessage(EventId = 29016, Level = LogLevel.Information, Message = "IMPORTANT NOTES:\n\r- For uploads and downloads only the compressed size is relevant.\n\r- An unusually high total files count beyond 200 and up will also increase your download time to others significantly.")]
    private static partial void LogImportantNotes(ILogger logger);
}

internal enum AnalysisCachePathChoice
{
    First,
    Last,
}
