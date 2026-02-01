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
using Snowcloak.WebAPI.SignalR.Utils;
using System.Reflection;
using Snowcloak.WebAPI.SignalR;

namespace Snowcloak.WebAPI;

#pragma warning disable MA0040
public sealed partial class ApiController : DisposableMediatorSubscriberBase, ISnowHubClient
{
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
    private readonly DalamudUtilService _dalamudUtil;
    private readonly HubFactory _hubFactory;
    private readonly PairManager _pairManager;
    private readonly PairRequestService _pairRequestService;
    private readonly ServerConfigurationManager _serverManager;
    private readonly TokenProvider _tokenProvider;
    private CancellationTokenSource _connectionCancellationTokenSource;
    private ConnectionDto? _connectionDto;
    private bool _doNotNotifyOnNextInfo = false;
    private CancellationTokenSource? _healthCheckTokenSource = new();
    private bool _initialized;
    private HubConnection? _snowHub;
    private ServerState _serverState;
    private CensusUpdateMessage? _lastCensus;

    public ApiController(ILogger<ApiController> logger, HubFactory hubFactory, DalamudUtilService dalamudUtil,
        PairManager pairManager, PairRequestService pairRequestService, ServerConfigurationManager serverManager, SnowMediator mediator,
        TokenProvider tokenProvider) : base(logger, mediator)
    {
        _hubFactory = hubFactory;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _pairRequestService = pairRequestService;
        _serverManager = serverManager;
        _tokenProvider = tokenProvider;
        _connectionCancellationTokenSource = new CancellationTokenSource();

        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => DalamudUtilOnLogIn());
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => DalamudUtilOnLogOut());
        Mediator.Subscribe<HubClosedMessage>(this, (msg) => SnowHubOnClosed(msg.Exception));
        Mediator.Subscribe<HubReconnectedMessage>(this, (msg) => _ = SnowHubOnReconnected());
        Mediator.Subscribe<HubReconnectingMessage>(this, (msg) => SnowHubOnReconnecting(msg.Exception));
        Mediator.Subscribe<CyclePauseMessage>(this, (msg) => _ = CyclePause(msg.UserData));
        Mediator.Subscribe<CensusUpdateMessage>(this, (msg) => _lastCensus = msg);
        Mediator.Subscribe<PauseMessage>(this, (msg) => _ = Pause(msg.UserData));

        ServerState = ServerState.Offline;

        if (_dalamudUtil.IsLoggedIn)
        {
            DalamudUtilOnLogIn();
        }
    }

    public string AuthFailureMessage { get; private set; } = string.Empty;

    public Version CurrentClientVersion => _connectionDto?.CurrentClientVersion ?? new Version(0, 0, 0);

    public string DisplayName => _connectionDto?.User.AliasOrUID ?? string.Empty;
    public string DisplayColour => _connectionDto?.User.DisplayColour ?? string.Empty;
    public bool HasPersistentKey => _connectionDto?.HasPersistentKey ?? false;
    public bool HexAllowed => _connectionDto?.HexAllowed ?? false;
    public string? VanityId => _connectionDto?.User.Alias;
    public bool IsConnected => ServerState == ServerState.Connected;

    public bool IsCurrentVersion => (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0)) >= (_connectionDto?.CurrentClientVersion ?? new Version(0, 0, 0, 0));

    public int OnlineUsers => SystemInfoDto.OnlineUsers;

    public bool ServerAlive => ServerState is ServerState.Connected or ServerState.RateLimited or ServerState.Unauthorized or ServerState.Disconnected;

    public ServerInfo ServerInfo => _connectionDto?.ServerInfo ?? new ServerInfo();

    public ServerState ServerState
    {
        get => _serverState;
        private set
        {
            Logger.LogDebug("New ServerState: {value}, prev ServerState: {_serverState}", value, _serverState);
            _serverState = value;
        }
    }

    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public string UID => _connectionDto?.User.UID ?? string.Empty;

    public async Task<bool> CheckClientHealth()
    {
        var hub = _snowHub;
        if (hub == null || hub.State != HubConnectionState.Connected)
        {
            return false;
        }

        return await hub.InvokeAsync<bool>(nameof(CheckClientHealth)).ConfigureAwait(false);    }

    public async Task CreateConnections()
    {
        Logger.LogDebug("CreateConnections called");

        if (_serverManager.CurrentServer?.FullPause ?? true)
        {
            Logger.LogInformation("Not recreating Connection, paused");
            _connectionDto = null;
            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
            _connectionCancellationTokenSource?.Cancel();
            return;
        }

        var secretKey = _serverManager.GetSecretKey(out bool multi);
        if (multi)
        {
            Logger.LogWarning("Multiple secret keys for current character");
            _connectionDto = null;
            Mediator.Publish(new NotificationMessage("Multiple Identical Characters detected", "Your Service configuration has multiple characters with the same name and world set up. Delete the duplicates in the character management to be able to connect.",
                NotificationType.Error));
            await StopConnection(ServerState.MultiChara).ConfigureAwait(false);
            _connectionCancellationTokenSource?.Cancel();
            return;
        }

        if (secretKey == null)
        {
            Logger.LogWarning("No secret key set for current character");
            _connectionDto = null;
            await StopConnection(ServerState.NoSecretKey).ConfigureAwait(false);
            _connectionCancellationTokenSource?.Cancel();
            return;
        }

        await StopConnection(ServerState.Disconnected).ConfigureAwait(false);

        Logger.LogInformation("Recreating Connection");
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Informational,
            $"Starting Connection to {_serverManager.CurrentServer.ServerName}")));

        _connectionCancellationTokenSource?.Cancel();
        _connectionCancellationTokenSource?.Dispose();
        _connectionCancellationTokenSource = new CancellationTokenSource();
        var token = _connectionCancellationTokenSource.Token;
        while (ServerState is not ServerState.Connected && !token.IsCancellationRequested)
        {
            AuthFailureMessage = string.Empty;

            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
            ServerState = ServerState.Connecting;

            try
            {
                Logger.LogDebug("Building connection");

                try
                {
                    await _tokenProvider.GetOrUpdateToken(token).ConfigureAwait(false);
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

                _snowHub = await _hubFactory.GetOrCreate(token).ConfigureAwait(false);
                InitializeApiHooks();

                await _snowHub.StartAsync(token).ConfigureAwait(false);

                _connectionDto = await GetConnectionDto(publishConnected: false).ConfigureAwait(false);

                await CheckClientHealth().ConfigureAwait(false);

                ServerState = ServerState.Connected;

                var currentClientVer = Assembly.GetExecutingAssembly().GetName().Version!;
 
                if (_connectionDto.ServerVersion != ISnowHub.ApiVersion)
                {
                    if (_connectionDto.CurrentClientVersion > currentClientVer)
                    {
                        Mediator.Publish(new NotificationMessage("Client incompatible",
                            $"Your client is outdated ({currentClientVer.Major}.{currentClientVer.Minor}.{currentClientVer.Build}), current is: " +
                            $"{_connectionDto.CurrentClientVersion.Major}.{_connectionDto.CurrentClientVersion.Minor}.{_connectionDto.CurrentClientVersion.Build}. " +
                            $"This client version is incompatible and will not be able to connect. Please update your Snowcloak client.",
                            NotificationType.Error));
                    }
                    await StopConnection(ServerState.VersionMisMatch).ConfigureAwait(false);
                    return;
                }

                if (_connectionDto.CurrentClientVersion > currentClientVer)
                {
                    Mediator.Publish(new NotificationMessage("Client outdated",
                        $"Your client is outdated ({currentClientVer.Major}.{currentClientVer.Minor}.{currentClientVer.Build}), current is: " +
                        $"{_connectionDto.CurrentClientVersion.Major}.{_connectionDto.CurrentClientVersion.Minor}.{_connectionDto.CurrentClientVersion.Build}. " +
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
                Mediator.Publish(new ConnectedMessage(_connectionDto));
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
                await Task.Delay(TimeSpan.FromSeconds(new Random().Next(5, 20)), token).ConfigureAwait(false);
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

                ServerState = ServerState.Reconnecting;
                Logger.LogInformation("Failed to establish connection, retrying");
                await Task.Delay(TimeSpan.FromSeconds(new Random().Next(5, 20)), token).ConfigureAwait(false);
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
                await Task.Delay(TimeSpan.FromSeconds(new Random().Next(5, 20)), token).ConfigureAwait(false);
            }
        }
    }

    public Task CyclePause(UserData userData)
    {
        CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        _ = Task.Run(async () =>
        {
            var pair = _pairManager.GetOnlineUserPairs().Single(p => p.UserPair != null && p.UserData == userData);
            var perm = pair.UserPair!.OwnPermissions;
            perm.SetPaused(paused: true);
            await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
            // wait until it's changed
            while (pair.UserPair!.OwnPermissions != perm)
            {
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
                Logger.LogTrace("Waiting for permissions change for {data}", userData);
            }
            perm.SetPaused(paused: false);
            await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
        }, cts.Token).ContinueWith((t) => cts.Dispose());

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

    public async Task<ConnectionDto> GetConnectionDto(bool publishConnected = true)
    {
        var dto = await _snowHub!.InvokeAsync<ConnectionDto>(nameof(GetConnectionDto)).ConfigureAwait(false);
        if (publishConnected) Mediator.Publish(new ConnectedMessage(dto));
        return dto;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _healthCheckTokenSource?.Cancel();
        _ = Task.Run(async () => await StopConnection(ServerState.Disconnected).ConfigureAwait(false));
        _connectionCancellationTokenSource?.Cancel();
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

                var healthy = await CheckClientHealth().ConfigureAwait(false);
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
        _ = Task.Run(() => CreateConnections());
    }

    private void DalamudUtilOnLogOut()
    {
        _ = Task.Run(async () => await StopConnection(ServerState.Disconnected).ConfigureAwait(false));
        ServerState = ServerState.Offline;
    }

    private void InitializeApiHooks()
    {
        if (_snowHub == null) return;

        Logger.LogDebug("Initializing data");
        OnDownloadReady((guid) => _ = Client_DownloadReady(guid));
        OnReceiveServerMessage((sev, msg) => _ = Client_ReceiveServerMessage(sev, msg));
        OnUpdateSystemInfo((dto) => _ = Client_UpdateSystemInfo(dto));

        OnUserSendOffline((dto) => _ = Client_UserSendOffline(dto));
        OnUserAddClientPair((dto) => _ = Client_UserAddClientPair(dto));
        OnUserReceiveCharacterData((dto) => _ = Client_UserReceiveCharacterData(dto));
        OnUserRemoveClientPair(dto => _ = Client_UserRemoveClientPair(dto));
        OnUserSendOnline(dto => _ = Client_UserSendOnline(dto));
        OnUserUpdateOtherPairPermissions(dto => _ = Client_UserUpdateOtherPairPermissions(dto));
        OnUserUpdateSelfPairPermissions(dto => _ = Client_UserUpdateSelfPairPermissions(dto));
        OnUserReceiveUploadStatus(dto => _ = Client_UserReceiveUploadStatus(dto));
        OnUserUpdateProfile(dto => _ = Client_UserUpdateProfile(dto));
        OnUserPairingAvailability(dto => _ = Client_UserPairingAvailability(dto));
        OnUserPairingRequest(dto => _ = Client_UserPairingRequest(dto));
        OnRequestPairingAvailabilitySubscription(dto => _ = Client_RequestPairingAvailabilitySubscription(dto));
        OnUserPairingAvailabilityDelta(dto => _ = Client_UserPairingAvailabilityDelta(dto));
        OnGroupChangePermissions((dto) => _ = Client_GroupChangePermissions(dto));
        OnGroupDelete((dto) => _ = Client_GroupDelete(dto));
        OnGroupPairChangeUserInfo((dto) => _ = Client_GroupPairChangeUserInfo(dto));
        OnGroupPairJoined((dto) => _ = Client_GroupPairJoined(dto));
        OnGroupPairLeft((dto) => _ = Client_GroupPairLeft(dto));
        OnGroupSendFullInfo((dto) => _ = Client_GroupSendFullInfo(dto));
        OnGroupSendInfo((dto) => _ = Client_GroupSendInfo(dto));
        OnGroupPairChangePermissions((dto) => _ = Client_GroupPairChangePermissions(dto));

        OnUserChatMsg((dto) => _ = Client_UserChatMsg(dto));
        OnGroupChatMsg((dto) => _ = Client_GroupChatMsg(dto));
        OnGroupChatMemberState((dto) => _ = Client_GroupChatMemberState(dto));
        OnChannelChatMsg((dto) => _ = Client_ChannelChatMsg(dto));
        OnChannelMemberJoined((dto) => _ = Client_ChannelMemberJoined(dto));
        OnChannelMemberLeft((dto) => _ = Client_ChannelMemberLeft(dto));

        OnGposeLobbyJoin((dto) => _ = Client_GposeLobbyJoin(dto));
        OnGposeLobbyLeave((dto) => _ = Client_GposeLobbyLeave(dto));
        OnGposeLobbyPushCharacterData((dto) => _ = Client_GposeLobbyPushCharacterData(dto));
        OnGposeLobbyPushPoseData((dto, data) => _ = Client_GposeLobbyPushPoseData(dto, data));
        OnGposeLobbyPushWorldData((dto, data) => _ = Client_GposeLobbyPushWorldData(dto, data));

        _healthCheckTokenSource?.Cancel();
        _healthCheckTokenSource?.Dispose();
        _healthCheckTokenSource = new CancellationTokenSource();
        _ = ClientHealthCheck(_healthCheckTokenSource.Token);

        _initialized = true;
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
        foreach (var group in _pairManager.GroupPairs.Keys)
        {
            var users = await GroupsGetUsersInGroup(group).ConfigureAwait(false);
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
        _healthCheckTokenSource?.Cancel();
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
            _connectionDto = await GetConnectionDto(publishConnected: false).ConfigureAwait(false);
            if (_connectionDto.ServerVersion != ISnowHub.ApiVersion)
            {
                await StopConnection(ServerState.VersionMisMatch).ConfigureAwait(false);
                return;
            }
            ServerState = ServerState.Connected;
            await LoadIninitialPairs().ConfigureAwait(false);
            await LoadOnlinePairs().ConfigureAwait(false);
            Mediator.Publish(new ConnectedMessage(_connectionDto));
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
        _healthCheckTokenSource?.Cancel();
        ServerState = ServerState.Reconnecting;
        Logger.LogWarning(arg, "Connection closed... Reconnecting");
        Mediator.Publish(new DisconnectedMessage());
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Warning,
            $"Connection interrupted, reconnecting to {_serverManager.CurrentServer.ServerName}")));

    }

    private async Task StopConnection(ServerState state)
    {
        ServerState = ServerState.Disconnecting;

        Logger.LogInformation("Stopping existing connection");
        await _hubFactory.DisposeHubAsync().ConfigureAwait(false);

        if (_snowHub is not null)
        {
            Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Informational,
                $"Stopping existing connection to {_serverManager.CurrentServer.ServerName}")));

            _initialized = false;
            _healthCheckTokenSource?.Cancel();
            Mediator.Publish(new DisconnectedMessage());
            _snowHub = null;
            _connectionDto = null;
        }

        ServerState = state;
    }
    //Because this plugin really likes to bug out with connections, lets "fix" it....
    public async Task ForceResetConnection()
    {
        if (!_initialized) return;
        Logger.LogInformation("ForceReconnect called");

        try
        {
            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);

            // Cancel any ongoing health checks to prevent conflicts
            _healthCheckTokenSource?.Cancel();
            _healthCheckTokenSource?.Dispose();
            _healthCheckTokenSource = null;

            await CreateConnections().ConfigureAwait(false);

            Logger.LogInformation("ForceReconnect completed successfully");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failure during ForceReconnect, disconnecting");
            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
        }
    }
}
#pragma warning restore MA0040
