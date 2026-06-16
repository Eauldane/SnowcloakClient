using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ElezenTools.Data;
using ElezenTools.Housing;
using ElezenTools.Services;
using Snowcloak.API.Dto.CharaData;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services.Mediator;
using System.Numerics;
using ElezenMapData = ElezenTools.Data.Classes.MapData;
using ElezenPlayerCharacterData = ElezenTools.Data.Classes.PlayerCharacterData;
using ElezenWorldData = ElezenTools.Data.Classes.WorldData;

namespace Snowcloak.Services;

public sealed partial class DalamudUtilService : IHostedService, IMediatorSubscriber
{
    private readonly IClientState _clientState;
    private readonly IDataManager _gameData;
    private readonly IChatGui _chatGui;
    private readonly IToastGui _toastGui;
    private readonly SnowcloakConfigService _configService;
    private readonly ILogger<DalamudUtilService> _logger;
    private readonly ObjectTableCache _objectTableCache;
    private readonly GposeService _gposeService;
    private readonly GameStateTracker _gameStateTracker;
    private readonly PlayerInteractionService _playerInteraction;
    private readonly Dalamud.Game.ClientLanguage _dataLanguage = Dalamud.Game.ClientLanguage.English;
    private IReadOnlyDictionary<ushort, string>? _worldNameCache;
    private List<(ushort Id, string Name, string Region)>? _worldCatalogCache;
    private string _lastDisplayedServerNews = string.Empty;

    public DalamudUtilService(
        ILogger<DalamudUtilService> logger,
        IClientState clientState,
        IDataManager gameData,
        IChatGui chatGui,
        IToastGui toastGui,
        SnowcloakConfigService configService,
        SnowMediator mediator,
        ObjectTableCache objectTableCache,
        GposeService gposeService,
        GameStateTracker gameStateTracker,
        PlayerInteractionService playerInteraction)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        _logger = logger;
        _clientState = clientState;
        _gameData = gameData;
        _chatGui = chatGui;
        _toastGui = toastGui;
        _configService = configService;
        Mediator = mediator;
        _objectTableCache = objectTableCache;
        _gposeService = gposeService;
        _gameStateTracker = gameStateTracker;
        _playerInteraction = playerInteraction;
        IsWine = Util.IsWine();

        mediator.Subscribe<TargetPairMessage>(this, msg =>
        {
            if (clientState.IsPvP) return;

            var ident = msg.Pair.GetPlayerNameHash();
            _ = Service.RunOnFrameworkAsync(() =>
            {
                if (!_playerInteraction.TargetPlayerByIdentInRange(ident, 55.0f))
                {
                    _toastGui.ShowError("Player out of range.");
                }
            });
        });
        mediator.Subscribe<ConnectedMessage>(this, message =>
        {
            if (!string.IsNullOrWhiteSpace(message.Connection.News))
            {
                PrintServerNewsToChat(message.Connection.News);
            }
        });
        mediator.Subscribe<ServerNewsMessage>(this, message => PrintServerNewsToChat(message.News));
        mediator.Subscribe<DalamudLogoutMessage>(this, _ => _lastDisplayedServerNews = string.Empty);
    }

    public bool IsWine { get; }

    public bool IsAnythingDrawing => _gameStateTracker.IsAnythingDrawing;
    public bool IsInCutscene => _gameStateTracker.IsInCutscene;
    public bool IsInGpose => _gameStateTracker.IsInGpose;
    public bool IsInPvP => _clientState.IsPvP;
    public bool IsLoggedIn => _gameStateTracker.IsLoggedIn;
    public bool IsZoning => _gameStateTracker.IsZoning;
    public bool IsInCombatOrPerforming => _gameStateTracker.IsInCombatOrPerforming;
    public bool HasModifiedGameFiles => _gameData.HasModifiedGameDataFiles;
    public uint ClassJobId => _objectTableCache.ClassJobId;
    public IReadOnlyDictionary<ushort, string> WorldData => WorldDetails.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name);

    /// <summary>
    /// Resolves a world id (as carried on community/directory DTOs) to its display name,
    /// or null when unset or unknown. The world map is cached since the data language is
    /// fixed for the session, so this is cheap to call per-frame.
    /// </summary>
    public string? GetWorldName(uint? worldId)
    {
        if (worldId is not uint id || id == 0 || id > ushort.MaxValue)
            return null;

        _worldNameCache ??= WorldData;
        return _worldNameCache.TryGetValue((ushort)id, out var name) && !string.IsNullOrEmpty(name) ? name : null;
    }

    /// <summary>Resolves a world id to its datacenter region name, or null when unset/unknown.</summary>
    public string? GetWorldRegion(uint? worldId)
    {
        if (worldId is not uint id || id == 0 || id > ushort.MaxValue)
            return null;

        foreach (var world in WorldCatalog)
        {
            if (world.Id == id)
                return string.IsNullOrEmpty(world.Region) ? null : world.Region;
        }

        return null;
    }

    /// <summary>Distinct datacenter region names, ordered for display.</summary>
    public IReadOnlyList<string> WorldRegions => WorldCatalog
        .Select(world => world.Region)
        .Where(region => !string.IsNullOrEmpty(region))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(region => region, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Worlds belonging to the given region, ordered by name.</summary>
    public IReadOnlyList<(ushort Id, string Name)> GetWorldsInRegion(string region) => WorldCatalog
        .Where(world => string.Equals(world.Region, region, StringComparison.Ordinal))
        .Select(world => (world.Id, world.Name))
        .ToList();

    // Cached (id, name, region) snapshot of public worlds. Not cached while empty, since
    // world data may not be ready the first time it is queried (e.g. before login).
    private List<(ushort Id, string Name, string Region)> WorldCatalog
    {
        get
        {
            if (_worldCatalogCache is { Count: > 0 })
                return _worldCatalogCache;

            _worldCatalogCache = WorldDetails
                .Where(kvp => !string.IsNullOrEmpty(kvp.Value.Name))
                .Select(kvp => (Id: kvp.Key, Name: kvp.Value.Name, Region: kvp.Value.RegionName ?? string.Empty))
                .OrderBy(world => world.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return _worldCatalogCache;
        }
    }
    public IReadOnlyDictionary<ushort, ElezenWorldData> WorldDetails => ElezenData.Worlds.GetAll(_dataLanguage)
        .Where(kvp => kvp.Value.DataCenterId != 0
            // Hide the Cloudtest servers (region id 7, shown as "Region 7") from every world/region picker.
            && kvp.Value.RegionId != 7
            && !string.Equals(kvp.Value.RegionName, "Region 7", StringComparison.OrdinalIgnoreCase)
            && (kvp.Value.IsPublic || (kvp.Value.Name.Length > 0 && char.IsUpper(kvp.Value.Name[0]))))
        .ToDictionary(
            kvp => (ushort)kvp.Key,
            kvp => kvp.Value);
    public IReadOnlyDictionary<int, Lumina.Excel.Sheets.UIColor> UiColors => ElezenData.UiColors.GetAll(_dataLanguage);
    public IReadOnlyDictionary<uint, string> TerritoryData => ElezenData.Territories.GetNames(_dataLanguage);
    public IReadOnlyDictionary<byte, string> TribeNames => ElezenData.Tribes.GetNames(_dataLanguage);
    public IReadOnlyDictionary<uint, ElezenMapData> Maps => ElezenData.Maps.GetAll(_dataLanguage);
    public string HousingString => _gameStateTracker.HousingString;

    public SnowMediator Mediator { get; }

    public IGameObject? CreateGameObject(IntPtr reference) => _objectTableCache.CreateGameObject(reference);

    public Task<IGameObject?> CreateGameObjectAsync(IntPtr reference) => _objectTableCache.CreateGameObjectAsync(reference);

    public ICharacter? GetCharacterFromObjectTableByIndex(int index) => _objectTableCache.GetCharacterFromObjectTableByIndex(index);

    public IntPtr GetCompanion(IntPtr? playerPointer = null) => _objectTableCache.GetCompanion(playerPointer);

    public Task<IntPtr> GetCompanionAsync(IntPtr? playerPointer = null) => _objectTableCache.GetCompanionAsync(playerPointer);

    public Task<IGameObject?> GetGposeTargetGameObjectAsync() => _gposeService.GetGposeTargetGameObjectAsync();

    public Task<ICharacter?> GetGposeCharacterFromObjectTableByNameAsync(string name, bool onlyGposeCharacters = false)
        => _gposeService.GetGposeCharacterFromObjectTableByNameAsync(name, onlyGposeCharacters);

    public ICharacter? GetGposeCharacterFromObjectTableByName(string name, bool onlyGposeCharacters = false)
        => _gposeService.GetGposeCharacterFromObjectTableByName(name, onlyGposeCharacters);

    public IEnumerable<ICharacter?> GetGposeCharactersFromObjectTable() => _gposeService.GetGposeCharactersFromObjectTable();

    public bool GetIsPlayerPresent() => _objectTableCache.GetIsPlayerPresent();

    public Task<bool> GetIsPlayerPresentAsync() => _objectTableCache.GetIsPlayerPresentAsync();

    public IntPtr GetMinionOrMount(IntPtr? playerPointer = null) => _objectTableCache.GetMinionOrMount(playerPointer);

    public Task<IntPtr> GetMinionOrMountAsync(IntPtr? playerPointer = null) => _objectTableCache.GetMinionOrMountAsync(playerPointer);

    public IntPtr GetPet(IntPtr? playerPointer = null) => _objectTableCache.GetPet(playerPointer);

    public Task<IntPtr> GetPetAsync(IntPtr? playerPointer = null) => _objectTableCache.GetPetAsync(playerPointer);

    public Task<IPlayerCharacter> GetPlayerCharacterAsync() => _objectTableCache.GetPlayerCharacterAsync();

    public Task<bool> TargetPlayerByIdentAsync(string ident) => _playerInteraction.TargetPlayerByIdentAsync(ident);

    public Task<bool> ExaminePlayerByIdentAsync(string ident) => _playerInteraction.ExaminePlayerByIdentAsync(ident);

    public Task<bool> OpenAdventurerPlateByIdentAsync(string ident) => _playerInteraction.OpenAdventurerPlateByIdentAsync(ident);

    public IPlayerCharacter GetPlayerCharacter() => _objectTableCache.GetPlayerCharacter();

    public IntPtr GetPlayerCharacterFromCachedTableByName(string characterName)
        => _objectTableCache.GetPlayerCharacterFromCachedTableByName(characterName);

    public IntPtr GetPlayerCharacterFromCachedTableByIdent(string characterName)
        => _objectTableCache.GetPlayerCharacterFromCachedTableByIdent(characterName);

    public bool IsFriendByIdent(string ident) => _objectTableCache.IsFriendByIdent(ident);

    public Task<bool> IsFriendByIdentAsync(string ident) => _objectTableCache.IsFriendByIdentAsync(ident);

    public string GetPlayerName() => _objectTableCache.GetPlayerName();

    public Task<string> GetPlayerNameAsync() => _objectTableCache.GetPlayerNameAsync();

    public Task<string> GetPlayerNameHashedAsync() => _objectTableCache.GetPlayerNameHashedAsync();

    public Task<IReadOnlyList<string>> GetNearbyPlayerNameHashesAsync(int maxPlayers = 0)
        => _objectTableCache.GetNearbyPlayerNameHashesAsync(maxPlayers);

    public IntPtr GetPlayerPointer() => _objectTableCache.GetPlayerPointer();

    public Task<IntPtr> GetPlayerPointerAsync() => _objectTableCache.GetPlayerPointerAsync();

    public uint GetHomeWorldId() => _objectTableCache.GetHomeWorldId();

    public uint GetWorldId() => _objectTableCache.GetWorldId();

    public string GetDataCenterRegion() => _objectTableCache.GetDataCenterRegion();

    public LocationInfo GetMapData() => _playerInteraction.GetMapData();

    public Task<LocationInfo> GetMapDataAsync() => _playerInteraction.GetMapDataAsync();

    public Task<uint> GetWorldIdAsync() => _objectTableCache.GetWorldIdAsync();

    public Task<uint> GetHomeWorldIdAsync() => _objectTableCache.GetHomeWorldIdAsync();

    public bool IsGameObjectPresent(IntPtr key) => _objectTableCache.IsGameObjectPresent(key);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogStarting(_logger);
        LogStarted(_logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        LogStopping(_logger);
        Mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }

    public void WaitWhileGposeCharacterIsDrawing(IntPtr characterAddress, int timeOut = 5000)
        => _gposeService.WaitWhileGposeCharacterIsDrawing(characterAddress, timeOut);

    public Vector2 WorldToScreen(IGameObject? obj) => _playerInteraction.WorldToScreen(obj);

    public ElezenPlayerCharacterData FindPlayerByNameHash(string ident) => _objectTableCache.FindPlayerByNameHash(ident);

    public bool TryGetLastHousingPlot(out HousingPlotLocation location) => _gameStateTracker.TryGetLastHousingPlot(out location);

    public uint? GetTargetObjectId() => _playerInteraction.GetTargetObjectId();

    public IPlayerCharacter? GetTargetPlayerCharacter() => _playerInteraction.GetTargetPlayerCharacter();

    public Task<IPlayerCharacter?> GetTargetPlayerCharacterAsync() => _playerInteraction.GetTargetPlayerCharacterAsync();

    public IEnumerable<IPlayerCharacter> GetPartyPlayerCharacters() => _playerInteraction.GetPartyPlayerCharacters();

    private void PrintServerNewsToChat(string news)
    {
        if (_configService.Current.DisableServerNewsInChat)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(news))
        {
            return;
        }

        var normalizedNews = news.Trim();
        if (string.Equals(_lastDisplayedServerNews, normalizedNews, StringComparison.Ordinal))
        {
            return;
        }

        _lastDisplayedServerNews = normalizedNews;
        _chatGui.Print(new XivChatEntry
        {
            Message = "[Snowcloak News] " + normalizedNews,
            Type = XivChatType.SystemMessage
        });
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Information, Message = "Starting DalamudUtilService")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Started DalamudUtilService")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Trace, Message = "Stopping DalamudUtilService")]
    private static partial void LogStopping(ILogger logger);
}
