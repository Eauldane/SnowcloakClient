using Snowcloak.API.Dto.CharaData;
using Snowcloak.Services.CharaData.Models;
using System.Collections.Concurrent;
using System.Threading;

namespace Snowcloak.Services.CharaData;

public sealed class MetaInfoCache
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly CharaDataNearbyManager _nearbyManager;
    private readonly CharaDataCharacterHandler _characterHandler;
    private readonly ConcurrentDictionary<string, CharaDataMetaInfoExtendedDto?> _cache = [];
    private readonly ConcurrentDictionary<string, DateTime> _downloadCooldowns = [];
    private readonly Lock _distributionLock = new();

    public MetaInfoCache(DalamudUtilService dalamudUtilService, CharaDataNearbyManager nearbyManager,
        CharaDataCharacterHandler characterHandler)
    {
        _dalamudUtilService = dalamudUtilService;
        _nearbyManager = nearbyManager;
        _characterHandler = characterHandler;
    }

    public bool TryGet(string key, out CharaDataMetaInfoExtendedDto? metaInfo) => _cache.TryGetValue(key, out metaInfo);

    public void Store(CharaDataMetaInfoExtendedDto charaData) => _cache[charaData.FullId] = charaData;

    public void StoreFailure(string key) => _cache[key] = null;

    public void Remove(string fullId) => _cache.Remove(fullId, out _);

    public void Clear() => _cache.Clear();

    public async Task<CharaDataMetaInfoExtendedDto> CacheOwn(CharaDataFullExtendedDto ownCharaData)
    {
        var metaInfo = new CharaDataMetaInfoDto(ownCharaData.Id, ownCharaData.Uploader)
        {
            Description = ownCharaData.Description,
            UpdatedDate = ownCharaData.UpdatedDate,
            CanBeDownloaded = !string.IsNullOrEmpty(ownCharaData.GlamourerData) && (ownCharaData.OriginalFiles.Count == ownCharaData.FileGamePaths.Count),
            PoseData = ownCharaData.PoseData,
        };

        return await CacheMeta(metaInfo, isOwnData: true).ConfigureAwait(false);
    }

    public async Task<CharaDataMetaInfoExtendedDto> CacheMeta(CharaDataMetaInfoDto metaInfo, bool isOwnData = false)
    {
        var extended = await CharaDataMetaInfoExtendedDto.Create(metaInfo, _dalamudUtilService, isOwnData).ConfigureAwait(false);
        _cache[extended.FullId] = extended;
        Distribute();

        return extended;
    }

    public void Distribute()
    {
        lock (_distributionLock)
        {
            var snapshot = _cache.ToDictionary();
            _nearbyManager.UpdateSharedData(snapshot);
            _characterHandler.UpdateHandledData(snapshot);
        }
    }

    public void MarkDownloaded(string code) => _downloadCooldowns[code] = DateTime.UtcNow.AddSeconds(10);

    public bool IsInTimeout(string key) => _downloadCooldowns.TryGetValue(key, out var expiry) && expiry > DateTime.UtcNow;
}
