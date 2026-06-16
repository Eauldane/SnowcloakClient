using Dalamud.Game.ClientState.Objects.Types;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using System.Collections.Concurrent;

namespace Snowcloak.Interop.Ipc;

public sealed class RedrawManager : IDisposable
{
    private readonly SnowMediator _snowMediator;
    private readonly ConcurrentDictionary<nint, bool> _penumbraRedrawRequests = [];
    private CancellationTokenSource _disposalCts = new();
    private readonly SemaphoreSlim _redrawSlots = new(2, 2);
    private int _disposed;

    public RedrawManager(SnowMediator snowMediator)
    {
        _snowMediator = snowMediator;
    }

    public async Task RunWithRedrawSlotAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, Action<ICharacter> action, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(action);

        await _redrawSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RunPenumbraRedrawAsync(logger, handler, applicationId, action, token).ConfigureAwait(false);
        }
        finally
        {
            _redrawSlots.Release();
        }
    }

    private async Task RunPenumbraRedrawAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, Action<ICharacter> action, CancellationToken token)
    {
        _snowMediator.Publish(new PenumbraStartRedrawMessage(handler.Address));

        _penumbraRedrawRequests[handler.Address] = true;

        try
        {
            var disposalToken = _disposalCts.Token;
            using CancellationTokenSource cancelToken = new CancellationTokenSource();
            using CancellationTokenSource combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken.Token, token, disposalToken);
            var combinedToken = combinedCts.Token;
            cancelToken.CancelAfter(TimeSpan.FromSeconds(15));
            await handler.ActOnFrameworkAfterEnsureNoDrawAsync(action, combinedToken).ConfigureAwait(false);

            if (!disposalToken.IsCancellationRequested)
                await ObjectTableCache.WaitWhileCharacterIsDrawing(logger, handler, applicationId, 30000, combinedToken).ConfigureAwait(false);
        }
        finally
        {
            _penumbraRedrawRequests[handler.Address] = false;
            _snowMediator.Publish(new PenumbraEndRedrawMessage(handler.Address));
        }
    }

    internal void Cancel()
    {
        var previous = Interlocked.Exchange(ref _disposalCts, new CancellationTokenSource());
        previous.CancelDispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposalCts.CancelDispose();
        _redrawSlots.Dispose();
    }
}
