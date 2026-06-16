using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using ElezenTools.Core.Async;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.WebAPI;

namespace Snowcloak.Services.CharaData;

public sealed class SharedCharaDataStore : IDisposable
{
    private readonly ILogger<SharedCharaDataStore> _logger;
    private readonly ApiController _apiController;
    private readonly MetaInfoCache _metaInfoCache;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly Dictionary<UserData, List<CharaDataMetaInfoExtendedDto>> _sharedWithYouData = [];

#if !DEBUG
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(1);
#else
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(5);
#endif

    public SharedCharaDataStore(ILogger<SharedCharaDataStore> logger, ApiController apiController,
        MetaInfoCache metaInfoCache, PairManager pairManager, DalamudUtilService dalamudUtilService)
    {
        _logger = logger;
        _apiController = apiController;
        _metaInfoCache = metaInfoCache;
        _pairManager = pairManager;
        _dalamudUtilService = dalamudUtilService;
    }

    public IDictionary<UserData, List<CharaDataMetaInfoExtendedDto>> SharedWithYouData => _sharedWithYouData;
    public AsyncOp<List<CharaDataMetaInfoDto>> Download { get; } = new();
    public Cooldown Cooldown { get; } = new();
    public AsyncOp<(string Result, bool Success)> MetaInfoDownload { get; } = new();
    public CharaDataMetaInfoExtendedDto? LastDownloadedMetaInfo { get; private set; }

    public void Reset() => _sharedWithYouData.Clear();

    public async Task GetAllSharedData(CancellationToken token)
    {
        _logger.LogDebug("Getting Shared with You Data");

        var result = await Download.Run(() => _apiController.CharaDataGetShared()).ConfigureAwait(false);

        var newData = new Dictionary<UserData, List<CharaDataMetaInfoExtendedDto>>();
        foreach (var grouping in result.GroupBy(r => r.Uploader))
        {
            var pair = _pairManager.GetPairByUID(grouping.Key.UID);
            if (pair?.IsPaused ?? false) continue;
            List<CharaDataMetaInfoExtendedDto> newList = new();
            foreach (var item in grouping)
            {
                var extended = await CharaDataMetaInfoExtendedDto.Create(item, _dalamudUtilService).ConfigureAwait(false);
                newList.Add(extended);
                _metaInfoCache.Store(extended);
            }
            newData[grouping.Key] = newList;
        }

        _sharedWithYouData.Clear();
        foreach (var entry in newData)
        {
            _sharedWithYouData[entry.Key] = entry.Value;
        }

        _metaInfoCache.Distribute();
        Cooldown.Trigger(CooldownDuration);

        _logger.LogDebug("Finished getting Shared with You Data");
    }

    public void DownloadMetaInfo(string importCode, bool store = true)
    {
        _ = MetaInfoDownload.Run(async () =>
        {
            try
            {
                if (store)
                {
                    LastDownloadedMetaInfo = null;
                }
                var metaInfo = await _apiController.CharaDataGetMetainfo(importCode).ConfigureAwait(false);
                _metaInfoCache.MarkDownloaded(importCode);
                if (metaInfo == null)
                {
                    if (!store)
                        _metaInfoCache.StoreFailure(importCode);
                    return ("Failed to download meta info for this code. Check if the code is valid and you have rights to access it.", false);
                }
                await _metaInfoCache.CacheMeta(metaInfo).ConfigureAwait(false);
                if (store)
                {
                    LastDownloadedMetaInfo = await CharaDataMetaInfoExtendedDto.Create(metaInfo, _dalamudUtilService).ConfigureAwait(false);
                }
                return ("Ok", true);
            }
            finally
            {
                if (!store)
                    MetaInfoDownload.Reset();
            }
        });
    }

    public IEnumerable<Task> GetKnownTasks()
    {
        Task?[] tasks = [Download.Task, MetaInfoDownload.Task];
        return tasks.Where(task => task is { IsCompleted: false }).Cast<Task>();
    }

    public void Dispose()
    {
    }
}
