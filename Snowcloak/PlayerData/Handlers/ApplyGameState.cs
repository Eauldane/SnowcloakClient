using Microsoft.Extensions.Logging;
using Snowcloak.Services;

namespace Snowcloak.PlayerData.Handlers;

internal sealed class ApplyGameState(DalamudUtilService dalamudUtil) : IApplyGameState
{
    public Task WaitWhileCharacterIsDrawing(ILogger logger, IGameObjectHandle handler, Guid applicationId, int timeoutMs, CancellationToken token)
        => ObjectTableCache.WaitWhileCharacterIsDrawing(logger, (GameObjectHandler)handler, applicationId, timeoutMs, token);

    public nint GetCompanion(nint playerPointer) => dalamudUtil.GetCompanion(playerPointer);

    public nint GetMinionOrMount(nint playerPointer) => dalamudUtil.GetMinionOrMount(playerPointer);

    public nint GetPet(nint playerPointer) => dalamudUtil.GetPet(playerPointer);
}
