using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Microsoft.Extensions.Logging;

namespace Snowcloak.Game.Interop;

public sealed unsafe partial class BlockedCharacterHandler
    : IDisposable
{
    private sealed record CharaData(ulong AccId, ulong ContentId);
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<CharaData, bool> _blockedCharacterCache = new();

    private readonly IClientState _clientState;
    private readonly ILogger<BlockedCharacterHandler> _logger;

    public BlockedCharacterHandler(ILogger<BlockedCharacterHandler> logger, IGameInteropProvider gameInteropProvider, IClientState clientState)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(gameInteropProvider);
        ArgumentNullException.ThrowIfNull(clientState);

        gameInteropProvider.InitializeFromAttributes(this);
        _clientState = clientState;
        _logger = logger;
        _clientState.TerritoryChanged += OnTerritoryChanged;
    }

    private static CharaData GetIdsFromPlayerPointer(nint characterAddress)
    {
        if (characterAddress == nint.Zero) return new(0, 0);
        var castChar = ((BattleChara*)characterAddress);
        return new(castChar->Character.AccountId, castChar->Character.ContentId);
    }

    public bool IsCharacterBlocked(nint characterAddress, out bool firstTime)
    {
        firstTime = false;
        var combined = GetIdsFromPlayerPointer(characterAddress);
        lock (_cacheLock)
        {
            if (_blockedCharacterCache.TryGetValue(combined, out var isBlocked))
                return isBlocked;
        }

        firstTime = true;
        var blockStatus = InfoProxyBlacklist.Instance()->GetBlockResultType(combined.AccId, combined.ContentId);
        LogBlockStatus(_logger, characterAddress, blockStatus);
        if ((int)blockStatus == 0)
            return false;

        var blocked = blockStatus != InfoProxyBlacklist.BlockResultType.NotBlocked;
        lock (_cacheLock)
        {
            _blockedCharacterCache[combined] = blocked;
        }

        return blocked;
    }

    private void OnTerritoryChanged(uint _)
    {
        lock (_cacheLock)
        {
            _blockedCharacterCache.Clear();
        }
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Trace, Message = "CharaPtr {CharacterAddress} is BlockStatus: {Status}")]
    private static partial void LogBlockStatus(ILogger logger, nint characterAddress, InfoProxyBlacklist.BlockResultType status);
}
