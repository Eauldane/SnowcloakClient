using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI.SignalR.Utils;

namespace Snowcloak.WebAPI.SignalR;

internal enum ConnectionLifecyclePhase
{
    Disconnected,
    Authenticating,
    Connecting,
    SyncingState,
    Connected,
    Unauthorized,
    VersionMismatch,
    RateLimited
}

internal sealed class ConnectionLifecycle : IDisposable
{
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly HubFactory _hubFactory;
    private readonly ILogger _logger;
    private readonly SnowMediator _mediator;
    private readonly SingleFlightCts _connectionFlight = new();
    private readonly SingleFlightCts _healthCheckFlight = new();
    private SingleFlightCts.Scope? _connectionScope;
    private int _disposed;

    public ConnectionLifecycle(ILogger logger, HubFactory hubFactory, BackgroundTaskTracker backgroundTasks, SnowMediator mediator)
    {
        _logger = logger;
        _hubFactory = hubFactory;
        _backgroundTasks = backgroundTasks;
        _mediator = mediator;
        ConnectionToken = CancellationToken.None;
        State = ServerState.Offline;
        Phase = ConnectionLifecyclePhase.Disconnected;
    }

    public CancellationToken ConnectionToken { get; private set; }
    public HubConnection? Hub { get; private set; }
    public bool HooksRegistered { get; private set; }
    public ConnectionLifecyclePhase Phase { get; private set; }
    public ServerState State { get; private set; }

    public void CancelConnectionToken()
    {
        _connectionFlight.Cancel();
        ConnectionToken = new CancellationToken(canceled: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connectionScope?.Dispose();
        _connectionScope = null;
        _connectionFlight.Dispose();
        _healthCheckFlight.Dispose();
    }

    public async Task<HubConnection> GetOrCreateHub(CancellationToken token)
    {
        Hub = await _hubFactory.GetOrCreate(token).ConfigureAwait(false);
        HooksRegistered = false;
        return Hub;
    }

    public void MarkCallbacksRegistered()
    {
        HooksRegistered = true;
    }

    public void MovePhase(ConnectionLifecyclePhase phase)
    {
        if (Phase == phase)
        {
            return;
        }

        _logger.LogDebug("New connection phase: {phase}, prev phase: {previous}", phase, Phase);
        Phase = phase;
    }

    public void MoveTo(ServerState state)
    {
        _logger.LogDebug("New ServerState: {value}, prev ServerState: {previous}", state, State);
        State = state;

        Phase = state switch
        {
            ServerState.Connected => ConnectionLifecyclePhase.Connected,
            ServerState.Unauthorized => ConnectionLifecyclePhase.Unauthorized,
            ServerState.VersionMisMatch => ConnectionLifecyclePhase.VersionMismatch,
            ServerState.RateLimited => ConnectionLifecyclePhase.RateLimited,
            ServerState.Connecting => Phase is ConnectionLifecyclePhase.Authenticating or ConnectionLifecyclePhase.SyncingState ? Phase : ConnectionLifecyclePhase.Connecting,
            ServerState.Reconnecting => ConnectionLifecyclePhase.Connecting,
            _ => ConnectionLifecyclePhase.Disconnected
        };
    }

    public CancellationToken RenewConnectionToken()
    {
        var previousScope = _connectionScope;
        var scope = _connectionFlight.Begin();
        _connectionScope = scope;
        previousScope?.Dispose();
        ConnectionToken = scope.Token;
        return ConnectionToken;
    }

    public void StartHealthLoop(Func<CancellationToken, Task> healthLoop)
    {
        var healthCheckScope = _healthCheckFlight.Begin();
        var healthCheckToken = healthCheckScope.Token;
        _ = _backgroundTasks.Run(async () =>
        {
            using (healthCheckScope)
            {
                await healthLoop(healthCheckToken).ConfigureAwait(false);
            }
        }, nameof(StartHealthLoop));
    }

    public void StopHealthLoop()
    {
        _healthCheckFlight.Cancel();
    }

    public async Task StopAsync(ServerState state, string serverName, Action stopSystemInfoPolling)
    {
        MoveTo(ServerState.Disconnecting);

        _logger.LogInformation("Stopping existing connection");
        await _hubFactory.DisposeHubAsync().ConfigureAwait(false);

        if (Hub is not null)
        {
            _mediator.Publish(new EventMessage(new Event(nameof(ApiController), EventSeverity.Informational,
                $"Stopping existing connection to {serverName}")));

            HooksRegistered = false;
            StopHealthLoop();
            stopSystemInfoPolling();
            _mediator.Publish(new DisconnectedMessage());
            Hub = null;
        }

        MoveTo(state);
    }
}
