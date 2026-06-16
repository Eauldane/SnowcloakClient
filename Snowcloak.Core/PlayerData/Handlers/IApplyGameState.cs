using Microsoft.Extensions.Logging;

namespace Snowcloak.PlayerData.Handlers;

public interface IApplyGameState
{
    Task WaitWhileCharacterIsDrawing(ILogger logger, IGameObjectHandle handler, Guid applicationId, int timeoutMs, CancellationToken token);

    nint GetCompanion(nint playerPointer);

    nint GetMinionOrMount(nint playerPointer);

    nint GetPet(nint playerPointer);
}
