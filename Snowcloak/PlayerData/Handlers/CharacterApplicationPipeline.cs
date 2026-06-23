using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using ElezenTools.Core.Async;
using Snowcloak.FileCache;
using Snowcloak.Interop.Ipc;
using Snowcloak.PlayerData.Data;
using Snowcloak.PlayerData.Factories;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ModNullification;
using Snowcloak.Services.Performance;
using Snowcloak.WebAPI.Files;
using System.Collections.Concurrent;
using System.Diagnostics;
using CharacterData = Snowcloak.API.Data.CharacterData;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

using ElezenTools.Services;

namespace Snowcloak.PlayerData.Handlers;

internal sealed partial class CharacterApplicationPipeline
{
    private readonly PairHandler _handler;
    private readonly ILogger Logger;
    private readonly SnowMediator Mediator;
    private readonly Pair Pair;
    private readonly PairAppliedState _appliedState;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly CancellationTokenSource _runtimeCts;
    private readonly SingleFlightCts _applicationFlight;
    private readonly SingleFlightCts _downloadFlight;
    private readonly SemaphoreSlim _applicationGate;
    private readonly FileDownloadManager _downloadManager;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly IApplyGameState _gameState;
    private readonly FileCacheManager _fileDbManager;
    private readonly DatabaseService _databaseService;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly ModNullificationService _modNullificationService;
    private readonly ModApplicator _modApplicator;
    private readonly UsageStatisticsService _usageStatisticsService;
    private readonly ApplicationAdmissionController _applicationAdmissionController;

    public CharacterApplicationPipeline(PairHandler handler, ILogger logger, SnowMediator mediator, Pair pair,
        PairAppliedState appliedState, BackgroundTaskTracker backgroundTasks, CancellationTokenSource runtimeCts,
        SingleFlightCts applicationFlight, SingleFlightCts downloadFlight, SemaphoreSlim applicationGate,
        FileDownloadManager downloadManager, PlayerPerformanceService playerPerformanceService, IpcManager ipcManager,
        IApplyGameState gameState, FileCacheManager fileDbManager, DatabaseService databaseService,
        GameObjectHandlerFactory gameObjectHandlerFactory, ModNullificationService modNullificationService,
        UsageStatisticsService usageStatisticsService, ApplicationAdmissionController applicationAdmissionController)
    {
        _handler = handler;
        Logger = logger;
        Mediator = mediator;
        Pair = pair;
        _appliedState = appliedState;
        _backgroundTasks = backgroundTasks;
        _runtimeCts = runtimeCts;
        _applicationFlight = applicationFlight;
        _downloadFlight = downloadFlight;
        _applicationGate = applicationGate;
        _downloadManager = downloadManager;
        _playerPerformanceService = playerPerformanceService;
        _gameState = gameState;
        _fileDbManager = fileDbManager;
        _databaseService = databaseService;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _modNullificationService = modNullificationService;
        _usageStatisticsService = usageStatisticsService;
        _applicationAdmissionController = applicationAdmissionController;
        _modApplicator = new ModApplicator(ipcManager.Penumbra, ipcManager.Glamourer, ipcManager.CustomizePlus,
            ipcManager.Heels, ipcManager.Honorific, ipcManager.Moodles, ipcManager.PetNames, _gameState);
    }

    public CharacterData FilterReceivedData(CharacterData raw, PairFilterContext context)
    {
        var data = raw.Clone();
        if (context.Paused)
        {
            return data;
        }

        if (context.DisableAnimations || context.DisableSounds || context.DisableVFX)
        {
            foreach (var objectKind in data.FileReplacements.Select(k => k.Key).ToList())
            {
                if (context.DisableSounds)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("scd", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                if (context.DisableAnimations)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("tmb", StringComparison.OrdinalIgnoreCase) || p.EndsWith("pap", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                if (context.DisableVFX)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("atex", StringComparison.OrdinalIgnoreCase) || p.EndsWith("avfx", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
            }
        }

        _ = _modNullificationService.Apply(data, context.IsWhitelisted);
        return data;
    }

    public void DownloadAndApplyCharacter(CharacterData charaData, CharacterDataChangeSet updatedData)
    {
        if (updatedData.Count == 0)
        {
            LogNothingToUpdate(_handler);
            return;
        }

        var updateModdedPaths = updatedData.ContainsAny(PlayerChanges.ModFiles);
        var updateManip = updatedData.ContainsAny(PlayerChanges.ModManip);


        var downloadScope = _downloadFlight.Begin(_runtimeCts.Token);

        _ = _backgroundTasks.Track(
            DownloadAndApplyCharacterAsync(downloadScope, charaData, updatedData, updateModdedPaths, updateManip),
            nameof(DownloadAndApplyCharacterAsync));
    }

    private async Task DownloadAndApplyCharacterAsync(SingleFlightCts.Scope downloadScope, CharacterData charaData, CharacterDataChangeSet updatedData,
        bool updateModdedPaths, bool updateManip)
    {
        using var downloadLifetime = downloadScope;
        var downloadToken = downloadScope.Token;

        try
        {
            await DownloadAndApplyCharacterInternalAsync(charaData, updatedData, updateModdedPaths, updateManip, downloadToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "FileTransferManager is not initialized", StringComparison.Ordinal))
        {
            LogSkippingDownloadNotInitialized(_handler);
        }
    }

    private async Task DownloadAndApplyCharacterInternalAsync(CharacterData charaData, CharacterDataChangeSet updatedData,
        bool updateModdedPaths, bool updateManip, CancellationToken downloadToken)
    {
        Dictionary<(string GamePath, string? Hash), string> moddedPaths = [];
        Dictionary<string, long> moddedFileSizes = new(StringComparer.OrdinalIgnoreCase);

        if (updateModdedPaths)
        {
            int attempts = 0;
            List<FileReplacementData> toDownloadReplacements = TryCalculateModdedDictionary(charaData, out moddedPaths, out moddedFileSizes, downloadToken);

            while (toDownloadReplacements.Count > 0 && attempts++ <= 10 && !downloadToken.IsCancellationRequested)
            {
                if (_handler.PairDownloadTask != null && !_handler.PairDownloadTask.IsCompleted)
                {
                    LogFinishingPriorDownload(_handler.PlayerName, updatedData);
                    await _handler.PairDownloadTask.ConfigureAwait(false);
                }

                LogDownloadingMissingFiles(_handler.PlayerName, updatedData);

                Mediator.Publish(new EventMessage(new Event(_handler.PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
                    $"Starting download for {toDownloadReplacements.Count} files")));
                var toDownloadFiles = await _downloadManager.InitiateDownloadList(_handler.CharaHandler!, toDownloadReplacements, downloadToken).ConfigureAwait(false);

                if (!_playerPerformanceService.ComputeAndAutoPauseOnVRAMUsageThresholds(_handler, charaData, toDownloadFiles, affect: true))
                {
                    return;
                }

                _handler.PairDownloadTask = Task.Run(async () => await _downloadManager.DownloadFiles(_handler.CharaHandler!, toDownloadReplacements, downloadToken, Pair.UserData.UID).ConfigureAwait(false));

                await _handler.PairDownloadTask.ConfigureAwait(false);

                if (downloadToken.IsCancellationRequested)
                {
                    LogDetectedCancellation();
                    return;
                }

                toDownloadReplacements = TryCalculateModdedDictionary(charaData, out moddedPaths, out moddedFileSizes, downloadToken);

                if (toDownloadReplacements.TrueForAll(c => _downloadManager.IsHashForbidden(c.Hash)))
                {
                    break;
                }
            }

            if (!await _playerPerformanceService.CheckBothThresholds(_handler, charaData).ConfigureAwait(false))
                return;
        }

        downloadToken.ThrowIfCancellationRequested();

        _applicationFlight.Cancel();
        await _applicationAdmissionController.WaitForSlotAsync(Pair, updatedData.ContainsAny(PlayerChanges.ForcedRedraw), downloadToken).ConfigureAwait(false);
        await _applicationGate.WaitAsync(downloadToken).ConfigureAwait(false);
        try
        {
            if (downloadToken.IsCancellationRequested) return;

            using var appScope = _applicationFlight.Begin(_runtimeCts.Token);
            var token = appScope.Token;

            _handler.ApplicationTask = ApplyCharacterDataAsync(charaData, updatedData, updateModdedPaths, updateManip, moddedPaths, moddedFileSizes, token);
            _ = _backgroundTasks.Track(_handler.ApplicationTask, nameof(ApplyCharacterDataAsync));
            await _handler.ApplicationTask.ConfigureAwait(false);
        }
        finally
        {
            _applicationGate.Release();
        }
    }

    private async Task ApplyCharacterDataAsync(CharacterData charaData, CharacterDataChangeSet updatedData, bool updateModdedPaths, bool updateManip,
        Dictionary<(string GamePath, string? Hash), string> moddedPaths, Dictionary<string, long> moddedFileSizes, CancellationToken token)
    {
        _handler.ApplicationId = Guid.NewGuid();
        using var appScope = Logger.BeginScope("{ApplicationId}", _handler.ApplicationId);
        try
        {
            LogStartingApplicationTask(_handler);

            var handler = _handler.CharaHandler;
            if (handler == null)
            {
                LogAbortingNullHandler(_handler);
                return;
            }

            async Task<ushort?> TryGetObjectIndexAsync()
            {
                try
                {
                    var index = await Service.RunOnFrameworkAsync(() => handler.GetGameObject()?.ObjectIndex ?? ushort.MaxValue).ConfigureAwait(false);
                    return index == ushort.MaxValue ? null : index;
                }
                catch (Exception ex) when (ex is NullReferenceException or AccessViolationException)
                {
                    LogFailedResolveObjectIndex(ex, handler);
                    return null;
                }
            }
            var applied = await _modApplicator.ApplyModsAsync(Logger, _handler, handler, Pair.UserData.UID, TryGetObjectIndexAsync,
                _handler.ApplicationId, updateModdedPaths, updateManip,
                moddedPaths.ToDictionary(k => k.Key.GamePath, k => k.Value, StringComparer.Ordinal), charaData.ManipulationData, token).ConfigureAwait(false);
            if (!applied) return;

            if (updateModdedPaths)
            {
                _handler.LastAppliedDataBytes = -1;
                foreach (var path in moddedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    long length;
                    if (moddedFileSizes.TryGetValue(path, out var cachedSize))
                    {
                        length = cachedSize;
                    }
                    else
                    {
                        var fileInfo = new FileInfo(path);
                        if (!fileInfo.Exists) continue;
                        length = fileInfo.Length;
                    }

                    if (_handler.LastAppliedDataBytes == -1) _handler.LastAppliedDataBytes = 0;

                    _handler.LastAppliedDataBytes += length;
                }
            }

            token.ThrowIfCancellationRequested();

            foreach (var kind in updatedData)
            {
                await ApplyCustomizationDataAsync(_handler.ApplicationId, kind, charaData, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            _appliedState.CachedData = charaData;
            if (updateModdedPaths)
            {
                _usageStatisticsService.RecordAppliedLoad(Pair.LastAppliedApproximateVRAMBytes, Pair.LastAppliedDataTris, _handler.LastAppliedDataBytes);
            }
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));
            Mediator.Publish(new PairApplicationCompletedMessage(Pair.UserData.UID, charaData));

            LogApplicationFinished();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogDetectedCancellation();
        }
        catch (Exception ex)
        {
            var handler = _handler.CharaHandler;
            if (handler == null || handler.Address == nint.Zero)
            {
                // The character object went away mid-application: mark invisible so it is
                // re-detected, force a re-apply, and keep the data cached for the retry.
                _handler.IsVisible = false;
                _appliedState.ForceApplyMods = true;
                _appliedState.CachedData = charaData;
                Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));
                LogPlayerTurnedNull(ex);
            }
            else
            {
                LogApplicationFailed(ex);
            }
        }
    }

    private async Task ApplyCustomizationDataAsync(Guid applicationId, KeyValuePair<ObjectKind, IReadOnlyList<PlayerChanges>> changes, CharacterData charaData, CancellationToken token)
    {
        if (_handler.PlayerCharacter == nint.Zero) return;
        var ptr = _handler.PlayerCharacter;
        _appliedState.LastKnownPlayerAddress = ptr;

        var handler = changes.Key switch
        {
            ObjectKind.Player => _handler.CharaHandler!,
            ObjectKind.Companion => await _gameObjectHandlerFactory.Create(changes.Key, () => _gameState.GetCompanion(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.MinionOrMount => await _gameObjectHandlerFactory.Create(changes.Key, () => _gameState.GetMinionOrMount(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.Pet => await _gameObjectHandlerFactory.Create(changes.Key, () => _gameState.GetPet(ptr), isWatched: false).ConfigureAwait(false),
            _ => throw new NotSupportedException("ObjectKind not supported: " + changes.Key)
        };

        try
        {
            if (handler.Address == nint.Zero)
            {
                return;
            }

            await _modApplicator.ApplyKindAsync(Logger, handler, changes.Key, changes.Value, charaData, _appliedState, applicationId, token).ConfigureAwait(false);
        }
        finally
        {
            if (handler != _handler.CharaHandler) handler.Dispose();
        }
    }

    private List<FileReplacementData> TryCalculateModdedDictionary(CharacterData charaData, out Dictionary<(string GamePath, string? Hash), string> moddedDictionary, out Dictionary<string, long> moddedFileSizes, CancellationToken token)
    {
        Stopwatch st = Stopwatch.StartNew();
        ConcurrentBag<FileReplacementData> missingFiles = [];
        moddedDictionary = [];
        moddedFileSizes = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<(string GamePath, string? Hash), string> outputDict = new();
        ConcurrentDictionary<string, long> sizeByPath = new(StringComparer.OrdinalIgnoreCase);
        bool hasMigrationChanges = false;

        try
        {
            var replacementList = charaData.FileReplacements.SelectMany(k => k.Value.Where(v => string.IsNullOrEmpty(v.FileSwapPath))).ToList();
            Parallel.ForEach(replacementList, new ParallelOptions()
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = 4
            },
            (item) =>
            {
                token.ThrowIfCancellationRequested();
                var fileCache = _fileDbManager.GetFileCacheByHash(item.Hash, preferSubst: true);
                if (fileCache != null)
                {
                    if (string.IsNullOrEmpty(new FileInfo(fileCache.ResolvedFilepath).Extension))
                    {
                        hasMigrationChanges = true;
                        fileCache = _fileDbManager.MigrateFileHashToExtension(fileCache, item.GamePaths[0].Split(".")[^1]);
                    }
                    _databaseService.RecordFileSeen(Pair.UserData.UID, item.Hash, DateTime.UtcNow);

                    if (fileCache.Size is long size && size >= 0)
                    {
                        sizeByPath[fileCache.ResolvedFilepath] = size;
                    }

                    foreach (var gamePath in item.GamePaths)
                    {
                        outputDict[(gamePath, item.Hash)] = fileCache.ResolvedFilepath;
                    }
                }
                else
                {
                    LogMissingFile(item.Hash);
                    missingFiles.Add(item);
                }
            });

            moddedDictionary = outputDict.ToDictionary(k => k.Key, k => k.Value);
            moddedFileSizes = sizeByPath.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);

            foreach (var item in charaData.FileReplacements.SelectMany(k => k.Value.Where(v => !string.IsNullOrEmpty(v.FileSwapPath))).ToList())
            {
                foreach (var gamePath in item.GamePaths)
                {
                    LogAddingFileSwap(gamePath, item.FileSwapPath);
                    moddedDictionary[(gamePath, null)] = item.FileSwapPath;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCalcReplacementsError(ex);
        }
        if (hasMigrationChanges) _fileDbManager.WriteOutFullIndex();
        st.Stop();
        LogModdedPathsCalculated(st.ElapsedMilliseconds, missingFiles.Count, moddedDictionary.Keys.Count);
        return [.. missingFiles];
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Nothing to update for {obj}")]
    private partial void LogNothingToUpdate(object obj);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping download for {obj} because file transfers are not initialized yet")]
    private partial void LogSkippingDownloadNotInitialized(object obj);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Finishing prior running download task for player {name}, {kind}")]
    private partial void LogFinishingPriorDownload(string? name, object kind);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Downloading missing files for player {name}, {kind}")]
    private partial void LogDownloadingMissingFiles(string? name, object kind);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Detected cancellation")]
    private partial void LogDetectedCancellation();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting application task for {obj}")]
    private partial void LogStartingApplicationTask(object obj);

    [LoggerMessage(Level = LogLevel.Information, Message = "Aborting application task, character handler is null for {obj}")]
    private partial void LogAbortingNullHandler(object obj);

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed to resolve object index for {handler}")]
    private partial void LogFailedResolveObjectIndex(Exception ex, object handler);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Application finished")]
    private partial void LogApplicationFinished();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Player turned null during application")]
    private partial void LogPlayerTurnedNull(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Application failed")]
    private partial void LogApplicationFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Missing file: {hash}")]
    private partial void LogMissingFile(string hash);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Adding file swap for {path}: {fileSwap}")]
    private partial void LogAddingFileSwap(string path, string fileSwap);

    [LoggerMessage(Level = LogLevel.Error, Message = "Something went wrong during calculation replacements")]
    private partial void LogCalcReplacementsError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ModdedPaths calculated in {time}ms, missing files: {count}, total files: {total}")]
    private partial void LogModdedPathsCalculated(long time, int count, int total);
}
