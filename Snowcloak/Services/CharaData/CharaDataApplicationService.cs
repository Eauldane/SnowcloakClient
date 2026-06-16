using Dalamud.Game.ClientState.Objects.Types;
using K4os.Compression.LZ4.Legacy;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using ElezenTools.Core.Async;
using Microsoft.Extensions.Logging;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Text;

using ElezenTools.Services;

namespace Snowcloak.Services.CharaData;

public sealed partial class CharaDataApplicationService : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly CharaDataStateConfigService _stateConfigService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly CharaDataFileHandler _fileHandler;
    private readonly IpcManager _ipcManager;
    private readonly MetaInfoCache _metaInfoCache;
    private readonly CharaDataCharacterHandler _characterHandler;
    private readonly SingleFlightCts _applicationCts = new();
    private Task? _currentOperation;

    public CharaDataApplicationService(ILogger<CharaDataApplicationService> logger, SnowMediator mediator,
        ApiController apiController, CharaDataStateConfigService stateConfigService, DalamudUtilService dalamudUtilService,
        CharaDataFileHandler fileHandler, IpcManager ipcManager, MetaInfoCache metaInfoCache,
        CharaDataCharacterHandler characterHandler) : base(logger, mediator)
    {
        _apiController = apiController;
        _stateConfigService = stateConfigService;
        _dalamudUtilService = dalamudUtilService;
        _fileHandler = fileHandler;
        _ipcManager = ipcManager;
        _metaInfoCache = metaInfoCache;
        _characterHandler = characterHandler;
    }

    public AsyncOp DataApplication { get; } = new();
    public AsyncOp AttachingPose { get; } = new();
    public ValueProgress<string> ApplicationProgress { get; } = new();

    public bool IsBusy => DataApplication.IsRunning || AttachingPose.IsRunning || _currentOperation is { IsCompleted: false };

    public Task ApplyCharaData(CharaDataDownloadDto dataDownloadDto, string charaName)
        => TrackDataApplication(() => ApplyDownloadToNameAsync(dataDownloadDto, charaName));

    public Task ApplyCharaData(CharaDataMetaInfoDto dataMetaInfoDto, string charaName)
        => TrackDataApplication(() => ApplyMetaInfoToNameAsync(dataMetaInfoDto, charaName));

    public Task ApplyCharaDataToGposeTarget(CharaDataMetaInfoDto dataMetaInfoDto)
        => TrackDataApplication(() => ApplyMetaInfoToGposeTargetAsync(dataMetaInfoDto));

    public async Task ApplyOwnDataToGposeTarget(CharaDataFullExtendedDto dataDto)
    {
        var chara = await _dalamudUtilService.GetGposeTargetGameObjectAsync().ConfigureAwait(false);
        var charaName = chara?.Name.TextValue ?? string.Empty;
        _ = TrackDataApplication(() => DownloadAndApplyDataAsync(charaName, dataDto.ToDownloadDto(), dataDto.ToMetaInfoDto(), false));
    }

    public void ApplyDataToSelf(CharaDataFullExtendedDto dataDto)
    {
        var chara = _dalamudUtilService.GetPlayerName();
        _ = TrackDataApplication(() => DownloadAndApplyDataAsync(chara, dataDto.ToDownloadDto(), dataDto.ToMetaInfoDto()));
    }

    public Task ApplyPoseData(PoseEntry pose, string targetName)
        => TrackBackgroundOperation(() => ApplyPoseDataToTargetAsync(pose, targetName));

    public Task ApplyWorldDataToTarget(PoseEntry pose, string targetName)
        => TrackBackgroundOperation(() => ApplyWorldDataToTargetAsync(pose, targetName));

    public void ApplyFullPoseDataToGposeTarget(PoseEntry value)
        => _ = TrackBackgroundOperation(() => ApplyFullPoseDataToGposeTargetAsync(value));

    public void AttachWorldData(PoseEntry pose, CharaDataExtendedUpdateDto updateDto)
    {
        _ = AttachingPose.Run(async () =>
        {
            ICharacter? playerChar = await _dalamudUtilService.GetPlayerCharacterAsync().ConfigureAwait(false);
            if (playerChar == null) return;
            if (_dalamudUtilService.IsInGpose)
            {
                playerChar = await _dalamudUtilService.GetGposeCharacterFromObjectTableByNameAsync(playerChar.Name.TextValue, true).ConfigureAwait(false);
            }
            if (playerChar == null) return;
            var worldData = await _ipcManager.Brio.GetTransformAsync(playerChar.Address).ConfigureAwait(false);
            if (worldData == default) return;

            LogAttachingWorldData(Logger, worldData);

            worldData.LocationInfo = await _dalamudUtilService.GetMapDataAsync().ConfigureAwait(false);

            LogWorldDataSerialised(Logger, worldData);

            pose.WorldData = worldData;

            updateDto.UpdatePoseList();
        });
    }

    public void AttachPoseData(PoseEntry pose, CharaDataExtendedUpdateDto updateDto)
    {
        _ = AttachingPose.Run(async () =>
        {
            ICharacter? playerChar = await _dalamudUtilService.GetPlayerCharacterAsync().ConfigureAwait(false);
            if (playerChar == null) return;
            if (_dalamudUtilService.IsInGpose)
            {
                playerChar = await _dalamudUtilService.GetGposeCharacterFromObjectTableByNameAsync(playerChar.Name.TextValue, true).ConfigureAwait(false);
            }
            if (playerChar == null) return;
            var poseData = await _ipcManager.Brio.GetPoseAsync(playerChar.Address).ConfigureAwait(false);
            if (poseData == null) return;

            var compressedByteData = LZ4Wrapper.WrapHC(Encoding.UTF8.GetBytes(poseData));
            pose.PoseData = Convert.ToBase64String(compressedByteData);
            updateDto.UpdatePoseList();
        });
    }

    public Task<HandledCharaDataEntry?> SpawnAndApplyData(CharaDataDownloadDto charaDataDownloadDto)
        => SpawnAndApply(name => ApplyCharaData(charaDataDownloadDto, name));

    public Task<HandledCharaDataEntry?> SpawnAndApplyData(CharaDataMetaInfoDto charaDataMetaInfoDto)
        => SpawnAndApply(name => ApplyCharaData(charaDataMetaInfoDto, name));

    private Task<HandledCharaDataEntry?> SpawnAndApply(Func<string, Task> applyByName)
    {
        var task = Task.Run(() => SpawnAndApplyAsync(applyByName));
        _currentOperation = task;
        return task;
    }

    public void SpawnAndApplyWorldTransform(CharaDataMetaInfoDto metaInfo, PoseEntry value)
        => _ = TrackBackgroundOperation(() => SpawnAndApplyWorldTransformAsync(metaInfo, value));

    private Task TrackDataApplication(Func<Task> operation)
    {
        var task = DataApplication.Run(operation);
        _currentOperation = task;
        return task;
    }

    private Task TrackBackgroundOperation(Func<Task> operation)
    {
        var task = Task.Run(operation);
        _currentOperation = task;
        return task;
    }

    private async Task ApplyDownloadToNameAsync(CharaDataDownloadDto dataDownloadDto, string charaName)
    {
        if (string.IsNullOrEmpty(charaName)) return;

        CharaDataMetaInfoDto metaInfo = new(dataDownloadDto.Id, dataDownloadDto.Uploader)
        {
            CanBeDownloaded = true,
            Description = string.Format(CultureInfo.InvariantCulture,
                "Data from {0} for {1}", dataDownloadDto.Uploader.AliasOrUID, dataDownloadDto.Id),
            UpdatedDate = dataDownloadDto.UpdatedDate,
        };

        await DownloadAndApplyDataAsync(charaName, dataDownloadDto, metaInfo, false).ConfigureAwait(false);
    }

    private async Task ApplyMetaInfoToNameAsync(CharaDataMetaInfoDto dataMetaInfoDto, string charaName)
    {
        if (string.IsNullOrEmpty(charaName)) return;

        var download = await _apiController.CharaDataDownload(dataMetaInfoDto.Uploader.UID + ":" + dataMetaInfoDto.Id).ConfigureAwait(false);
        if (download == null)
        {
            return;
        }

        await DownloadAndApplyDataAsync(charaName, download, dataMetaInfoDto, false).ConfigureAwait(false);
    }

    private async Task ApplyMetaInfoToGposeTargetAsync(CharaDataMetaInfoDto dataMetaInfoDto)
    {
        var apply = await CanApplyInGpose().ConfigureAwait(false);
        if (!apply.CanApply) return;

        await ApplyMetaInfoToNameAsync(dataMetaInfoDto, apply.TargetName).ConfigureAwait(false);
    }

    private async Task ApplyPoseDataToTargetAsync(PoseEntry pose, string targetName)
    {
        if (string.IsNullOrEmpty(pose.PoseData) || !(await CanApplyInGpose().ConfigureAwait(false)).CanApply) return;

        var gposeChara = await GetGposeCharacterAsync(targetName).ConfigureAwait(false);
        if (gposeChara == null) return;

        var poseJson = DecodePoseData(pose.PoseData);
        if (string.IsNullOrEmpty(poseJson)) return;

        await _ipcManager.Brio.SetPoseAsync(gposeChara.Address, poseJson).ConfigureAwait(false);
    }

    private async Task ApplyWorldDataToTargetAsync(PoseEntry pose, string targetName)
    {
        var apply = await CanApplyInGpose().ConfigureAwait(false);
        if (!apply.CanApply || pose.WorldData is not { } worldData || worldData == default) return;

        var gposeChara = await GetGposeCharacterAsync(targetName).ConfigureAwait(false);
        if (gposeChara == null) return;

        LogApplyingWorldData(Logger, worldData);
        await _ipcManager.Brio.ApplyTransformAsync(gposeChara.Address, worldData).ConfigureAwait(false);
    }

    private async Task ApplyFullPoseDataToGposeTargetAsync(PoseEntry pose)
    {
        var apply = await CanApplyInGpose().ConfigureAwait(false);
        if (!apply.CanApply) return;

        await ApplyPoseDataToTargetAsync(pose, apply.TargetName).ConfigureAwait(false);
        await ApplyWorldDataToTargetAsync(pose, apply.TargetName).ConfigureAwait(false);
    }

    private async Task<HandledCharaDataEntry?> SpawnAndApplyAsync(Func<string, Task> applyByName)
    {
        var newActor = await _ipcManager.Brio.SpawnActorAsync().ConfigureAwait(false);
        if (newActor == null) return null;

        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await applyByName(newActor.Name.TextValue).ConfigureAwait(false);

        return _characterHandler.HandledCharaData.FirstOrDefault(entry => string.Equals(entry.Name, newActor.Name.TextValue, StringComparison.Ordinal));
    }

    private async Task SpawnAndApplyWorldTransformAsync(CharaDataMetaInfoDto metaInfo, PoseEntry pose)
    {
        var actor = await SpawnAndApplyAsync(name => DataApplication.Run(() => ApplyMetaInfoToNameAsync(metaInfo, name))).ConfigureAwait(false);
        if (actor == null) return;

        await ApplyPoseDataToTargetAsync(pose, actor.Name).ConfigureAwait(false);
        await ApplyWorldDataToTargetAsync(pose, actor.Name).ConfigureAwait(false);
    }

    private Task<ICharacter?> GetGposeCharacterAsync(string targetName)
        => _dalamudUtilService.GetGposeCharacterFromObjectTableByNameAsync(targetName, true);

    private static string DecodePoseData(string poseData)
        => Encoding.UTF8.GetString(LZ4Wrapper.Unwrap(Convert.FromBase64String(poseData)));

    public unsafe void TargetGposeActor(HandledCharaDataEntry actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var gposeActor = _dalamudUtilService.GetGposeCharacterFromObjectTableByName(actor.Name, true);
        if (gposeActor != null)
        {
            GposeService.GposeTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gposeActor.Address;
        }
    }

    public void RevertChara(HandledCharaDataEntry? handled)
    {
        _currentOperation = _characterHandler.RevertHandledChara(handled);
    }

    public void RemoveChara(string handledActor)
    {
        if (string.IsNullOrEmpty(handledActor)) return;
        _currentOperation = Task.Run(async () =>
        {
            await _characterHandler.RevertHandledChara(handledActor).ConfigureAwait(false);
            var gposeChara = await _dalamudUtilService.GetGposeCharacterFromObjectTableByNameAsync(handledActor, true).ConfigureAwait(false);
            if (gposeChara != null)
                await _ipcManager.Brio.DespawnActorAsync(gposeChara.Address).ConfigureAwait(false);
        });
    }

    public async Task<(bool CanApply, string TargetName)> CanApplyInGpose()
    {
        var obj = await _dalamudUtilService.GetGposeTargetGameObjectAsync().ConfigureAwait(false);
        string targetName;
        bool canApply = _dalamudUtilService.IsInGpose && obj != null
            && obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc;
        if (canApply)
        {
            targetName = obj!.Name.TextValue;
        }
        else
        {
            targetName = "Invalid Target";
        }
        return (canApply, targetName);
    }

    public void CancelDataApplication() => _applicationCts.Cancel();

    internal async Task ApplyDataAsync(Guid applicationId, GameObjectHandler tempHandler, bool isSelf, bool autoRevert,
        CharaDataMetaInfoExtendedDto metaInfo, Dictionary<string, string> modPaths, string? manipData, string? glamourerData, string? customizeData, CancellationToken token)
    {
        Guid? cPlusId = null;
        try
        {
            await RevertExistingAsync(tempHandler).ConfigureAwait(false);
            cPlusId = await ApplyAppearanceAsync(applicationId, tempHandler, metaInfo, modPaths, manipData, glamourerData, customizeData, token).ConfigureAwait(false);

            if (autoRevert)
            {
                await AutoRevertCountdownAsync(token).ConfigureAwait(false);
            }
            else
            {
                LogAddingToHandledObjects(Logger, tempHandler.Name);
                _characterHandler.AddHandledChara(new HandledCharaDataEntry(tempHandler.Name, isSelf, cPlusId, metaInfo));
            }
        }
        finally
        {
            await FinishApplicationAsync(tempHandler, autoRevert, cPlusId, metaInfo, token).ConfigureAwait(false);
        }
    }

    private async Task RevertExistingAsync(GameObjectHandler tempHandler)
    {
        ApplicationProgress.Report("Reverting previous Application");
        LogRevertingChara(Logger, tempHandler.Name);
        bool reverted = await _characterHandler.RevertHandledChara(tempHandler.Name).ConfigureAwait(false);
        if (reverted)
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    private async Task<Guid?> ApplyAppearanceAsync(Guid applicationId, GameObjectHandler tempHandler, CharaDataMetaInfoExtendedDto metaInfo,
        Dictionary<string, string> modPaths, string? manipData, string? glamourerData, string? customizeData, CancellationToken token)
    {
        LogApplyingDataInPenumbra(Logger);
        ApplicationProgress.Report("Applying Penumbra information");
        var penumbraCollection = await _ipcManager.Penumbra.CreateTemporaryCollectionAsync(Logger, metaInfo.Uploader.UID + metaInfo.Id).ConfigureAwait(false);
        var idx = await Service.RunOnFrameworkAsync(() => tempHandler.GetGameObject()?.ObjectIndex).ConfigureAwait(false) ?? 0;
        await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, penumbraCollection, idx).ConfigureAwait(false);
        await _ipcManager.Penumbra.SetTemporaryModsAsync(Logger, applicationId, penumbraCollection, modPaths).ConfigureAwait(false);
        await _ipcManager.Penumbra.SetManipulationDataAsync(Logger, applicationId, penumbraCollection, manipData ?? string.Empty).ConfigureAwait(false);

        LogApplyingGlamourerData(Logger);
        ApplicationProgress.Report("Applying Glamourer and redrawing Character");
        await _ipcManager.Glamourer.ApplyAllAsync(Logger, tempHandler, glamourerData, applicationId, token).ConfigureAwait(false);
        await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, token).ConfigureAwait(false);
        await ObjectTableCache.WaitWhileCharacterIsDrawing(Logger, tempHandler, applicationId, ct: token).ConfigureAwait(false);
        LogRemovingPenumbraCollection(Logger);
        await _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(Logger, applicationId, penumbraCollection).ConfigureAwait(false);

        ApplicationProgress.Report("Applying Customize+ data");
        LogApplyingCustomizeData(Logger);
        var bodyScale = string.IsNullOrEmpty(customizeData) ? Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")) : customizeData;
        return await _ipcManager.CustomizePlus.SetBodyScaleAsync(tempHandler.Address, bodyScale).ConfigureAwait(false);
    }

    private async Task AutoRevertCountdownAsync(CancellationToken token)
    {
        LogStartingAutoRevertWait(Logger);
        for (int i = 15; i > 0; i--)
        {
            ApplicationProgress.Report(string.Format(CultureInfo.InvariantCulture,
                "All data applied. Reverting automatically in {0} seconds.", i));
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    private async Task FinishApplicationAsync(GameObjectHandler tempHandler, bool autoRevert, Guid? cPlusId, CharaDataMetaInfoExtendedDto metaInfo, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            ApplicationProgress.Report("Application aborted. Reverting Character...");
        else if (autoRevert)
            ApplicationProgress.Report("Application finished. Reverting Character...");

        if (autoRevert)
            await _characterHandler.RevertChara(tempHandler.Name, cPlusId).ConfigureAwait(false);

        if (!_dalamudUtilService.IsInGpose)
            Mediator.Publish(new HaltCharaDataCreation(Resume: true));

        MarkFavoriteDownloaded(metaInfo);
        ApplicationProgress.Report(string.Empty);
    }

    private void MarkFavoriteDownloaded(CharaDataMetaInfoExtendedDto metaInfo)
    {
        if (_stateConfigService.Current.FavoriteCodes.TryGetValue(metaInfo.Uploader.UID + ":" + metaInfo.Id, out var favorite) && favorite != null)
        {
            _stateConfigService.Update(_ => favorite.LastDownloaded = DateTime.UtcNow);
        }
    }

    private async Task DownloadAndApplyDataAsync(string charaName, CharaDataDownloadDto charaDataDownloadDto, CharaDataMetaInfoDto metaInfo, bool autoRevert = true)
    {
        using var scope = _applicationCts.Begin();
        var token = scope.Token;
        ICharacter? chara = (await _dalamudUtilService.GetGposeCharacterFromObjectTableByNameAsync(charaName, _dalamudUtilService.IsInGpose).ConfigureAwait(false));

        if (chara == null)
            return;

        var applicationId = Guid.NewGuid();

        var playerChar = await _dalamudUtilService.GetPlayerCharacterAsync().ConfigureAwait(false);
        bool isSelf = playerChar != null && string.Equals(playerChar.Name.TextValue, chara.Name.TextValue, StringComparison.Ordinal);

        ApplicationProgress.Report("Checking local files");

        LogComputingLocalMissingFiles(Logger, applicationId);

        Dictionary<string, string> modPaths;
        List<FileReplacementData> missingFiles;
        _fileHandler.ComputeMissingFiles(charaDataDownloadDto, out modPaths, out missingFiles);

        using GameObjectHandler? tempHandler = await _characterHandler.TryCreateGameObjectHandler(chara.ObjectIndex).ConfigureAwait(false);
        if (tempHandler == null) return;

        if (missingFiles.Count != 0)
        {
            try
            {
                ApplicationProgress.Report("Downloading Missing Files. Please be patient.");
                await _fileHandler.DownloadFilesAsync(tempHandler, missingFiles, modPaths, token).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                ApplicationProgress.Report("Failed to download one or more files. Aborting.");
                return;
            }
            catch (OperationCanceledException)
            {
                ApplicationProgress.Report("Application aborted.");
                return;
            }
        }

        if (!_dalamudUtilService.IsInGpose)
            Mediator.Publish(new HaltCharaDataCreation());

        var extendedMetaInfo = await _metaInfoCache.CacheMeta(metaInfo).ConfigureAwait(false);

        await ApplyDataAsync(applicationId, tempHandler, isSelf, autoRevert, extendedMetaInfo, modPaths, charaDataDownloadDto.ManipulationData, charaDataDownloadDto.GlamourerData,
            charaDataDownloadDto.CustomizeData, token).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        _applicationCts.Cancel();
        try
        {
            Task.WhenAll(GetKnownTasks()).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            LogApplicationTaskDrainFailed(Logger, ex);
        }
        _applicationCts.Dispose();
    }

    private IEnumerable<Task> GetKnownTasks()
    {
        Task?[] tasks = [DataApplication.Task, AttachingPose.Task, _currentOperation];
        return tasks.Where(task => task is { IsCompleted: false }).Cast<Task>();
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Trace, Message = "Attaching World data {WorldData}")]
    private static partial void LogAttachingWorldData(ILogger logger, WorldData worldData);

    [LoggerMessage(EventId = 1, Level = LogLevel.Trace, Message = "World data serialised: {WorldData}")]
    private static partial void LogWorldDataSerialised(ILogger logger, WorldData worldData);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Applying World data {WorldData}")]
    private static partial void LogApplyingWorldData(ILogger logger, WorldData worldData);

    [LoggerMessage(EventId = 3, Level = LogLevel.Trace, Message = "Adding {Name} to handled objects")]
    private static partial void LogAddingToHandledObjects(ILogger logger, string name);

    [LoggerMessage(EventId = 4, Level = LogLevel.Trace, Message = "Reverting chara {Name}")]
    private static partial void LogRevertingChara(ILogger logger, string name);

    [LoggerMessage(EventId = 5, Level = LogLevel.Trace, Message = "Applying data in Penumbra")]
    private static partial void LogApplyingDataInPenumbra(ILogger logger);

    [LoggerMessage(EventId = 6, Level = LogLevel.Trace, Message = "Applying Glamourer data and redrawing")]
    private static partial void LogApplyingGlamourerData(ILogger logger);

    [LoggerMessage(EventId = 7, Level = LogLevel.Trace, Message = "Removing collection")]
    private static partial void LogRemovingPenumbraCollection(ILogger logger);

    [LoggerMessage(EventId = 8, Level = LogLevel.Trace, Message = "Applying C+ data")]
    private static partial void LogApplyingCustomizeData(ILogger logger);

    [LoggerMessage(EventId = 9, Level = LogLevel.Trace, Message = "Starting wait for auto revert")]
    private static partial void LogStartingAutoRevertWait(ILogger logger);

    [LoggerMessage(EventId = 10, Level = LogLevel.Trace, Message = "Computing local missing files for {ApplicationId}")]
    private static partial void LogComputingLocalMissingFiles(ILogger logger, Guid applicationId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug, Message = "Application service task drain observed a failure")]
    private static partial void LogApplicationTaskDrainFailed(ILogger logger, Exception exception);
}
