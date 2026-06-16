using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using ElezenTools.Core.Async;
using Microsoft.Extensions.Logging;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using System.Threading;

namespace Snowcloak.Services.CharaData;

public sealed class CharaDataManager : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private readonly CharaDataConfigService _configService;
    private readonly IpcManager _ipcManager;
    private readonly MetaInfoCache _metaInfoCache;
    private readonly CharaDataCharacterHandler _characterHandler;
    private readonly OwnCharaDataStore _ownStore;
    private readonly SharedCharaDataStore _sharedStore;
    private readonly CharaDataApplicationService _applicationService;
    private readonly McdfService _mcdfService;
    private readonly SingleFlightCts _connectCts = new();
    private int _disposed;

    public CharaDataManager(ILogger<CharaDataManager> logger, SnowMediator snowMediator,
        IpcManager ipcManager, CharaDataConfigService charaDataConfigService, MetaInfoCache metaInfoCache,
        CharaDataCharacterHandler charaDataCharacterHandler, OwnCharaDataStore ownStore, SharedCharaDataStore sharedStore,
        CharaDataApplicationService applicationService, McdfService mcdfService) : base(logger, snowMediator)
    {
        _ipcManager = ipcManager;
        _configService = charaDataConfigService;
        _metaInfoCache = metaInfoCache;
        _characterHandler = charaDataCharacterHandler;
        _ownStore = ownStore;
        _sharedStore = sharedStore;
        _applicationService = applicationService;
        _mcdfService = mcdfService;
        snowMediator.Subscribe<ConnectedMessage>(this, (msg) =>
        {
            _connectCts.Cancel();
            ResetState();
            _ownStore.MaxCreatableCharaData = msg.Connection.ServerInfo.MaxCharaData;
            if (_configService.Current.DownloadMcdDataOnConnection)
            {
                var scope = _connectCts.Begin();
                var loads = Task.WhenAll(_ownStore.GetAllData(scope.Token), _sharedStore.GetAllSharedData(scope.Token));
                _ = loads.ContinueWith(_ => scope.Dispose(), TaskScheduler.Default);
            }
        });
        snowMediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            ResetState();
        });
    }

    private void ResetState()
    {
        _ownStore.Reset();
        _sharedStore.Reset();
        _metaInfoCache.Clear();
    }

    public AsyncOp AttachingPose => _applicationService.AttachingPose;
    public AsyncOp CharaUpdate => _ownStore.CharaUpdate;
    public string DataApplicationProgress => _applicationService.ApplicationProgress.Value ?? string.Empty;
    public AsyncOp DataApplication => _applicationService.DataApplication;
    public AsyncOp<(string Output, bool Success)> DataCreation => _ownStore.DataCreation;
    public AsyncOp<(string Result, bool Success)> MetaInfoDownload => _sharedStore.MetaInfoDownload;
    public bool OwnDataDownloading => _ownStore.Download.IsRunning;
    public bool OwnDataOnCooldown => _ownStore.Cooldown.IsActive;
    public bool SharedDataDownloading => _sharedStore.Download.IsRunning;
    public bool SharedDataOnCooldown => _sharedStore.Cooldown.IsActive;
    public IEnumerable<HandledCharaDataEntry> HandledCharaData => _characterHandler.HandledCharaData;
    public bool Initialized => _ownStore.Initialized;
    public CharaDataMetaInfoExtendedDto? LastDownloadedMetaInfo => _sharedStore.LastDownloadedMetaInfo;
    public Task<(MareCharaFileHeader LoadedFile, long ExpectedLength)>? LoadedMcdfHeader => _mcdfService.LoadedMcdfHeader;
    public int MaxCreatableCharaData => _ownStore.MaxCreatableCharaData;
    public AsyncOp McdfApplication => _mcdfService.McdfApplication;
    public IDictionary<string, CharaDataFullExtendedDto> OwnCharaData => _ownStore.OwnCharaData;
    public IDictionary<UserData, List<CharaDataMetaInfoExtendedDto>> SharedWithYouData => _sharedStore.SharedWithYouData;
    public ValueProgress<string>? UploadProgress => _ownStore.UploadProgress;
    public AsyncOp<(string Output, bool Success)> Upload => _ownStore.Upload;
    public bool BrioAvailable => _ipcManager.Brio.APIAvailable;

    public bool IsBusy => _applicationService.IsBusy || _mcdfService.IsBusy
        || _ownStore.Download.IsRunning || _ownStore.DataCreation.IsRunning
        || _ownStore.CharaUpdate.IsRunning || _ownStore.Upload.IsRunning
        || _sharedStore.Download.IsRunning || _sharedStore.MetaInfoDownload.IsRunning;

    public Task ApplyCharaData(CharaDataDownloadDto dataDownloadDto, string charaName) => _applicationService.ApplyCharaData(dataDownloadDto, charaName);

    public Task ApplyCharaData(CharaDataMetaInfoDto dataMetaInfoDto, string charaName) => _applicationService.ApplyCharaData(dataMetaInfoDto, charaName);

    public Task ApplyCharaDataToGposeTarget(CharaDataMetaInfoDto dataMetaInfoDto) => _applicationService.ApplyCharaDataToGposeTarget(dataMetaInfoDto);

    public Task ApplyOwnDataToGposeTarget(CharaDataFullExtendedDto dataDto) => _applicationService.ApplyOwnDataToGposeTarget(dataDto);

    public Task ApplyPoseData(PoseEntry pose, string targetName) => _applicationService.ApplyPoseData(pose, targetName);

    public Task ApplyWorldDataToTarget(PoseEntry pose, string targetName) => _applicationService.ApplyWorldDataToTarget(pose, targetName);

    public void AttachWorldData(PoseEntry pose, CharaDataExtendedUpdateDto updateDto) => _applicationService.AttachWorldData(pose, updateDto);

    public Task<(bool CanApply, string TargetName)> CanApplyInGpose() => _applicationService.CanApplyInGpose();

    public void CancelDataApplication() => _applicationService.CancelDataApplication();

    public void CancelUpload() => _ownStore.CancelUpload();

    public void CreateCharaDataEntry(CancellationToken cancelToken) => _ownStore.CreateCharaDataEntry(cancelToken);

    public Task DeleteCharaData(CharaDataFullExtendedDto dto) => _ownStore.DeleteCharaData(dto);

    public void DownloadMetaInfo(string importCode, bool store = true) => _sharedStore.DownloadMetaInfo(importCode, store);

    public Task GetAllData(CancellationToken cancelToken) => _ownStore.GetAllData(cancelToken);

    public Task GetAllSharedData(CancellationToken token) => _sharedStore.GetAllSharedData(token);

    public CharaDataExtendedUpdateDto? GetUpdateDto(string id) => _ownStore.GetUpdateDto(id);

    public bool IsInTimeout(string key) => _metaInfoCache.IsInTimeout(key);

    public void LoadMcdf(string filePath) => _mcdfService.LoadMcdf(filePath);

    public void McdfApplyToTarget(string charaName) => _mcdfService.McdfApplyToTarget(charaName);

    public Task McdfApplyToGposeTarget() => _mcdfService.McdfApplyToGposeTarget();

    public void SaveMareCharaFile(string description, string filePath) => _mcdfService.SaveMareCharaFile(description, filePath);

    public void SetAppearanceData(string dtoId) => _ownStore.SetAppearanceData(dtoId);

    public Task<HandledCharaDataEntry?> SpawnAndApplyData(CharaDataDownloadDto charaDataDownloadDto) => _applicationService.SpawnAndApplyData(charaDataDownloadDto);

    public Task<HandledCharaDataEntry?> SpawnAndApplyData(CharaDataMetaInfoDto charaDataMetaInfoDto) => _applicationService.SpawnAndApplyData(charaDataMetaInfoDto);

    public bool TryGetMetaInfo(string key, out CharaDataMetaInfoExtendedDto? metaInfo) => _metaInfoCache.TryGet(key, out metaInfo);

    public void UploadAllCharaData() => _ownStore.UploadAllCharaData();

    public void UploadCharaData(string id) => _ownStore.UploadCharaData(id);

    public void UploadMissingFiles(string id) => _ownStore.UploadMissingFiles(id);

    public Task<(string Result, bool Success)> UploadFiles(List<GamePathEntry> missingFileList, Func<Task>? postUpload = null)
        => _ownStore.UploadFiles(missingFileList, postUpload);

    public void RevertChara(HandledCharaDataEntry? handled) => _applicationService.RevertChara(handled);

    internal void ApplyDataToSelf(CharaDataFullExtendedDto dataDto) => _applicationService.ApplyDataToSelf(dataDto);

    internal void AttachPoseData(PoseEntry pose, CharaDataExtendedUpdateDto updateDto) => _applicationService.AttachPoseData(pose, updateDto);

    internal void McdfSpawnApplyToGposeTarget() => _mcdfService.McdfSpawnApplyToGposeTarget();

    internal void ApplyFullPoseDataToGposeTarget(PoseEntry value) => _applicationService.ApplyFullPoseDataToGposeTarget(value);

    internal void SpawnAndApplyWorldTransform(CharaDataMetaInfoDto metaInfo, PoseEntry value) => _applicationService.SpawnAndApplyWorldTransform(metaInfo, value);

    internal void TargetGposeActor(HandledCharaDataEntry actor) => _applicationService.TargetGposeActor(actor);

    internal void RemoveChara(string handledActor) => _applicationService.RemoveChara(handledActor);

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing);
        if (!disposing) return;

        _connectCts.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
