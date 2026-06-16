using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using ElezenTools.Housing;
using ElezenTools.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.Utils;
using System.Numerics;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Snowcloak.Services;

public sealed class PlayerInteractionService
{
    private readonly IObjectTable _objectTable;
    private readonly IGameGui _gameGui;
    private readonly ITargetManager _targetManager;
    private readonly IPartyList _partyList;
    private readonly ObjectTableCache _objectTableCache;

    public PlayerInteractionService(
        IObjectTable objectTable,
        IGameGui gameGui,
        ITargetManager targetManager,
        IPartyList partyList,
        ObjectTableCache objectTableCache)
    {
        _objectTable = objectTable;
        _gameGui = gameGui;
        _targetManager = targetManager;
        _partyList = partyList;
        _objectTableCache = objectTableCache;
    }

    public async Task<bool> TargetPlayerByIdentAsync(string ident)
    {
        return await Service.RunOnFrameworkAsync(() =>
        {
            var addr = _objectTableCache.GetPlayerCharacterFromCachedTableByIdent(ident);
            if (addr == IntPtr.Zero)
            {
                return false;
            }

            var obj = _objectTable.CreateObjectReference(addr);
            if (obj == null)
            {
                return false;
            }

            _targetManager.Target = obj;
            return true;
        }).ConfigureAwait(false);
    }

    public bool TargetPlayerByIdentInRange(string ident, float maxDistance)
    {
        Service.EnsureOnFramework();
        var addr = _objectTableCache.GetPlayerCharacterFromCachedTableByIdent(ident);
        var pc = _objectTable.LocalPlayer;
        var gobj = _objectTable.CreateObjectReference(addr);

        if (gobj != null && pc != null && Vector3.Distance(pc.Position, gobj.Position) < maxDistance)
        {
            _targetManager.Target = gobj;
            return true;
        }

        return false;
    }

    public async Task<bool> ExaminePlayerByIdentAsync(string ident)
    {
        return await Service.RunOnFrameworkAsync(() =>
        {
            unsafe
            {
                var addr = _objectTableCache.GetPlayerCharacterFromCachedTableByIdent(ident);
                if (addr == IntPtr.Zero)
                {
                    return false;
                }

                var obj = _objectTable.CreateObjectReference(addr);
                if (obj is not IPlayerCharacter pc)
                {
                    return false;
                }

                AgentInspect.Instance()->ExamineCharacter(pc.EntityId);
                return true;
            }
        }).ConfigureAwait(false);
    }

    public async Task<bool> OpenAdventurerPlateByIdentAsync(string ident)
    {
        return await Service.RunOnFrameworkAsync(() =>
        {
            unsafe
            {
                var addr = _objectTableCache.GetPlayerCharacterFromCachedTableByIdent(ident);
                if (addr == IntPtr.Zero)
                {
                    return false;
                }

                var obj = _objectTable.CreateObjectReference(addr);
                if (obj is not IPlayerCharacter pc)
                {
                    return false;
                }

                AgentCharaCard.Instance()->OpenCharaCard((GameObject*)pc.Address);
                return true;
            }
        }).ConfigureAwait(false);
    }

    public static bool TryGetIdentFromMenuTarget(IMenuOpenedArgs args, out string ident)
    {
        ArgumentNullException.ThrowIfNull(args);
        ident = string.Empty;
        if (args.Target is not MenuTargetDefault target || target.TargetHomeWorld.RowId == 0)
        {
            return false;
        }

        var name = target.TargetName ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        ident = (name + target.TargetHomeWorld.RowId).GetHash256();
        return true;
    }

    public LocationInfo GetMapData()
    {
        var locationInfo = HousingLocationReader.ReadCurrentLocation();

        return new LocationInfo
        {
            ServerId = locationInfo.ServerId,
            MapId = locationInfo.MapId,
            TerritoryId = locationInfo.TerritoryId,
            DivisionId = locationInfo.DivisionId,
            WardId = locationInfo.WardId,
            HouseId = locationInfo.HouseId,
            RoomId = locationInfo.RoomId
        };
    }

    public static unsafe void SetMarkerAndOpenMap(Vector3 position, Lumina.Excel.Sheets.Map map)
    {
        Service.EnsureOnFramework();
        var agentMap = AgentMap.Instance();
        if (agentMap == null)
        {
            return;
        }

        agentMap->OpenMapByMapId(map.RowId);
        agentMap->SetFlagMapMarker(map.TerritoryType.RowId, map.RowId, position);
    }

    public async Task<LocationInfo> GetMapDataAsync()
    {
        return await Service.RunOnFrameworkAsync(GetMapData).ConfigureAwait(false);
    }

    public Vector2 WorldToScreen(IGameObject? obj)
    {
        if (obj == null)
        {
            return Vector2.Zero;
        }

        return _gameGui.WorldToScreen(obj.Position, out var screenPos) ? screenPos : Vector2.Zero;
    }

    public uint? GetTargetObjectId()
    {
        if (_targetManager.Target is IPlayerCharacter playerCharacter)
        {
            return playerCharacter.EntityId;
        }

        return null;
    }

    public IPlayerCharacter? GetTargetPlayerCharacter()
    {
        Service.EnsureOnFramework();
        if (_targetManager.Target is IPlayerCharacter playerCharacter)
        {
            return playerCharacter;
        }

        return null;
    }

    public async Task<IPlayerCharacter?> GetTargetPlayerCharacterAsync()
    {
        return await Service.RunOnFrameworkAsync(GetTargetPlayerCharacter).ConfigureAwait(false);
    }

    public IEnumerable<IPlayerCharacter> GetPartyPlayerCharacters()
    {
        Service.EnsureOnFramework();
        for (var i = 0; i < _partyList.Count; i++)
        {
            if (_partyList[i]?.GameObject is IPlayerCharacter partyMember)
            {
                yield return partyMember;
            }
        }
    }

    public static bool TryGetHousingPlotLocation(out HousingPlotLocation housingLocation, out bool isInsideHousing)
    {
        return HousingLocationReader.TryGetCurrentPlot(out housingLocation, out isInsideHousing);
    }
}
