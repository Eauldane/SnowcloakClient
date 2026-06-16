using Snowcloak.API.Data;
using ElezenTools.Core.Async;
using Microsoft.Extensions.Logging;
using Snowcloak.Interop.Ipc;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services.CharaData.Models;

namespace Snowcloak.Services.CharaData;

public sealed class McdfService : IDisposable
{
    private readonly ILogger<McdfService> _logger;
    private readonly CharaDataFileHandler _fileHandler;
    private readonly CharaDataCharacterHandler _characterHandler;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IpcManager _ipcManager;
    private readonly CharaDataApplicationService _applicationService;
    private Task? _currentOperation;

    public McdfService(ILogger<McdfService> logger, CharaDataFileHandler fileHandler,
        CharaDataCharacterHandler characterHandler, DalamudUtilService dalamudUtilService,
        IpcManager ipcManager, CharaDataApplicationService applicationService)
    {
        _logger = logger;
        _fileHandler = fileHandler;
        _characterHandler = characterHandler;
        _dalamudUtilService = dalamudUtilService;
        _ipcManager = ipcManager;
        _applicationService = applicationService;
    }

    public AsyncOp McdfApplication { get; } = new();
    public Task<(MareCharaFileHeader LoadedFile, long ExpectedLength)>? LoadedMcdfHeader { get; private set; }

    public bool IsBusy => McdfApplication.IsRunning || _currentOperation is { IsCompleted: false };

    public void LoadMcdf(string filePath)
    {
        LoadedMcdfHeader = _fileHandler.LoadCharaFileHeader(filePath);
    }

    public void McdfApplyToTarget(string charaName)
    {
        if (LoadedMcdfHeader == null || !LoadedMcdfHeader.IsCompletedSuccessfully) return;

        List<string> actuallyExtractedFiles = [];

        _ = McdfApplication.Run(async () =>
        {
            Guid applicationId = Guid.NewGuid();
            try
            {
                using GameObjectHandler? tempHandler = await _characterHandler.TryCreateGameObjectHandler(charaName, true).ConfigureAwait(false);
                if (tempHandler == null) return;
                var playerChar = await _dalamudUtilService.GetPlayerCharacterAsync().ConfigureAwait(false);
                bool isSelf = playerChar != null && string.Equals(playerChar.Name.TextValue, tempHandler.Name, StringComparison.Ordinal);

                long expectedExtractedSize = LoadedMcdfHeader.Result.ExpectedLength;
                var charaFile = LoadedMcdfHeader.Result.LoadedFile;
                _applicationService.ApplicationProgress.Report("Extracting MCDF data");

                var extractedFiles = _fileHandler.McdfExtractFiles(charaFile, expectedExtractedSize, actuallyExtractedFiles);

                foreach (var entry in charaFile.CharaFileData.FileSwaps.SelectMany(k => k.GamePaths, (k, p) => new KeyValuePair<string, string>(p, k.FileSwapPath)))
                {
                    extractedFiles[entry.Key] = entry.Value;
                }

                _applicationService.ApplicationProgress.Report("Applying MCDF data");

                var extended = await CharaDataMetaInfoExtendedDto.Create(new(charaFile.FilePath, new UserData(string.Empty)), _dalamudUtilService)
                    .ConfigureAwait(false);
                await _applicationService.ApplyDataAsync(applicationId, tempHandler, isSelf, autoRevert: false, extended,
                    extractedFiles, charaFile.CharaFileData.ManipulationData, charaFile.CharaFileData.GlamourerData,
                    charaFile.CharaFileData.CustomizePlusData, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract MCDF");
                throw;
            }
            finally
            {
                foreach (var file in actuallyExtractedFiles)
                {
                    File.Delete(file);
                }
            }
        });
    }

    public async Task McdfApplyToGposeTarget()
    {
        var apply = await _applicationService.CanApplyInGpose().ConfigureAwait(false);
        if (apply.CanApply)
        {
            McdfApplyToTarget(apply.TargetName);
        }
    }

    public void McdfSpawnApplyToGposeTarget()
    {
        _currentOperation = Task.Run(async () =>
        {
            var newActor = await _ipcManager.Brio.SpawnActorAsync().ConfigureAwait(false);
            if (newActor == null) return;
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            unsafe
            {
                GposeService.GposeTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)newActor.Address;
            }

            await McdfApplyToGposeTarget().ConfigureAwait(false);
        });
    }

    public void SaveMareCharaFile(string description, string filePath)
    {
        _currentOperation = Task.Run(async () => await _fileHandler.SaveCharaFileAsync(description, filePath).ConfigureAwait(false));
    }

    public void Dispose()
    {
        try
        {
            var tasks = new[] { McdfApplication.Task, _currentOperation, LoadedMcdfHeader }
                .Where(t => t is { IsCompleted: false }).Cast<Task>().ToArray();
            if (tasks.Length != 0)
                Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MCDF service task drain observed a failure");
        }
    }
}
