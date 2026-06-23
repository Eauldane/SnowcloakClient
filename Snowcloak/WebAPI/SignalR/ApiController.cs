using Snowcloak.API.Data;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.User;
using Snowcloak.API.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Reflection;
using Snowcloak.WebAPI.SignalR;

namespace Snowcloak.WebAPI;

#pragma warning disable MA0040
public sealed partial class ApiController : DisposableMediatorSubscriberBase, ISnowHubClient, ISnowHub, IAsyncDisposable
{
    private const string TransientAuthenticationMessage = "Previous session is still closing on the server. Snowcloak will reconnect automatically.";
    private static readonly TimeSpan TransientAuthenticationRetryDelay = TimeSpan.FromSeconds(1);

    // Dev builds should most likely be run against a dev server, so we define that here. 
    // Most of the time this'll be local or at least on LAN (and if not, VPNed)
    // so SSL isn't strictly needed. 
    //
    // For the sake of adding a barrier for those who don't understand the risks here, it's
    // on a fake TLD sdo that you need to edit your host file as an affirmative "I know what
    // I'm doing" step.
    // 
    // If you're testing against the live server for some reason, do a release build.
    #if DEBUG
        public const string SnowcloakServer = "Snowcloak Dev Server";
        public const string SnowcloakServiceUri = "ws://hub.snow.cloak";
        public const string SnowcloakServiceApiUri = "ws://hub.snow.cloak/";
        public const string SnowcloakServiceHubUri = "ws://hub.snow.cloak/snow";
    #else 
        public const string SnowcloakServer = "Snowcloak Main Server";
        public const string SnowcloakServiceUri = "wss://hub.snowcloak-sync.com";
        public const string SnowcloakServiceApiUri = "wss://hub.snowcloak-sync.com/";
        public const string SnowcloakServiceHubUri = "wss://hub.snowcloak-sync.com/snow";
    #endif
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly ConnectionLifecycle _connectionLifecycle;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly PairManager _pairManager;
    private readonly PairRequestService _pairRequestService;
    private readonly ServerRegistry _serverManager;
    private readonly ShellConfigStore _shellConfigStore;
    private readonly TokenProvider _tokenProvider;
    private readonly SingleFlightCts _systemInfoPollFlight = new();
    private CancellationToken _systemInfoPollToken = CancellationToken.None;
    private ConnectionContext _connectionContext = ConnectionContext.Empty;
    private bool _doNotNotifyOnNextInfo;
    private CensusUpdateMessage? _lastCensus;
    private int _disposed;
    private HubConnection? _snowHub => _connectionLifecycle.Hub;

    public ApiController(ILogger<ApiController> logger, HubFactory hubFactory, DalamudUtilService dalamudUtil,
        PairManager pairManager, PairRequestService pairRequestService, ServerRegistry serverManager, ShellConfigStore shellConfigStore, SnowMediator mediator,
        TokenProvider tokenProvider) : base(logger, mediator)
    {
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _connectionLifecycle = new ConnectionLifecycle(logger, hubFactory, _backgroundTasks, mediator);
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _pairRequestService = pairRequestService;
        _serverManager = serverManager;
        _shellConfigStore = shellConfigStore;
        _tokenProvider = tokenProvider;

        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => DalamudUtilOnLogIn());
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => DalamudUtilOnLogOut());
        Mediator.Subscribe<HubClosedMessage>(this, (msg) => SnowHubOnClosed(msg.Exception));
        Mediator.Subscribe<HubReconnectedMessage>(this, (msg) => _ = _backgroundTasks.Run(SnowHubOnReconnected, nameof(SnowHubOnReconnected)));
        Mediator.Subscribe<HubReconnectingMessage>(this, (msg) => SnowHubOnReconnecting(msg.Exception));
        Mediator.Subscribe<CyclePauseMessage>(this, (msg) => _ = CyclePause(msg.UserData));
        Mediator.Subscribe<CensusUpdateMessage>(this, (msg) => _lastCensus = msg);
        Mediator.Subscribe<PauseMessage>(this, (msg) => _ = Pause(msg.UserData));
        Mediator.Subscribe<RequestPairDataMessage>(this, (msg) => _ = _backgroundTasks.Run(() => UserRequestData(msg.UserData), nameof(UserRequestData)));
        Mediator.Subscribe<PairApplicationCompletedMessage>(this, (msg) => _ = _backgroundTasks.Run(() => SendApplicationReceipt(msg), nameof(SendApplicationReceipt)));

        ServerState = ServerState.Offline;

        if (_dalamudUtil.IsLoggedIn)
        {
            DalamudUtilOnLogIn();
        }
    }

    public string AuthFailureMessage { get; private set; } = string.Empty;

    public Version CurrentClientVersion => _connectionContext.CurrentClientVersion;

    public string DisplayName => _connectionContext.DisplayName;
    public string DisplayColour => _connectionContext.DisplayColour;
    public string DisplayGlowColour => _connectionContext.DisplayGlowColour;
    public bool HasPersistentKey => _connectionContext.HasPersistentKey;
    public bool HexAllowed => _connectionContext.HexAllowed;
    public string? VanityId => _connectionContext.VanityId;
    public bool IsConnected => ServerState == ServerState.Connected;

    public bool IsCurrentVersion => _connectionContext.IsCurrentVersion;

    public int OnlineUsers => SystemInfoDto.OnlineUsers;

    public bool ServerAlive => ServerState is ServerState.Connected or ServerState.RateLimited or ServerState.Unauthorized or ServerState.Disconnected;

    public ServerInfo ServerInfo => _connectionContext.ServerInfo;

    public ServerState ServerState
    {
        get => _connectionLifecycle.State;
        private set => _connectionLifecycle.MoveTo(value);
    }

    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public string UID => _connectionContext.UID;

    public Task<bool> CheckClientHealth() => CheckClientHealth(CancellationToken.None);

    private async Task<bool> CheckClientHealth(CancellationToken cancellationToken)
    {
        var hub = _snowHub;
        if (hub == null || hub.State != HubConnectionState.Connected)
        {
            return false;
        }

        return await hub.InvokeAsync<bool>(nameof(CheckClientHealth), cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateConnections()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Logger.LogDebug("CreateConnections called");

        if (_serverManager.CurrentServer?.FullPause ?? true)
        {
            Logger.LogInformation("Not recreating Connection, paused");
            _connectionContext = ConnectionContext.Empty;
            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
            _connectionLifecycle.CancelConnectionToken();
            return;
        }

        var secretKey = _serverManager.GetSecretKey(out bool multi);
        if (multi)
        {
            Logger.LogWarning("Multiple secret keys for current character");
            _connectionContext = ConnectionContext.Empty;
            Mediator.Publish(new NotificationMessage("Multiple Identical Characters detected", "Your Service configuration has multiple characters with the same name and world set up. Delete the duplicates in the character management to be able to connect.",
                NotificationType.Error));
            await StopConnection(ServerState.MultiChara).ConfigureAwait(false);
            _connectionLifecycle.CancelConnectionToken();
            return;
        }

        if (secretKey == null)
        {
            Logger.LogWarning("No secret key set for current character");
            _connectionContext = ConnectionContext.Empty;
            await StopConnection(ServerState.NoSecretKey).ConfigureAwait(false);
            _connectionLifecycle.CancelConnectionToken();
            return;
        }

        await StopConnection(ServerState.Disconnected).ConfigureAwait(false);

        Logger.LogInformation("Recreating Connection");
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Informational,
            $"Starting Connection to {_serverManager.CurrentServer.ServerName}")));

        var token = _connectionLifecycle.RenewConnectionToken();
        while (ServerState is not ServerState.Connected && !token.IsCancellationRequested)
        {
            AuthFailureMessage = string.Empty;

            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
            ServerState = ServerState.Connecting;
            _connectionLifecycle.MovePhase(ConnectionLifecyclePhase.Authenticating);

            try
            {
                Logger.LogDebug("Building connection");

                try
                {
                    await _tokenProvider.GetOrUpdateToken(token).ConfigureAwait(false);
                }
                catch (SnowAuthFailureException ex) when (ex.IsTransient)
                {
                    AuthFailureMessage = TransientAuthenticationMessage;
                    ServerState = ServerState.Reconnecting;
                    await DelayBeforeTransientAuthenticationRetry(token).ConfigureAwait(false);
                    continue;
                }
                catch (SnowAuthFailureException ex)
                {
                    AuthFailureMessage = ex.Reason;
                    throw new HttpRequestException("Error during authentication", ex, System.Net.HttpStatusCode.Unauthorized);
                }

                while (!await _dalamudUtil.GetIsPlayerPresentAsync().ConfigureAwait(false) && !token.IsCancellationRequested)
                {
                    Logger.LogDebug("Player not loaded in yet, waiting");
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested) break;

                _connectionLifecycle.MovePhase(ConnectionLifecyclePhase.Connecting);
                var hub = await _connectionLifecycle.GetOrCreateHub(token).ConfigureAwait(false);
                InitializeApiHooks();

                await hub.StartAsync(token).ConfigureAwait(false);

                _connectionLifecycle.MovePhase(ConnectionLifecyclePhase.SyncingState);
                var connectionDto = await GetConnectionDto(publishConnected: false).ConfigureAwait(false);
                _connectionContext = ConnectionContext.From(connectionDto);
                _lastPublishedNews = string.IsNullOrWhiteSpace(connectionDto.News) ? null : connectionDto.News.Trim();

                await CheckClientHealth().ConfigureAwait(false);

                ServerState = ServerState.Connected;
                TriggerSystemInfoRefresh();

                var currentClientVer = Assembly.GetExecutingAssembly().GetName().Version!;
 
                if (connectionDto.ServerVersion != ISnowHub.ApiVersion)
                {
                    Mediator.Publish(new NotificationMessage("Client incompatible",
                        "This client version is incompatible and will not be able to connect. Please update your Snowcloak client.",
                        NotificationType.Error));
                    await StopConnection(ServerState.VersionMisMatch).ConfigureAwait(false);
                    return;
                }

                if (connectionDto.CurrentClientVersion > currentClientVer)
                {
                    Mediator.Publish(new NotificationMessage("Client outdated",
                        $"Your client is outdated ({currentClientVer.Major}.{currentClientVer.Minor}.{currentClientVer.Build}), current is: " +
                        $"{connectionDto.CurrentClientVersion.Major}.{connectionDto.CurrentClientVersion.Minor}.{connectionDto.CurrentClientVersion.Build}. " +
                        $"Please keep your Snowcloak client up-to-date.",
                        NotificationType.Warning, TimeSpan.FromSeconds(15)));
                }

                if (_dalamudUtil.HasModifiedGameFiles)
                {
                    Logger.LogWarning("Detected modified game files on connection");
#if false
                        Mediator.Publish(new NotificationMessage("Modified Game Files detected",
                            "Dalamud has reported modified game files in your FFXIV installation. " +
                            "You will be able to connect, but the synchronization functionality might be (partially) broken. " +
                            "Exit the game and repair it through XIVLauncher to get rid of this message.",
                            NotificationType.Error, TimeSpan.FromSeconds(15)));
#endif
                }

                await LoadIninitialPairs().ConfigureAwait(false);
                await LoadOnlinePairs().ConfigureAwait(false);
                Mediator.Publish(new ConnectedMessage(connectionDto));
            }
            catch (OperationCanceledException ex)
            {
                if (token.IsCancellationRequested)
                {
                    Logger.LogWarning("Connection attempt cancelled");
                    return;
                }

                Logger.LogWarning(ex, "Connection attempt timed out, retrying");
                ServerState = ServerState.Reconnecting;
                await DelayBeforeConnectionRetry(token).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException ex)
            {
                Logger.LogWarning(ex, "HttpRequestException on Connection");

                if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    await StopConnection(ServerState.Unauthorized).ConfigureAwait(false);
                    return;
                }

                if (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    await StopConnection(ServerState.RateLimited).ConfigureAwait(false);
                    return;
                }

                ServerState = ServerState.Reconnecting;
                Logger.LogInformation("Failed to establish connection, retrying");
                await DelayBeforeConnectionRetry(token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogWarning(ex, "InvalidOperationException on connection");
                await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Exception on Connection");

                Logger.LogInformation("Failed to establish connection, retrying");
                await DelayBeforeConnectionRetry(token).ConfigureAwait(false);
            }
        }
    }

    private static Task DelayBeforeConnectionRetry(CancellationToken token)
    {
        return Task.Delay(TimeSpan.FromSeconds(System.Security.Cryptography.RandomNumberGenerator.GetInt32(5, 20)), token);
    }

    private static Task DelayBeforeTransientAuthenticationRetry(CancellationToken token)
    {
        return Task.Delay(TransientAuthenticationRetryDelay, token);
    }

    public Task CyclePause(UserData userData)
    {
        _ = _backgroundTasks.Run(async () =>
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _connectionLifecycle.ConnectionToken);
                var token = linkedCts.Token;
                var pair = _pairManager.GetOnlineUserPairs().Single(p => p.UserPair != null && p.UserData == userData);
                var perm = pair.UserPair!.OwnPermissions;
                perm.SetPaused(paused: true);
                await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
                // wait until it's changed
                while (pair.UserPair!.OwnPermissions != perm)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    Logger.LogTrace("Waiting for permissions change for {data}", userData);
                }
                perm.SetPaused(paused: false);
                await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.LogTrace("Cycle pause timed out or was cancelled for {data}", userData);
            }
        }, nameof(CyclePause));

        return Task.CompletedTask;
    }

    public async Task Pause(UserData userData)
    {
        var pair = _pairManager.GetOnlineUserPairs().Single(p => p.UserPair != null && p.UserData == userData);
        var perm = pair.UserPair!.OwnPermissions;
        perm.SetPaused(paused: true);
        await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
    }

    public Task<ConnectionDto> GetConnectionDto() => GetConnectionDto(true);

    public async Task<ConnectionDto> GetConnectionDto(bool publishConnected)
    {
        var dto = await _snowHub!.InvokeAsync<ConnectionDto>(nameof(GetConnectionDto)).ConfigureAwait(false);
        if (publishConnected) Mediator.Publish(new ConnectedMessage(dto));
        return dto;
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing);

        CancelBackgroundWork();
        try
        {
            StopConnection(ServerState.Disconnected).Wait(TimeSpan.FromSeconds(2));
            _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(2), nameof(ApiController));
        }
        catch (AggregateException)
        {
            // ignored
        }
        DisposeOwnedResources();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing: true);
        CancelBackgroundWork();
        await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
        await _backgroundTasks.StopAsync().ConfigureAwait(false);
        DisposeOwnedResources();
        GC.SuppressFinalize(this);
    }
    
    private async Task ClientHealthCheck(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var hub = _snowHub;
            if (hub == null)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (!ReferenceEquals(hub, _snowHub))
                {
                    continue;
                }

                var healthy = await CheckClientHealth(ct).ConfigureAwait(false);
                if (!healthy || hub.State != HubConnectionState.Connected)
                {
                    Logger.LogWarning("Health check failed, forcing reconnect. ClientHealth: {0} HubConnected: {1}", healthy, hub.State != HubConnectionState.Connected);
                    await ForceResetConnection().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Health check exception, forcing reconnect");
                await ForceResetConnection().ConfigureAwait(false);
            }
        }
    }

    private void DalamudUtilOnLogIn()
    {
        _ = _backgroundTasks.Run(CreateConnections, nameof(CreateConnections));
    }

    private void DalamudUtilOnLogOut()
    {
        _ = _backgroundTasks.Run(() => StopConnection(ServerState.Disconnected), nameof(StopConnection));
        ServerState = ServerState.Offline;
    }

    private void InitializeApiHooks()
    {
        var hub = _snowHub;
        if (hub == null) return;

        if (!_connectionLifecycle.HooksRegistered)
        {
            Logger.LogDebug("Initializing data");
            CallbackRouter.Register(hub, this);
            _connectionLifecycle.MarkCallbacksRegistered();
        }

        _connectionLifecycle.StartHealthLoop(ClientHealthCheck);

        StartSystemInfoPolling();
    }

    private async Task LoadIninitialPairs()
    {
        foreach (var userPair in await UserGetPairedClients().ConfigureAwait(false))
        {
            Logger.LogDebug("Individual Pair: {userPair}", userPair);
            _pairManager.AddUserPair(userPair, addToLastAddedUser: false);
        }
        foreach (var entry in await GroupsGetAll().ConfigureAwait(false))
        {
            Logger.LogDebug("Group: {entry}", entry);
            _pairManager.AddGroup(entry);
        }

        var groups = _pairManager.GroupPairs.Keys.ToList();
        var groupUsers = await Task.WhenAll(groups.Select(async group =>
            await GroupsGetUsersInGroup(group).ConfigureAwait(false))).ConfigureAwait(false);

        foreach (var users in groupUsers)
        {
            foreach (var user in users)
            {
                Logger.LogDebug("Group Pair: {user}", user);
                _pairManager.AddGroupPair(user);
            }
        }
    }

    private async Task LoadOnlinePairs()
    {
        foreach (var entry in await UserGetOnlinePairs().ConfigureAwait(false))
        {
            Logger.LogDebug("Pair online: {pair}", entry);
            _pairManager.MarkPairOnline(entry, sendNotif: false);
        }
    }

    private void SnowHubOnClosed(Exception? arg)
    {
        _connectionLifecycle.StopHealthLoop();
        StopSystemInfoPolling();
        Mediator.Publish(new DisconnectedMessage());
        ServerState = ServerState.Offline;
        if (arg != null)
        {
            Logger.LogWarning(arg, "Connection closed");
        }
        else
        {
            Logger.LogInformation("Connection closed");
        }
    }

    private async Task SnowHubOnReconnected()
    {
        ServerState = ServerState.Reconnecting;
        try
        {
            InitializeApiHooks();
            _connectionLifecycle.MovePhase(ConnectionLifecyclePhase.SyncingState);
            var connectionDto = await GetConnectionDto(publishConnected: false).ConfigureAwait(false);
            _connectionContext = ConnectionContext.From(connectionDto);
            _lastPublishedNews = string.IsNullOrWhiteSpace(connectionDto.News) ? null : connectionDto.News.Trim();
            if (connectionDto.ServerVersion != ISnowHub.ApiVersion)
            {
                await StopConnection(ServerState.VersionMisMatch).ConfigureAwait(false);
                return;
            }
            ServerState = ServerState.Connected;
            TriggerSystemInfoRefresh();
            await LoadIninitialPairs().ConfigureAwait(false);
            await LoadOnlinePairs().ConfigureAwait(false);
            Mediator.Publish(new ConnectedMessage(connectionDto));
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Failure to obtain data after reconnection");
            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
        }
    }

    private void SnowHubOnReconnecting(Exception? arg)
    {
        _doNotNotifyOnNextInfo = true;
        _connectionLifecycle.StopHealthLoop();
        StopSystemInfoPolling();
        ServerState = ServerState.Reconnecting;
        Logger.LogWarning(arg, "Connection closed... Reconnecting");
        Mediator.Publish(new DisconnectedMessage());
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Warning,
            $"Connection interrupted, reconnecting to {_serverManager.CurrentServer.ServerName}")));

    }

    private async Task StopConnection(ServerState state)
    {
        var hadHub = _snowHub is not null;
        await _connectionLifecycle.StopAsync(state, _serverManager.CurrentServer.ServerName, StopSystemInfoPolling).ConfigureAwait(false);
        if (hadHub)
        {
            _connectionContext = ConnectionContext.Empty;
        }
    }

    public async Task ForceResetConnection()
    {
        if (!_connectionLifecycle.HooksRegistered) return;
        Logger.LogInformation("ForceReconnect called");

        try
        {
            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);

            _connectionLifecycle.StopHealthLoop();

            await CreateConnections().ConfigureAwait(false);

            Logger.LogInformation("ForceReconnect completed successfully");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failure during ForceReconnect, disconnecting");
            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
        }
    }

    private void CancelBackgroundWork()
    {
        _backgroundTasks.StopAccepting();
        _connectionLifecycle.CancelConnectionToken();
        _connectionLifecycle.StopHealthLoop();
        _systemInfoPollFlight.Cancel();
    }

    private void DisposeOwnedResources()
    {
        _connectionLifecycle.Dispose();
        _systemInfoPollFlight.Dispose();
    }
}
#pragma warning restore MA0040
