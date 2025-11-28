using Dalamud.Game.ClientState.Objects.Types;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using System.Collections.Concurrent;

namespace Snowcloak.Interop.Ipc;

public class RedrawManager
{
    private readonly SnowMediator _snowMediator;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ConcurrentDictionary<nint, bool> _penumbraRedrawRequests = [];
    private CancellationTokenSource _disposalCts = new();

    public SemaphoreSlim RedrawSemaphore { get; init; } = new(2, 2);

    public RedrawManager(SnowMediator snowMediator, DalamudUtilService dalamudUtil)
    {
        _snowMediator = snowMediator;
        _dalamudUtil = dalamudUtil;
    }

    public async Task PenumbraRedrawInternalAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, Action<ICharacter> action, CancellationToken token)
    {
        _snowMediator.Publish(new PenumbraStartRedrawMessage(handler.Address));

        _penumbraRedrawRequests[handler.Address] = true;

        try
        {
            using CancellationTokenSource cancelToken = new CancellationTokenSource();
            using CancellationTokenSource combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken.Token, token, _disposalCts.Token);
            var combinedToken = combinedCts.Token;
            cancelToken.CancelAfter(TimeSpan.FromSeconds(15));
            await handler.ActOnFrameworkAfterEnsureNoDrawAsync(action, combinedToken).ConfigureAwait(false);

            if (!_disposalCts.Token.IsCancellationRequested)
                await _dalamudUtil.WaitWhileCharacterIsDrawing(logger, handler, applicationId, 30000, combinedToken).ConfigureAwait(false);
        }
        finally
        {
            _penumbraRedrawRequests[handler.Address] = false;
            _snowMediator.Publish(new PenumbraEndRedrawMessage(handler.Address));
        }
    }

    internal void Cancel()
    {
        _disposalCts = _disposalCts.CancelRecreate();
    }
}
