using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ElezenTools.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Microsoft.Extensions.Logging;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using VisibilityFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.VisibilityFlags;

namespace Snowcloak.Services;

public sealed class GposeService
{
    private readonly ILogger<GposeService> _logger;
    private readonly IObjectTable _objectTable;

    public GposeService(ILogger<GposeService> logger, IObjectTable objectTable)
    {
        _logger = logger;
        _objectTable = objectTable;
    }

    public static unsafe GameObject* GposeTarget
    {
        get => TargetSystem.Instance()->GPoseTarget;
        set => TargetSystem.Instance()->GPoseTarget = value;
    }

    private static unsafe bool HasGposeTarget => GposeTarget != null;
    private static unsafe int GposeTargetIndex => !HasGposeTarget ? -1 : GposeTarget->ObjectIndex;

    public async Task<IGameObject?> GetGposeTargetGameObjectAsync()
    {
        if (!HasGposeTarget)
        {
            return null;
        }

        return await Service.RunOnFrameworkAsync(() => _objectTable[GposeTargetIndex]).ConfigureAwait(true);
    }

    public async Task<ICharacter?> GetGposeCharacterFromObjectTableByNameAsync(string name, bool onlyGposeCharacters = false)
    {
        return await Service.RunOnFrameworkAsync(() => GetGposeCharacterFromObjectTableByName(name, onlyGposeCharacters)).ConfigureAwait(false);
    }

    public ICharacter? GetGposeCharacterFromObjectTableByName(string name, bool onlyGposeCharacters = false)
    {
        Service.EnsureOnFramework();
        return (ICharacter?)_objectTable.FirstOrDefault(i =>
            (!onlyGposeCharacters || i.ObjectIndex >= 200)
            && string.Equals(i.Name.ToString(), name, StringComparison.Ordinal));
    }

    public IEnumerable<ICharacter?> GetGposeCharactersFromObjectTable()
    {
        Service.EnsureOnFramework();
        return _objectTable.Where(o => o.ObjectIndex > 200 && o.ObjectKind == ObjectKind.Pc).Cast<ICharacter>();
    }

    public unsafe void WaitWhileGposeCharacterIsDrawing(IntPtr characterAddress, int timeOut = 5000)
    {
        Thread.Sleep(500);
        var obj = (GameObject*)characterAddress;
        const int tick = 250;
        var curWaitTime = 0;
        _logger.LogTrace("RenderFlags: {flags}", obj->RenderFlags.ToString("X"));
        while (obj->RenderFlags != VisibilityFlags.None && curWaitTime < timeOut)
        {
            _logger.LogTrace("Waiting for gpose actor to finish drawing");
            curWaitTime += tick;
            Thread.Sleep(tick);
        }

        Thread.Sleep(tick * 2);
    }
}
