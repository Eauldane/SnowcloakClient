using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Snowcloak.API.Dto.CharaData;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Interop;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using DalamudGameObject = Dalamud.Game.ClientState.Objects.Types.IGameObject;
using Snowcloak.Services.Housing;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ElezenTools.Player;
using ElezenTools.Data;
using ElezenTools.Services;

namespace Snowcloak.Services;

public class DalamudUtilService : IHostedService, IMediatorSubscriber
{

    
    private struct PlayerInfo
    {
        public PlayerCharacter Character;
        public string Hash;
    };

    private readonly List<uint> _classJobIdsIgnoredForPets = [30];
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IDataManager _gameData;
    private readonly BlockedCharacterHandler _blockedCharacterHandler;
    private readonly IFramework _framework;
    private readonly IGameGui _gameGui;
    private readonly IChatGui _chatGui;
    private readonly ITargetManager _targetManager;
    private readonly IPartyList _partyList;
    private readonly IToastGui _toastGui;
    private readonly ILogger<DalamudUtilService> _logger;
    private readonly IObjectTable _objectTable;
    private readonly  IPlayerState _playerState;
    private readonly PerformanceCollectorService _performanceCollector;
    private uint? _classJobId = 0;
    private DateTime _delayedFrameworkUpdateCheck = DateTime.UtcNow;
    private string _lastGlobalBlockPlayer = string.Empty;
    private string _lastGlobalBlockReason = string.Empty;
    private ushort _lastZone = 0;
    private readonly Dictionary<string, PlayerCharacter> _playerCharas = new(StringComparer.Ordinal);
    private readonly List<string> _notUpdatedCharas = [];
    private bool _sentBetweenAreas = false;
    private static readonly Dictionary<uint, PlayerInfo> _playerInfoCache = new();
    private bool _isOnHousingPlot = false;
    private HousingPlotLocation _lastHousingPlotLocation = default;
    private uint _lastTargetEntityId = 0;
    
    public DalamudUtilService(ILogger<DalamudUtilService> logger, IClientState clientState, IObjectTable objectTable, IFramework framework,
        IGameGui gameGui, IChatGui chatGui, IToastGui toastGui,ICondition condition, IDataManager gameData, ITargetManager targetManager,
        IPlayerState playerState, BlockedCharacterHandler blockedCharacterHandler, SnowMediator mediator, PerformanceCollectorService performanceCollector,
        IPartyList partyList)
    {
        _logger = logger;
        _clientState = clientState;
        _objectTable = objectTable;
        _framework = framework;
        _gameGui = gameGui;
        _toastGui = toastGui;
        _chatGui = chatGui;
        _condition = condition;
        _playerState = playerState;
        _targetManager = targetManager;
        _partyList = partyList;
        _gameData = gameData;
        _blockedCharacterHandler = blockedCharacterHandler;
        Mediator = mediator;
        _performanceCollector = performanceCollector;
        WorldInfoData = new(() =>
        {
            return gameData.GetExcelSheet<Lumina.Excel.Sheets.World>(Dalamud.Game.ClientLanguage.English)!
                .Where(w => w.Name.ByteLength > 0 && w.DataCenter.RowId != 0 && (w.IsPublic || char.IsUpper((char)w.Name.Data.Span[0])))
                .ToDictionary(
                    w => (ushort)w.RowId,
                    w => new WorldInfo(
                        w.Name.ToString(),
                        w.DataCenter.ValueNullable?.Name.ToString() ?? "Unknown"));
        });
        WorldData = new(() => WorldInfoData.Value.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name));
        ClassJobAbbreviations = new(() =>
        {
            return gameData.GetExcelSheet<ClassJob>(Dalamud.Game.ClientLanguage.English)!
                .Where(cj => cj.RowId != 0)
                .ToDictionary(cj => (byte)cj.RowId, cj => cj.Abbreviation.ToString());
        });
        TribeNames = new(() =>
        {
            return gameData.GetExcelSheet<Tribe>(Dalamud.Game.ClientLanguage.English)!
                .Where(t => t.RowId != 0)
                .ToDictionary(t => (byte)t.RowId, t => t.Masculine.ToString());
        });
        UiColors = new(() =>
        {
            return gameData.GetExcelSheet<Lumina.Excel.Sheets.UIColor>(Dalamud.Game.ClientLanguage.English)!
                .Where(x => x.RowId != 0 && !(x.RowId >= 500 && (x.Dark & 0xFFFFFF00) == 0))
                .ToDictionary(x => (int)x.RowId);
        });
        TerritoryData = new(() =>
        {
            return gameData.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>(Dalamud.Game.ClientLanguage.English)!
            .Where(w => w.RowId != 0)
            .ToDictionary(w => w.RowId, w =>
            {
                StringBuilder sb = new();
                sb.Append(w.PlaceNameRegion.Value.Name);
                if (w.PlaceName.ValueNullable != null)
                {
                    sb.Append(" - ");
                    sb.Append(w.PlaceName.Value.Name);
                }
                return sb.ToString();
            });
        });
        MapData = new(() =>
        {
            return gameData.GetExcelSheet<Lumina.Excel.Sheets.Map>(Dalamud.Game.ClientLanguage.English)!
            .Where(w => w.RowId != 0)
            .ToDictionary(w => w.RowId, w =>
            {
                StringBuilder sb = new();
                sb.Append(w.PlaceNameRegion.Value.Name);
                if (w.PlaceName.ValueNullable != null)
                {
                    sb.Append(" - ");
                    sb.Append(w.PlaceName.Value.Name);
                }
                if (w.PlaceNameSub.ValueNullable != null && !string.IsNullOrEmpty(w.PlaceNameSub.Value.Name.ToString()))
                {
                    sb.Append(" - ");
                    sb.Append(w.PlaceNameSub.Value.Name);
                }
                return (w, sb.ToString());
            });
        });
        mediator.Subscribe<TargetPairMessage>(this, (msg) =>
        {
            if (clientState.IsPvP) return;
            var ident = msg.Pair.GetPlayerNameHash();
            _ = Service.UseFramework(() =>
            {
                var addr = GetPlayerCharacterFromCachedTableByIdent(ident);
                var pc = _objectTable.LocalPlayer!;

                var gobj = CreateGameObject(addr);
                // Any further than roughly 55y is out of range for targetting
                if (gobj != null && Vector3.Distance(pc.Position, gobj.Position) < 55.0f)
                    targetManager.Target = gobj;
                else
                    _toastGui.ShowError("Player out of range.");
            }).ConfigureAwait(false);
        });
        IsWine = Util.IsWine();
    }

    public bool IsWine { get; init; }
    public unsafe GameObject* GposeTarget
    {
        get => TargetSystem.Instance()->GPoseTarget;
        set => TargetSystem.Instance()->GPoseTarget = value;
    }

    private unsafe bool HasGposeTarget => GposeTarget != null;
    private unsafe int GPoseTargetIdx => !HasGposeTarget ? -1 : GposeTarget->ObjectIndex;

    public async Task<IGameObject?> GetGposeTargetGameObjectAsync()
    {
        if (!HasGposeTarget)
            return null;

        return await _framework.RunOnFrameworkThread(() => _objectTable[GPoseTargetIdx]).ConfigureAwait(true);
    }
    public bool IsAnythingDrawing { get; private set; } = false;
    public bool IsInCutscene { get; private set; } = false;
    public bool IsInGpose { get; private set; } = false;
    public bool IsLoggedIn { get; private set; }
    public bool IsOnFrameworkThread => _framework.IsInFrameworkUpdateThread;
    public bool IsZoning => _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];
    public bool IsInCombatOrPerforming { get; private set; } = false;
    public bool HasModifiedGameFiles => _gameData.HasModifiedGameDataFiles;
    public uint ClassJobId => _classJobId!.Value;
    public Lazy<Dictionary<ushort, string>> WorldData { get; private set; }
    public Lazy<Dictionary<ushort, WorldInfo>> WorldInfoData { get; private set; }
    public Lazy<Dictionary<int, Lumina.Excel.Sheets.UIColor>> UiColors { get; private set; }
    public Lazy<Dictionary<uint, string>> TerritoryData { get; private set; }
    public Lazy<Dictionary<byte, string>> ClassJobAbbreviations { get; private set; }
    public Lazy<Dictionary<byte, string>> TribeNames { get; private set; }
    public Lazy<Dictionary<uint, (Lumina.Excel.Sheets.Map Map, string MapName)>> MapData { get; private set; }

    public SnowMediator Mediator { get; }

    public Dalamud.Game.ClientState.Objects.Types.IGameObject? CreateGameObject(IntPtr reference)
    {
        EnsureIsOnFramework();
        return _objectTable.CreateObjectReference(reference);
    }

    public async Task<Dalamud.Game.ClientState.Objects.Types.IGameObject?> CreateGameObjectAsync(IntPtr reference)
    {
        return await Service.UseFramework(() => _objectTable.CreateObjectReference(reference)).ConfigureAwait(false);
    }

    public void EnsureIsOnFramework()
    {
        if (!_framework.IsInFrameworkUpdateThread) throw new InvalidOperationException("Can only be run on Framework");
    }

    public Dalamud.Game.ClientState.Objects.Types.ICharacter? GetCharacterFromObjectTableByIndex(int index)
    {
        EnsureIsOnFramework();
        var objTableObj = _objectTable[index];
        if (objTableObj!.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player) return null;
        return (Dalamud.Game.ClientState.Objects.Types.ICharacter)objTableObj;
    }

    public unsafe IntPtr GetCompanion(IntPtr? playerPointer = null)
    {
        EnsureIsOnFramework();
        var mgr = CharacterManager.Instance();
        playerPointer ??= GetPlayerPointer();
        if (playerPointer == IntPtr.Zero || (IntPtr)mgr == IntPtr.Zero) return IntPtr.Zero;
        return (IntPtr)mgr->LookupBuddyByOwnerObject((BattleChara*)playerPointer);
    }

    public async Task<IntPtr> GetCompanionAsync(IntPtr? playerPointer = null)
    {
        return await Service.UseFramework(() => GetCompanion(playerPointer)).ConfigureAwait(false);
    }

    public async Task<ICharacter?> GetGposeCharacterFromObjectTableByNameAsync(string name, bool onlyGposeCharacters = false)
    {
        return await Service.UseFramework(() => GetGposeCharacterFromObjectTableByName(name, onlyGposeCharacters)).ConfigureAwait(false);
    }

    public ICharacter? GetGposeCharacterFromObjectTableByName(string name, bool onlyGposeCharacters = false)
    {
        EnsureIsOnFramework();
        return (ICharacter?)_objectTable
            .FirstOrDefault(i => (!onlyGposeCharacters || i.ObjectIndex >= 200) && string.Equals(i.Name.ToString(), name, StringComparison.Ordinal));
    }

    public IEnumerable<ICharacter?> GetGposeCharactersFromObjectTable()
    {
        return _objectTable.Where(o => o.ObjectIndex > 200 && o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player).Cast<ICharacter>();
    }

    public bool GetIsPlayerPresent()
    {
        EnsureIsOnFramework();
        return _objectTable.LocalPlayer != null && _objectTable.LocalPlayer.IsValid();
    }

    public async Task<bool> GetIsPlayerPresentAsync()
    {
        return await Service.UseFramework(GetIsPlayerPresent).ConfigureAwait(false);
    }

    public unsafe IntPtr GetMinionOrMount(IntPtr? playerPointer = null)
    {
        EnsureIsOnFramework();
        playerPointer ??= GetPlayerPointer();
        if (playerPointer == IntPtr.Zero) return IntPtr.Zero;
        return _objectTable.GetObjectAddress(((GameObject*)playerPointer)->ObjectIndex + 1);
    }

    public async Task<IntPtr> GetMinionOrMountAsync(IntPtr? playerPointer = null)
    {
        return await Service.UseFramework(() => GetMinionOrMount(playerPointer)).ConfigureAwait(false);
    }

    public unsafe IntPtr GetPet(IntPtr? playerPointer = null)
    {
        EnsureIsOnFramework();
        if (_classJobIdsIgnoredForPets.Contains(_classJobId ?? 0)) return IntPtr.Zero;
        var mgr = CharacterManager.Instance();
        playerPointer ??= GetPlayerPointer();
        if (playerPointer == IntPtr.Zero || (IntPtr)mgr == IntPtr.Zero) return IntPtr.Zero;
        return (IntPtr)mgr->LookupPetByOwnerObject((BattleChara*)playerPointer);
    }

    public async Task<IntPtr> GetPetAsync(IntPtr? playerPointer = null)
    {
        return await Service.UseFramework(() => GetPet(playerPointer)).ConfigureAwait(false);
    }

    public async Task<IPlayerCharacter> GetPlayerCharacterAsync()
    {
        return await Service.UseFramework(GetPlayerCharacter).ConfigureAwait(false);
    }

    public async Task<bool> TargetPlayerByIdentAsync(string ident)
    {
        return await Service.UseFramework(() =>
        {
            var addr = GetPlayerCharacterFromCachedTableByIdent(ident);
            if (addr == IntPtr.Zero)
                return false;

            var obj = _objectTable.CreateObjectReference(addr);
            if (obj == null)
                return false;

            _targetManager.Target = obj;
            return true;
        }).ConfigureAwait(false);
    }
    
    public async Task<bool> ExaminePlayerByIdentAsync(string ident)
    {
        return await Service.UseFramework(() =>
        {
            unsafe
            {
                var addr = GetPlayerCharacterFromCachedTableByIdent(ident);
                if (addr == IntPtr.Zero)
                    return false;

                var obj = _objectTable.CreateObjectReference(addr);
                if (obj is not IPlayerCharacter pc)
                    return false;

                AgentInspect.Instance()->ExamineCharacter(pc.EntityId);
                return true;
            }
        }).ConfigureAwait(false);
    }

    public async Task<bool> OpenAdventurerPlateByIdentAsync(string ident)
    {
        return await Service.UseFramework(() =>
        {
            unsafe
            {
                var addr = GetPlayerCharacterFromCachedTableByIdent(ident);
                if (addr == IntPtr.Zero)
                    return false;

                var obj = _objectTable.CreateObjectReference(addr);
                if (obj is not IPlayerCharacter pc)
                    return false;

                AgentCharaCard.Instance()->OpenCharaCard((GameObject*)pc.Address);
                return true;
            }
        }).ConfigureAwait(false);
    }
    
    public IPlayerCharacter GetPlayerCharacter()
    {
        EnsureIsOnFramework();
        return _objectTable.LocalPlayer!;
    }

    public IntPtr GetPlayerCharacterFromCachedTableByName(string characterName)
    {
        foreach (var c in _playerCharas.Values)
        {
            if (c.Name.Equals(characterName, StringComparison.Ordinal))
                return c.Address;
        }
        return 0;
    }

    public IntPtr GetPlayerCharacterFromCachedTableByIdent(string characterName)
    {
        if (_playerCharas.TryGetValue(characterName, out var pchar)) return pchar.Address;
        return IntPtr.Zero;
    }

    public bool IsFriendByIdent(string ident)
    {
        EnsureIsOnFramework();
        var addr = GetPlayerCharacterFromCachedTableByIdent(ident);
        if (addr == IntPtr.Zero)
            return false;

        var obj = _objectTable.CreateObjectReference(addr);
        if (obj is not IPlayerCharacter pc)
            return false;

        return pc.StatusFlags.HasFlag(StatusFlags.Friend);
    }

    public async Task<bool> IsFriendByIdentAsync(string ident)
    {
        return await Service.UseFramework(() => IsFriendByIdent(ident)).ConfigureAwait(false);
    }
    
    public string GetPlayerName()
    {
        EnsureIsOnFramework();

        return _playerState.CharacterName ?? "--";
    }

    public async Task<string> GetPlayerNameAsync()
    {
        return await Service.UseFramework(GetPlayerName).ConfigureAwait(false);
    }

    public async Task<string> GetPlayerNameHashedAsync()
    {
        return await Service.UseFramework(() => (GetPlayerName() + GetHomeWorldId()).GetHash256()).ConfigureAwait(false);
    }
    
    public async Task<IReadOnlyList<string>> GetNearbyPlayerNameHashesAsync(int maxPlayers = 0)
    {
        return await Service.UseFramework(() =>
        {
            var hashes = _playerCharas.Keys;

            if (maxPlayers > 0)
                return (IReadOnlyList<string>)hashes.Take(maxPlayers).ToList();

            return hashes.ToList();
        }).ConfigureAwait(false);
    }
    
    public bool TryGetIdentFromMenuTarget(IMenuOpenedArgs args, out string ident)
    {
        ident = string.Empty;
        if (args.Target is not MenuTargetDefault target || target.TargetHomeWorld.RowId == 0)
            return false;

        var name = target.TargetName ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return false;

        ident = (name + target.TargetHomeWorld.RowId).GetHash256();
        return true;
    }

    public IntPtr GetPlayerPointer()
    {
        EnsureIsOnFramework();
        return _objectTable.LocalPlayer?.Address ?? IntPtr.Zero;
    }

    public async Task<IntPtr> GetPlayerPointerAsync()
    {
        return await Service.UseFramework(GetPlayerPointer).ConfigureAwait(false);
    }

    public uint GetHomeWorldId()
    {
        EnsureIsOnFramework();

        return _objectTable.LocalPlayer?.HomeWorld.RowId ?? 0;
    }

    public uint GetWorldId()
    {
        EnsureIsOnFramework();
        return _objectTable.LocalPlayer!.CurrentWorld.RowId;
    }

    public string GetDataCenterRegion()
    {
        EnsureIsOnFramework();

        var worldId = _objectTable.LocalPlayer?.HomeWorld.RowId ?? 0;
        if (worldId == 0)
            return string.Empty;

        var world = _gameData.GetExcelSheet<Lumina.Excel.Sheets.World>(Dalamud.Game.ClientLanguage.English)?.GetRow(worldId);
        var dataCenter = world?.DataCenter.Value;
        if (dataCenter == null)
            return string.Empty;

        byte region = 0;
        string fallbackName = string.Empty;

        var regionProperty = dataCenter.GetType().GetProperty("Region");
        if (regionProperty?.GetValue(dataCenter) is byte regionValue)
            region = regionValue;

        var nameProperty = dataCenter.GetType().GetProperty("Name");
        fallbackName = nameProperty?.GetValue(dataCenter)?.ToString() ?? string.Empty;

        return region switch
        {
            1 => "Japan",
            2 => "North America",
            3 => "Europe",
            4 => "Oceania",
            5 => "China",
            6 => "Korea",
            _ => fallbackName
        };
    }

    public bool TryGetWorldRegion(ushort worldId, out string region)
    {
        region = string.Empty;
        if (worldId == 0)
            return false;

        var world = _gameData.GetExcelSheet<Lumina.Excel.Sheets.World>(Dalamud.Game.ClientLanguage.English)?.GetRow(worldId);
        var dataCenter = world?.DataCenter.Value;
        if (dataCenter == null)
            return false;

        byte regionValue = 0;
        string fallbackName = string.Empty;

        var regionProperty = dataCenter.GetType().GetProperty("Region");
        if (regionProperty?.GetValue(dataCenter) is byte regionResult)
            regionValue = regionResult;

        var nameProperty = dataCenter.GetType().GetProperty("Name");
        fallbackName = nameProperty?.GetValue(dataCenter)?.ToString() ?? string.Empty;

        region = regionValue switch
        {
            1 => "Japan",
            2 => "North America",
            3 => "Europe",
            4 => "Oceania",
            5 => "China",
            6 => "Korea",
            _ => fallbackName
        };

        return !string.IsNullOrWhiteSpace(region);
    }
    
    public unsafe LocationInfo GetMapData()
    {
        EnsureIsOnFramework();
        var agentMap = AgentMap.Instance();
        var houseMan = HousingManager.Instance();
        uint serverId = 0;
        if (_objectTable.LocalPlayer == null) serverId = 0;
        else serverId = _playerState.CurrentWorld.RowId;
        uint mapId = agentMap == null ? 0 : agentMap->CurrentMapId;
        uint territoryId = agentMap == null ? 0 : agentMap->CurrentTerritoryId;
        uint divisionId = houseMan == null ? 0 : (uint)(houseMan->GetCurrentDivision());
        uint wardId = houseMan == null ? 0 : (uint)(houseMan->GetCurrentWard() + 1);
        uint houseId = 0;
        var tempHouseId = houseMan == null ? 0 : (houseMan->GetCurrentPlot());
        if (!houseMan->IsInside()) tempHouseId = 0;
        if (tempHouseId < -1)
        {
            divisionId = tempHouseId == -127 ? 2 : (uint)1;
            tempHouseId = 100;
        }
        if (tempHouseId == -1) tempHouseId = 0;
        houseId = (uint)tempHouseId;
        if (houseId != 0)
        {
            territoryId = HousingManager.GetOriginalHouseTerritoryTypeId();
        }
        uint roomId = houseMan == null ? 0 : (uint)(houseMan->GetCurrentRoom());

        return new LocationInfo()
        {
            ServerId = serverId,
            MapId = mapId,
            TerritoryId = territoryId,
            DivisionId = divisionId,
            WardId = wardId,
            HouseId = houseId,
            RoomId = roomId
        };
    }

    public unsafe void SetMarkerAndOpenMap(Vector3 position, Map map)
    {
        EnsureIsOnFramework();
        var agentMap = AgentMap.Instance();
        if (agentMap == null) return;
        agentMap->OpenMapByMapId(map.RowId);
        agentMap->SetFlagMapMarker(map.TerritoryType.RowId, map.RowId, position);
    }

    public async Task<LocationInfo> GetMapDataAsync()
    {
        return await Service.UseFramework(GetMapData).ConfigureAwait(false);
    }

    public async Task<uint> GetWorldIdAsync()
    {
        return await Service.UseFramework(GetWorldId).ConfigureAwait(false);
    }

    public async Task<uint> GetHomeWorldIdAsync()
    {
        return await Service.UseFramework(GetHomeWorldId).ConfigureAwait(false);
    }

    public unsafe bool IsGameObjectPresent(IntPtr key)
    {
        return _objectTable.Any(f => f.Address == key);
    }

    public bool IsObjectPresent(Dalamud.Game.ClientState.Objects.Types.IGameObject? obj)
    {
        EnsureIsOnFramework();
        return obj != null && obj.IsValid();
    }

    public async Task<bool> IsObjectPresentAsync(Dalamud.Game.ClientState.Objects.Types.IGameObject? obj)
    {
        return await Service.UseFramework(() => IsObjectPresent(obj)).ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting DalamudUtilService");
        Snowcloak.Plugin.Self.RealOnFrameworkUpdate = this.FrameworkOnUpdate;
        Service.Framework.Update += Snowcloak.Plugin.Self.OnFrameworkUpdate;

        if (IsLoggedIn)
        {
            _classJobId = _playerState.ClassJob.RowId;

        }

        _logger.LogInformation("Started DalamudUtilService");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogTrace("Stopping {type}", GetType());
        Mediator.UnsubscribeAll(this);
        Service.Framework.Update -= Snowcloak.Plugin.Self.OnFrameworkUpdate;

        return Task.CompletedTask;
    }

    public async Task WaitWhileCharacterIsDrawing(ILogger logger, GameObjectHandler handler, Guid redrawId, int timeOut = 5000, CancellationToken? ct = null)
    {
        if (!_clientState.IsLoggedIn) return;

        const int tick = 250;
        int curWaitTime = 0;
        try
        {
            logger.LogTrace("[{redrawId}] Starting wait for {handler} to draw", redrawId, handler);
            await Task.Delay(tick).ConfigureAwait(true);
            curWaitTime += tick;

            while ((!ct?.IsCancellationRequested ?? true)
                   && curWaitTime < timeOut
                   && await handler.IsBeingDrawnRunOnFrameworkAsync().ConfigureAwait(false)) // 0b100000000000 is "still rendering" or something
            {
                logger.LogTrace("[{redrawId}] Waiting for {handler} to finish drawing", redrawId, handler);
                curWaitTime += tick;
                await Task.Delay(tick).ConfigureAwait(true);
            }

            logger.LogTrace("[{redrawId}] Finished drawing after {curWaitTime}ms", redrawId, curWaitTime);
        }
        catch (NullReferenceException ex)
        {
            logger.LogWarning(ex, "Error accessing {handler}, object does not exist anymore?", handler);
        }
        catch (AccessViolationException ex)
        {
            logger.LogWarning(ex, "Error accessing {handler}, object does not exist anymore?", handler);
        }
    }

    public unsafe void WaitWhileGposeCharacterIsDrawing(IntPtr characterAddress, int timeOut = 5000)
    {
        Thread.Sleep(500);
        var obj = (GameObject*)characterAddress;
        const int tick = 250;
        int curWaitTime = 0;
        _logger.LogTrace("RenderFlags: {flags}", obj->RenderFlags.ToString("X"));
        while (obj->RenderFlags != 0x00 && curWaitTime < timeOut)
        {
            _logger.LogTrace($"Waiting for gpose actor to finish drawing");
            curWaitTime += tick;
            Thread.Sleep(tick);
        }

        Thread.Sleep(tick * 2);
    }

    public Vector2 WorldToScreen(Dalamud.Game.ClientState.Objects.Types.IGameObject? obj)
    {
        if (obj == null) return Vector2.Zero;
        return _gameGui.WorldToScreen(obj.Position, out var screenPos) ? screenPos : Vector2.Zero;
    }

    public PlayerCharacter FindPlayerByNameHash(string ident)
    {
        _playerCharas.TryGetValue(ident, out var result);
        return result;
    }

    private unsafe PlayerInfo GetPlayerInfo(DalamudGameObject chara)
    {
        uint id = chara.EntityId;

        if (!_playerInfoCache.TryGetValue(id, out var info))
        {
            info.Character.ObjectId = id;
            info.Character.Name = chara.Name.TextValue; // ?
            info.Character.HomeWorldId = ((BattleChara*)chara.Address)->Character.HomeWorld;
            info.Character.Address = chara.Address;
            info.Character.ClassJob = ((BattleChara*)chara.Address)->Character.ClassJob;
            info.Character.Gender = ((BattleChara*)chara.Address)->DrawData.CustomizeData.Data[(int)CustomizeIndex.Gender];
            info.Character.Clan = ((BattleChara*)chara.Address)->DrawData.CustomizeData.Data[(int)CustomizeIndex.Tribe];
            info.Hash = Crypto.GetHash256(info.Character.Name + info.Character.HomeWorldId.ToString());
            if (chara is IPlayerCharacter player)
                info.Character.Level = player.Level;
            _playerInfoCache[id] = info;
        }

        info.Character.Address = chara.Address;
        info.Character.ClassJob = ((BattleChara*)chara.Address)->Character.ClassJob;
        info.Character.Gender = ((BattleChara*)chara.Address)->DrawData.CustomizeData.Data[(int)CustomizeIndex.Gender];
        info.Character.Clan = ((BattleChara*)chara.Address)->DrawData.CustomizeData.Data[(int)CustomizeIndex.Tribe];
        if (chara is IPlayerCharacter updatedPlayer)
            info.Character.Level = updatedPlayer.Level;

        return info;
    }

    private unsafe void CheckCharacterForDrawing(PlayerCharacter p)
    {
        var gameObj = (GameObject*)p.Address;
        var drawObj = gameObj->DrawObject;
        var characterName = p.Name;
        bool isDrawing = false;
        bool isDrawingChanged = false;
        if ((nint)drawObj != IntPtr.Zero)
        {
            isDrawing = gameObj->RenderFlags == VisibilityFlags.Model;
            if (!isDrawing)
            {
                isDrawing = ((CharacterBase*)drawObj)->HasModelInSlotLoaded != 0;
                if (!isDrawing)
                {
                    isDrawing = ((CharacterBase*)drawObj)->HasModelFilesInSlotLoaded != 0;
                    if (isDrawing && !string.Equals(_lastGlobalBlockPlayer, characterName, StringComparison.Ordinal)
                        && !string.Equals(_lastGlobalBlockReason, "HasModelFilesInSlotLoaded", StringComparison.Ordinal))
                    {
                        _lastGlobalBlockPlayer = characterName;
                        _lastGlobalBlockReason = "HasModelFilesInSlotLoaded";
                        isDrawingChanged = true;
                    }
                }
                else
                {
                    if (!string.Equals(_lastGlobalBlockPlayer, characterName, StringComparison.Ordinal)
                        && !string.Equals(_lastGlobalBlockReason, "HasModelInSlotLoaded", StringComparison.Ordinal))
                    {
                        _lastGlobalBlockPlayer = characterName;
                        _lastGlobalBlockReason = "HasModelInSlotLoaded";
                        isDrawingChanged = true;
                    }
                }
            }
            else
            {
                if (!string.Equals(_lastGlobalBlockPlayer, characterName, StringComparison.Ordinal)
                    && !string.Equals(_lastGlobalBlockReason, "RenderFlags", StringComparison.Ordinal))
                {
                    _lastGlobalBlockPlayer = characterName;
                    _lastGlobalBlockReason = "RenderFlags";
                    isDrawingChanged = true;
                }
            }
        }

        if (isDrawingChanged)
        {
            _logger.LogTrace("Global draw block: START => {name} ({reason})", characterName, _lastGlobalBlockReason);
        }

        IsAnythingDrawing |= isDrawing;
    }
    
    private unsafe void HandleHousingPlotState()
    {
        if (_objectTable.LocalPlayer == null)
            return;

        var isCurrentlyOnPlot = TryGetHousingPlotLocation(out var currentLocation, out var isInsideHousing);

        if (_isOnHousingPlot && isCurrentlyOnPlot && isInsideHousing
            && !currentLocation.Equals(_lastHousingPlotLocation)
            && IsSameHousingStructure(currentLocation, _lastHousingPlotLocation))
        {
            currentLocation = _lastHousingPlotLocation;
        }
        
        if (_isOnHousingPlot && (!isCurrentlyOnPlot || !currentLocation.Equals(_lastHousingPlotLocation)))
        {
            _logger.LogInformation("Exited housing plot {FullId}", _lastHousingPlotLocation.FullId);
            #if DEBUG
            _chatGui.Print(new XivChatEntry
            {
                Message = $"Exited housing plot {_lastHousingPlotLocation.DisplayName}",
                Type = XivChatType.SystemMessage
            });
            #endif
            Mediator.Publish(new HousingPlotLeftMessage(_lastHousingPlotLocation));
        }

        if (isCurrentlyOnPlot && (!_isOnHousingPlot || !currentLocation.Equals(_lastHousingPlotLocation)))
        {
            _logger.LogInformation("Entered housing plot {FullId}", currentLocation.FullId);
            #if DEBUG
            _chatGui.Print(new XivChatEntry
            {
                Message = $"Entered housing plot {currentLocation.DisplayName}",
                Type = XivChatType.SystemMessage
            });
            #endif
            Mediator.Publish(new HousingPlotEnteredMessage(currentLocation));
        }

        _isOnHousingPlot = isCurrentlyOnPlot;
        _lastHousingPlotLocation = currentLocation;
        
    }

    public string GetHousingString()
    {
        return _lastHousingPlotLocation.FullId;
    }
    
    private static bool IsSameHousingStructure(HousingPlotLocation left, HousingPlotLocation right)
    {
        return left.WorldId == right.WorldId
               && left.WardId == right.WardId
               && left.PlotId == right.PlotId
               && left.IsApartment == right.IsApartment;
    }

    
    public bool TryGetLastHousingPlot(out HousingPlotLocation location)
    {
        location = _lastHousingPlotLocation;
        return _isOnHousingPlot;
    }
    
    private unsafe bool TryGetHousingPlotLocation(out HousingPlotLocation housingLocation, out bool isInsideHousing)
    {
        housingLocation = default;
        isInsideHousing = false;

        var houseMan = HousingManager.Instance();
        var agentMap = AgentMap.Instance();

        var locationInfo = GetMapData();
        
        if (houseMan != null)
        {
            isInsideHousing = houseMan->IsInside();
            var currentPlot = (uint)(houseMan->GetCurrentPlot() + 1); // Pass this as the actual number instead of index
            var ward = (uint)(houseMan->GetCurrentWard() + 1);
            var division = (uint)houseMan->GetCurrentDivision();
            var room = (uint)houseMan->GetCurrentRoom();
            var territoryId = locationInfo.TerritoryId;
            var worldId = locationInfo.ServerId;
            if (currentPlot > 0)
            {
                housingLocation = new HousingPlotLocation(worldId, territoryId, division, ward, currentPlot, 0, false);
                return true;
            }

            if (currentPlot < -1)
            {
                uint apartmentDivision = currentPlot == -127 ? 2u : 1u;
                housingLocation = new HousingPlotLocation(worldId, territoryId, apartmentDivision, ward, 100, room, true);
                return true;            }
        }

        if (locationInfo.HouseId > 0)
        {
            var isApartment = locationInfo.HouseId == 100 || locationInfo.DivisionId == 2;
            housingLocation = new HousingPlotLocation(locationInfo.ServerId, locationInfo.TerritoryId, locationInfo.DivisionId, locationInfo.WardId, locationInfo.HouseId, locationInfo.RoomId, isApartment);
            return true;
        }

        return false;
    }
    

    public uint? GetTargetObjectId()
    {
        if (_targetManager.Target is IPlayerCharacter playerCharacter)
            return playerCharacter.EntityId;

        return null;
    }

    public IPlayerCharacter? GetTargetPlayerCharacter()
    {
        EnsureIsOnFramework();
        if (_targetManager.Target is IPlayerCharacter playerCharacter)
            return playerCharacter;

        return null;
    }

    public async Task<IPlayerCharacter?> GetTargetPlayerCharacterAsync()
    {
        return await Service.UseFramework(GetTargetPlayerCharacter).ConfigureAwait(false);
    }

    public IEnumerable<IPlayerCharacter> GetPartyPlayerCharacters()
    {
        EnsureIsOnFramework();
        for (int i = 0; i < _partyList.Count; i++)
        {
            if (_partyList[i]?.GameObject is IPlayerCharacter partyMember)
                yield return partyMember;
        }
    }

    private void FrameworkOnUpdate(IFramework framework)
    {
        _performanceCollector.LogPerformance(this, $"FrameworkOnUpdate", FrameworkOnUpdateInternal);
    }

    private unsafe void FrameworkOnUpdateInternal()
    {
        if (_objectTable.LocalPlayer?.IsDead ?? false)
        {
            return;
        }

        bool isNormalFrameworkUpdate = DateTime.UtcNow < _delayedFrameworkUpdateCheck.AddMilliseconds(200);

        _performanceCollector.LogPerformance(this, $"FrameworkOnUpdateInternal+{(isNormalFrameworkUpdate ? "Regular" : "Delayed")}", () =>
        {
            IsAnythingDrawing = false;
            _performanceCollector.LogPerformance(this, $"ObjTableToCharas",
                () =>
                {
                    if (_sentBetweenAreas)
                        return;

                    _notUpdatedCharas.AddRange(_playerCharas.Keys);

                    for (int i = 0; i < _objectTable.Length; i++)
                    {
                        var chara = _objectTable[i];
                        if (chara == null || chara.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player)
                            continue;

                        if (_blockedCharacterHandler.IsCharacterBlocked(chara.Address, out bool firstTime) && firstTime)
                        {
                            _logger.LogTrace("Skipping character {addr}, blocked/muted", chara.Address.ToString("X"));
                            continue;
                        }

                        var info = GetPlayerInfo(chara);

                        if (!IsAnythingDrawing)
                            CheckCharacterForDrawing(info.Character);
                        _notUpdatedCharas.Remove(info.Hash);
                        _playerCharas[info.Hash] = info.Character;
                    }

                    foreach (var notUpdatedChara in _notUpdatedCharas)
                    {
                        _playerCharas.Remove(notUpdatedChara);
                    }

                    _notUpdatedCharas.Clear();
                });

            if (!IsAnythingDrawing && !string.IsNullOrEmpty(_lastGlobalBlockPlayer))
            {
                _logger.LogTrace("Global draw block: END => {name}", _lastGlobalBlockPlayer);
                _lastGlobalBlockPlayer = string.Empty;
                _lastGlobalBlockReason = string.Empty;
            }

            if (_clientState.IsGPosing && !IsInGpose)
            {
                _logger.LogDebug("Gpose start");
                IsInGpose = true;
                Mediator.Publish(new GposeStartMessage());
            }
            else if (!_clientState.IsGPosing && IsInGpose)
            {
                _logger.LogDebug("Gpose end");
                IsInGpose = false;
                Mediator.Publish(new GposeEndMessage());
            }

            if ((_condition[ConditionFlag.Performing] || _condition[ConditionFlag.InCombat]) && !IsInCombatOrPerforming)
            {
                _logger.LogDebug("Combat/Performance start");
                IsInCombatOrPerforming = true;
                Mediator.Publish(new CombatOrPerformanceStartMessage());
                Mediator.Publish(new HaltScanMessage(nameof(IsInCombatOrPerforming)));
            }
            else if ((!_condition[ConditionFlag.Performing] && !_condition[ConditionFlag.InCombat]) && IsInCombatOrPerforming)
            {
                _logger.LogDebug("Combat/Performance end");
                IsInCombatOrPerforming = false;
                Mediator.Publish(new CombatOrPerformanceEndMessage());
                Mediator.Publish(new ResumeScanMessage(nameof(IsInCombatOrPerforming)));
            }

            if (_condition[ConditionFlag.WatchingCutscene] && !IsInCutscene)
            {
                _logger.LogDebug("Cutscene start");
                IsInCutscene = true;
                Mediator.Publish(new CutsceneStartMessage());
                Mediator.Publish(new HaltScanMessage(nameof(IsInCutscene)));
            }
            else if (!_condition[ConditionFlag.WatchingCutscene] && IsInCutscene)
            {
                _logger.LogDebug("Cutscene end");
                IsInCutscene = false;
                Mediator.Publish(new CutsceneEndMessage());
                Mediator.Publish(new ResumeScanMessage(nameof(IsInCutscene)));
            }

            if (IsInCutscene)
            {
                Mediator.Publish(new CutsceneFrameworkUpdateMessage());
                return;
            }

            if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
            {
                var zone = _clientState.TerritoryType;
                if (_lastZone != zone)
                {
                    _lastZone = zone;
                    if (!_sentBetweenAreas)
                    {
                        _logger.LogDebug("Zone switch/Gpose start");
                        _sentBetweenAreas = true;
                        _playerInfoCache.Clear();
                        Mediator.Publish(new ZoneSwitchStartMessage());
                        Mediator.Publish(new HaltScanMessage(nameof(ConditionFlag.BetweenAreas)));
                    }
                }

                return;
            }

            if (_sentBetweenAreas)
            {
                _logger.LogDebug("Zone switch/Gpose end");
                _sentBetweenAreas = false;
                Mediator.Publish(new ZoneSwitchEndMessage());
                Mediator.Publish(new ResumeScanMessage(nameof(ConditionFlag.BetweenAreas)));
            }

            var localPlayer = _objectTable.LocalPlayer;
            if (localPlayer != null)
            {
                _classJobId = localPlayer.ClassJob.RowId;
            }
            
            var target = _targetManager.Target as IPlayerCharacter;
            var targetEntityId = target?.EntityId ?? 0;
            if (targetEntityId != _lastTargetEntityId)
            {
                _lastTargetEntityId = targetEntityId;
                Mediator.Publish(new TargetPlayerChangedMessage(target));
            }

            HandleHousingPlotState();

            Mediator.Publish(new PriorityFrameworkUpdateMessage());

            if (!IsInCombatOrPerforming)
                Mediator.Publish(new FrameworkUpdateMessage());

            if (isNormalFrameworkUpdate)
                return;

            if (localPlayer != null && !IsLoggedIn)
            {
                _logger.LogDebug("Logged in");
                IsLoggedIn = true;
                _lastZone = _clientState.TerritoryType;
                Mediator.Publish(new DalamudLoginMessage());
            }
            else if (localPlayer == null && IsLoggedIn)
            {
                _logger.LogDebug("Logged out");
                IsLoggedIn = false;
                Mediator.Publish(new DalamudLogoutMessage());
            }

            if (IsInCombatOrPerforming)
                Mediator.Publish(new FrameworkUpdateMessage());

            Mediator.Publish(new DelayedFrameworkUpdateMessage());

            _delayedFrameworkUpdateCheck = DateTime.UtcNow;
        });
    }
    
}
