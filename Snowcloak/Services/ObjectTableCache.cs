using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Player;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ElezenTools.Data;
using ElezenTools.Data.Classes;
using ElezenTools.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Microsoft.Extensions.Logging;
using Snowcloak.Game.Interop;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Utils;
using System.Globalization;
using DalamudGameObject = Dalamud.Game.ClientState.Objects.Types.IGameObject;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using VisibilityFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.VisibilityFlags;

namespace Snowcloak.Services;

public sealed class ObjectTableCache
{
    private readonly HashSet<uint> _classJobIdsIgnoredForPets = [30];
    private readonly ILogger<ObjectTableCache> _logger;
    private readonly IObjectTable _objectTable;
    private readonly IPlayerState _playerState;
    private readonly BlockedCharacterHandler _blockedCharacterHandler;
    private readonly Dictionary<string, PlayerCharacterData> _playerCharas = new(StringComparer.Ordinal);
    private readonly List<string> _notUpdatedCharas = [];
    private IReadOnlyDictionary<string, PlayerCharacterData> _snapshot = new Dictionary<string, PlayerCharacterData>(StringComparer.Ordinal);
    private string _lastGlobalBlockPlayer = string.Empty;
    private string _lastGlobalBlockReason = string.Empty;
    private uint _classJobId;

    public ObjectTableCache(
        ILogger<ObjectTableCache> logger,
        IObjectTable objectTable,
        IPlayerState playerState,
        BlockedCharacterHandler blockedCharacterHandler)
    {
        _logger = logger;
        _objectTable = objectTable;
        _playerState = playerState;
        _blockedCharacterHandler = blockedCharacterHandler;
    }

    public bool IsAnythingDrawing { get; private set; }
    public uint ClassJobId => _classJobId;

    public void Initialise()
    {
        if (_objectTable.LocalPlayer != null)
        {
            _classJobId = _playerState.ClassJob.RowId;
        }
    }

    public void SetLocalClassJob(ICharacter? localPlayer)
    {
        if (localPlayer != null)
        {
            _classJobId = localPlayer.ClassJob.RowId;
        }
    }

    public void Refresh(bool skipUpdate)
    {
        IsAnythingDrawing = false;

        if (skipUpdate)
        {
            return;
        }

        _notUpdatedCharas.AddRange(_playerCharas.Keys);

        for (var i = 0; i < _objectTable.Length; i++)
        {
            var chara = _objectTable[i];
            if (chara == null || chara.ObjectKind != ObjectKind.Pc)
            {
                continue;
            }

            if (_blockedCharacterHandler.IsCharacterBlocked(chara.Address, out var firstTime) && firstTime)
            {
                _logger.LogTrace("Skipping character {Address}, blocked/muted", chara.Address.ToString("X", CultureInfo.InvariantCulture));
                continue;
            }

            var info = GetPlayerInfo(chara);

            if (!IsAnythingDrawing)
            {
                CheckCharacterForDrawing(info.Character);
            }

            _notUpdatedCharas.Remove(info.Hash);
            _playerCharas[info.Hash] = info.Character;
        }

        foreach (var notUpdatedChara in _notUpdatedCharas)
        {
            _playerCharas.Remove(notUpdatedChara);
        }

        _notUpdatedCharas.Clear();
        _snapshot = new Dictionary<string, PlayerCharacterData>(_playerCharas, StringComparer.Ordinal);
    }

    public void FinishDrawingPass()
    {
        if (!IsAnythingDrawing && !string.IsNullOrEmpty(_lastGlobalBlockPlayer))
        {
            _logger.LogTrace("Global draw block: END => {name}", _lastGlobalBlockPlayer);
            _lastGlobalBlockPlayer = string.Empty;
            _lastGlobalBlockReason = string.Empty;
        }
    }

    public IGameObject? CreateGameObject(IntPtr reference)
    {
        Service.EnsureOnFramework();
        return _objectTable.CreateObjectReference(reference);
    }

    public async Task<IGameObject?> CreateGameObjectAsync(IntPtr reference)
    {
        return await Service.RunOnFrameworkAsync(() => _objectTable.CreateObjectReference(reference)).ConfigureAwait(false);
    }

    public ICharacter? GetCharacterFromObjectTableByIndex(int index)
    {
        Service.EnsureOnFramework();
        var objTableObj = _objectTable[index];
        if (objTableObj?.ObjectKind != ObjectKind.Pc)
        {
            return null;
        }

        return (ICharacter)objTableObj;
    }

    public unsafe IntPtr GetCompanion(IntPtr? playerPointer = null)
    {
        Service.EnsureOnFramework();
        var mgr = CharacterManager.Instance();
        playerPointer ??= GetPlayerPointer();
        if (playerPointer == IntPtr.Zero || (IntPtr)mgr == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return (IntPtr)mgr->LookupBuddyByOwnerObject((BattleChara*)playerPointer);
    }

    public async Task<IntPtr> GetCompanionAsync(IntPtr? playerPointer = null)
    {
        return await Service.RunOnFrameworkAsync(() => GetCompanion(playerPointer)).ConfigureAwait(false);
    }

    public bool GetIsPlayerPresent()
    {
        Service.EnsureOnFramework();
        return _objectTable.LocalPlayer != null && _objectTable.LocalPlayer.IsValid();
    }

    public async Task<bool> GetIsPlayerPresentAsync()
    {
        return await Service.RunOnFrameworkAsync(GetIsPlayerPresent).ConfigureAwait(false);
    }

    public unsafe IntPtr GetMinionOrMount(IntPtr? playerPointer = null)
    {
        Service.EnsureOnFramework();
        playerPointer ??= GetPlayerPointer();
        if (playerPointer == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return _objectTable.GetObjectAddress(((GameObject*)playerPointer)->ObjectIndex + 1);
    }

    public async Task<IntPtr> GetMinionOrMountAsync(IntPtr? playerPointer = null)
    {
        return await Service.RunOnFrameworkAsync(() => GetMinionOrMount(playerPointer)).ConfigureAwait(false);
    }

    public unsafe IntPtr GetPet(IntPtr? playerPointer = null)
    {
        Service.EnsureOnFramework();
        if (_classJobIdsIgnoredForPets.Contains(_classJobId))
        {
            return IntPtr.Zero;
        }

        var mgr = CharacterManager.Instance();
        playerPointer ??= GetPlayerPointer();
        if (playerPointer == IntPtr.Zero || (IntPtr)mgr == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return (IntPtr)mgr->LookupPetByOwnerObject((BattleChara*)playerPointer);
    }

    public async Task<IntPtr> GetPetAsync(IntPtr? playerPointer = null)
    {
        return await Service.RunOnFrameworkAsync(() => GetPet(playerPointer)).ConfigureAwait(false);
    }

    public async Task<IPlayerCharacter> GetPlayerCharacterAsync()
    {
        return await Service.RunOnFrameworkAsync(GetPlayerCharacter).ConfigureAwait(false);
    }

    public IPlayerCharacter GetPlayerCharacter()
    {
        Service.EnsureOnFramework();
        return _objectTable.LocalPlayer!;
    }

    public IntPtr GetPlayerCharacterFromCachedTableByName(string characterName)
    {
        foreach (var c in _snapshot.Values)
        {
            if (c.Name.Equals(characterName, StringComparison.Ordinal))
            {
                return c.Address;
            }
        }

        return IntPtr.Zero;
    }

    public IntPtr GetPlayerCharacterFromCachedTableByIdent(string characterName)
    {
        return _snapshot.TryGetValue(characterName, out var pchar) ? pchar.Address : IntPtr.Zero;
    }

    public bool IsFriendByIdent(string ident)
    {
        Service.EnsureOnFramework();
        var addr = GetPlayerCharacterFromCachedTableByIdent(ident);
        if (addr == IntPtr.Zero)
        {
            return false;
        }

        var obj = _objectTable.CreateObjectReference(addr);
        if (obj is not IPlayerCharacter pc)
        {
            return false;
        }

        return pc.StatusFlags.HasFlag(StatusFlags.Friend);
    }

    public async Task<bool> IsFriendByIdentAsync(string ident)
    {
        return await Service.RunOnFrameworkAsync(() => IsFriendByIdent(ident)).ConfigureAwait(false);
    }

    public string GetPlayerName()
    {
        Service.EnsureOnFramework();
        return _playerState.CharacterName ?? "--";
    }

    public async Task<string> GetPlayerNameAsync()
    {
        return await Service.RunOnFrameworkAsync(GetPlayerName).ConfigureAwait(false);
    }

    public async Task<string> GetPlayerNameHashedAsync()
    {
        return await Service.RunOnFrameworkAsync(() => (GetPlayerName() + GetHomeWorldId()).GetHash256()).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetNearbyPlayerNameHashesAsync(int maxPlayers = 0)
    {
        return await Service.RunOnFrameworkAsync(() =>
        {
            var hashes = _snapshot.Keys;
            return maxPlayers > 0
                ? hashes.Take(maxPlayers).ToList()
                : hashes.ToList();
        }).ConfigureAwait(false);
    }

    public IntPtr GetPlayerPointer()
    {
        Service.EnsureOnFramework();
        return _objectTable.LocalPlayer?.Address ?? IntPtr.Zero;
    }

    public async Task<IntPtr> GetPlayerPointerAsync()
    {
        return await Service.RunOnFrameworkAsync(GetPlayerPointer).ConfigureAwait(false);
    }

    public uint GetHomeWorldId()
    {
        Service.EnsureOnFramework();
        return _objectTable.LocalPlayer?.HomeWorld.RowId ?? 0;
    }

    public uint GetWorldId()
    {
        Service.EnsureOnFramework();
        return _objectTable.LocalPlayer!.CurrentWorld.RowId;
    }

    public string GetDataCenterRegion()
    {
        Service.EnsureOnFramework();
        var worldId = (ushort)(_objectTable.LocalPlayer?.HomeWorld.RowId ?? 0);
        return TryGetWorldRegion(worldId, out var region) ? region : string.Empty;
    }

    public static bool TryGetWorldRegion(ushort worldId, out string region)
    {
        region = string.Empty;
        if (worldId == 0)
        {
            return false;
        }

        var world = ElezenData.Worlds.GetById(worldId, Dalamud.Game.ClientLanguage.English);
        if (!world.HasValue || string.IsNullOrWhiteSpace(world.Value.RegionName))
        {
            return false;
        }

        region = world.Value.RegionName;
        return true;
    }

    public async Task<uint> GetWorldIdAsync()
    {
        return await Service.RunOnFrameworkAsync(GetWorldId).ConfigureAwait(false);
    }

    public async Task<uint> GetHomeWorldIdAsync()
    {
        return await Service.RunOnFrameworkAsync(GetHomeWorldId).ConfigureAwait(false);
    }

    public bool IsGameObjectPresent(IntPtr key)
    {
        return _objectTable.Any(f => f.Address == key);
    }

    public static bool IsObjectPresent(IGameObject? obj)
    {
        Service.EnsureOnFramework();
        return obj != null && obj.IsValid();
    }

    public static async Task<bool> IsObjectPresentAsync(IGameObject? obj)
    {
        return await Service.RunOnFrameworkAsync(() => IsObjectPresent(obj)).ConfigureAwait(false);
    }

    public static async Task WaitWhileCharacterIsDrawing(ILogger logger, GameObjectHandler handler, Guid redrawId, int timeOut = 5000, CancellationToken? ct = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(handler);

        if (Service.ClientState is { IsLoggedIn: false })
        {
            return;
        }

        const int tick = 250;
        var curWaitTime = 0;
        try
        {
            logger.LogTrace("[{redrawId}] Starting wait for {handler} to draw", redrawId, handler);
            await Task.Delay(tick).ConfigureAwait(true);
            curWaitTime += tick;

            while ((!ct?.IsCancellationRequested ?? true)
                   && curWaitTime < timeOut
                   && await handler.IsBeingDrawnRunOnFrameworkAsync().ConfigureAwait(false))
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

    public PlayerCharacterData FindPlayerByNameHash(string ident)
    {
        _snapshot.TryGetValue(ident, out var result);
        return result;
    }

    private unsafe PlayerInfo GetPlayerInfo(DalamudGameObject chara)
    {
        var battleChara = (BattleChara*)chara.Address;
        var customizeData = battleChara->DrawData.CustomizeData.Data;
        var playerCharacter = chara as IPlayerCharacter;
        var character = chara as ICharacter;
        var homeWorldId = (uint)battleChara->Character.HomeWorld;
        var currentWorldId = playerCharacter?.CurrentWorld.RowId ?? 0u;
        var classJobId = character?.ClassJob.RowId ?? battleChara->Character.ClassJob;
        var level = (short)(character?.Level ?? 0);

        var playerData = new PlayerCharacterData(
            chara.GameObjectId,
            chara.EntityId,
            _objectTable.LocalPlayer?.Address == chara.Address ? _playerState.ContentId : 0,
            chara.Address,
            chara.Name.TextValue,
            currentWorldId,
            homeWorldId,
            classJobId,
            customizeData[(int)CustomizeIndex.Race],
            customizeData[(int)CustomizeIndex.Tribe],
            (Sex)customizeData[(int)CustomizeIndex.Gender],
            level,
            level,
            false);

        return new PlayerInfo(playerData, Crypto.GetHash256(playerData.Name + playerData.HomeWorldId.ToString(CultureInfo.InvariantCulture)));
    }

    private unsafe void CheckCharacterForDrawing(PlayerCharacterData p)
    {
        var gameObj = (GameObject*)p.Address;
        var drawObj = gameObj->DrawObject;
        var characterName = p.Name;
        var isDrawing = false;
        var isDrawingChanged = false;

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

    private readonly record struct PlayerInfo(PlayerCharacterData Character, string Hash);
}
