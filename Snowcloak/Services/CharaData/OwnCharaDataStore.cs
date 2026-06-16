using ElezenTools.Services;
using Snowcloak.API.Dto.CharaData;
using ElezenTools.Core.Async;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Globalization;

namespace Snowcloak.Services.CharaData;

public sealed class OwnCharaDataStore : IDisposable
{
    private readonly ILogger<OwnCharaDataStore> _logger;
    private readonly ApiController _apiController;
    private readonly CharaDataFileHandler _fileHandler;
    private readonly MetaInfoCache _metaInfoCache;
    private readonly Dictionary<string, CharaDataFullExtendedDto> _ownCharaData = [];
    private readonly Dictionary<string, CharaDataExtendedUpdateDto> _updateDtos = [];
    private readonly SingleFlightCts _createCts = new();
    private readonly SingleFlightCts _uploadCts = new();
    private Task? _charaDataCreateTimeoutTask;

#if !DEBUG
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(1);
#else
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(5);
#endif

    public OwnCharaDataStore(ILogger<OwnCharaDataStore> logger, ApiController apiController,
        CharaDataFileHandler fileHandler, MetaInfoCache metaInfoCache)
    {
        _logger = logger;
        _apiController = apiController;
        _fileHandler = fileHandler;
        _metaInfoCache = metaInfoCache;
    }

    public IDictionary<string, CharaDataFullExtendedDto> OwnCharaData => _ownCharaData;
    public bool Initialized { get; private set; }
    public int MaxCreatableCharaData { get; set; }
    public AsyncOp<List<CharaDataFullExtendedDto>> Download { get; } = new();
    public Cooldown Cooldown { get; } = new();
    public AsyncOp<(string Output, bool Success)> DataCreation { get; } = new();
    public AsyncOp CharaUpdate { get; } = new();
    public AsyncOp<(string Output, bool Success)> Upload { get; } = new();
    public ValueProgress<string>? UploadProgress { get; private set; }

    public void Reset()
    {
        _ownCharaData.Clear();
        _updateDtos.Clear();
        Initialized = false;
    }

    public CharaDataExtendedUpdateDto? GetUpdateDto(string id) => _updateDtos.TryGetValue(id, out var dto) ? dto : null;

    public void CancelUpload() => _uploadCts.Cancel();

    public async Task GetAllData(CancellationToken cancelToken)
    {
        var result = await Download.Run(async () =>
        {
            var data = await _apiController.CharaDataGetOwn().ConfigureAwait(false);
            return data.OrderBy(u => u.CreatedDate).Select(k => new CharaDataFullExtendedDto(k)).ToList();
        }).ConfigureAwait(false);

        Initialized = true;

        foreach (var key in _ownCharaData.Keys.ToList())
        {
            _metaInfoCache.Remove(key);
        }
        _ownCharaData.Clear();

        foreach (var item in result)
        {
            await AddOrUpdateDto(item).ConfigureAwait(false);
        }

        foreach (var id in _updateDtos.Keys.Where(r => !result.Exists(res => string.Equals(res.Id, r, StringComparison.Ordinal))).ToList())
        {
            _updateDtos.Remove(id);
        }

        if (result.Count != 0)
        {
            Cooldown.Trigger(CooldownDuration);
        }
    }

    public void CreateCharaDataEntry(CancellationToken cancelToken)
    {
        _ = DataCreation.Run(async () =>
        {
            var result = await _apiController.CharaDataCreate().ConfigureAwait(false);
            _charaDataCreateTimeoutTask = Task.Run(async () =>
            {
                using var scope = _createCts.Begin();
                using var ct = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, cancelToken);
                await Task.Delay(TimeSpan.FromSeconds(10), ct.Token).ConfigureAwait(false);
                DataCreation.Reset();
            });

            if (result == null)
                return ("Failed to create character data, see log for more information", false);

            await AddOrUpdateDto(result).ConfigureAwait(false);

            return ("Created Character Data", true);
        });
    }

    public async Task DeleteCharaData(CharaDataFullExtendedDto dto)
    {
        var ret = await _apiController.CharaDataDelete(dto.Id).ConfigureAwait(false);
        if (ret)
        {
            _ownCharaData.Remove(dto.Id);
            _metaInfoCache.Remove(dto.FullId);
        }
        _metaInfoCache.Distribute();
    }

    public void SetAppearanceData(string dtoId)
    {
        var hasDto = _ownCharaData.TryGetValue(dtoId, out var dto);
        if (!hasDto || dto == null) return;

        var hasUpdateDto = _updateDtos.TryGetValue(dtoId, out var updateDto);
        if (!hasUpdateDto || updateDto == null) return;

        _ = Task.Run(async () => await _fileHandler.UpdateCharaDataAsync(updateDto).ConfigureAwait(false));
    }

    public void UploadAllCharaData()
    {
        _ = Task.Run(async () =>
        {
            foreach (var updateDto in _updateDtos.Values.Where(u => u.HasChanges))
            {
                await CharaUpdate.Run(() => CharaUpdateAsync(updateDto)).ConfigureAwait(false);
            }
        });
    }

    public void UploadCharaData(string id)
    {
        var hasUpdateDto = _updateDtos.TryGetValue(id, out var updateDto);
        if (!hasUpdateDto || updateDto == null) return;

        _ = CharaUpdate.Run(() => CharaUpdateAsync(updateDto));
    }

    public void UploadMissingFiles(string id)
    {
        var hasDto = _ownCharaData.TryGetValue(id, out var dto);
        if (!hasDto || dto == null) return;

        _ = Upload.Run(() => RestoreThenUpload(dto));
    }

    public async Task<(string Result, bool Success)> UploadFiles(List<GamePathEntry> missingFileList, Func<Task>? postUpload = null)
    {
        UploadProgress = new ValueProgress<string>();
        try
        {
            using var scope = _uploadCts.Begin();
            var missingFiles = await _fileHandler.UploadFiles([.. missingFileList.Select(k => k.HashOrFileSwap)], UploadProgress, scope.Token).ConfigureAwait(false);
            if (missingFiles.Any())
            {
                _logger.LogInformation("Failed to upload {files}", string.Join(", ", missingFiles));
                return (string.Format(CultureInfo.InvariantCulture,
                    "Upload failed: {0} missing or forbidden to upload local files.", missingFiles.Count), false);
            }

            if (postUpload != null)
                await postUpload.Invoke().ConfigureAwait(false);

            return ("Upload sucessful", true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during upload");
            if (ex is OperationCanceledException)
            {
                return ("Upload Cancelled", false);
            }
            return ("Error in upload, see log for more details", false);
        }
    }

    private async Task<(string Output, bool Success)> RestoreThenUpload(CharaDataFullExtendedDto dto)
    {
        var newDto = await _apiController.CharaDataAttemptRestore(dto.Id).ConfigureAwait(false);
        if (newDto == null)
        {
            _ownCharaData.Remove(dto.Id);
            _metaInfoCache.Remove(dto.FullId);
            return ("No such DTO found", false);
        }

        await AddOrUpdateDto(newDto).ConfigureAwait(false);
        _ = _ownCharaData.TryGetValue(dto.Id, out var extendedDto);

        if (!extendedDto!.HasMissingFiles)
        {
            return ("Restored successfully", true);
        }

        var missingFileList = extendedDto!.MissingFiles.ToList();
        var result = await UploadFiles(missingFileList, async () =>
        {
            var newFilePaths = dto.FileGamePaths;
            foreach (var missing in missingFileList)
            {
                newFilePaths.Add(missing);
            }
            CharaDataUpdateDto updateDto = new(dto.Id)
            {
                FileGamePaths = newFilePaths
            };
            var res = await _apiController.CharaDataUpdate(updateDto).ConfigureAwait(false);
            await AddOrUpdateDto(res).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return result;
    }

    private async Task CharaUpdateAsync(CharaDataExtendedUpdateDto updateDto)
    {
        _logger.LogDebug("Uploading Chara Data to Server");
        var baseUpdateDto = updateDto.BaseDto;
        if (baseUpdateDto.FileGamePaths != null)
        {
            var result = await Upload.Run(() => UploadFiles(baseUpdateDto.FileGamePaths)).ConfigureAwait(false);
            if (!result.Success)
            {
                return;
            }
        }

        _logger.LogDebug("Pushing update dto to server: {data}", baseUpdateDto);

        var res = await _apiController.CharaDataUpdate(baseUpdateDto).ConfigureAwait(false);
        await AddOrUpdateDto(res).ConfigureAwait(false);
    }

    private async Task AddOrUpdateDto(CharaDataFullDto? dto)
    {
        if (dto == null) return;

        _ownCharaData[dto.Id] = new(dto);
        _updateDtos[dto.Id] = new(new(dto.Id), _ownCharaData[dto.Id]);

        await _metaInfoCache.CacheOwn(_ownCharaData[dto.Id]).ConfigureAwait(false);
    }

    public void CancelOperations()
    {
        _createCts.Cancel();
        _uploadCts.Cancel();
    }

    public IEnumerable<Task> GetKnownTasks()
    {
        Task?[] tasks = [Download.Task, DataCreation.Task, CharaUpdate.Task, Upload.Task, _charaDataCreateTimeoutTask];
        return tasks.Where(task => task is { IsCompleted: false }).Cast<Task>();
    }

    public void Dispose()
    {
        CancelOperations();
        _createCts.Dispose();
        _uploadCts.Dispose();
    }
}
