using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using ElezenTools.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.Configuration;
using Snowcloak.Ipc;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI;

namespace Snowcloak.Interop.Ipc;

public sealed class PublicIpcProvider : IHostedService, IMediatorSubscriber, IDisposable
{
    public const int MaxBytesPerPlugin = 4 * 1024;
    public const int MaxTotalBytes = 128 * 1024;
    public const int MaxRegisteredPlugins = MaxTotalBytes / MaxBytesPerPlugin;
    public const int MinPushIntervalMilliseconds = 2000;

    private static readonly TimeSpan MinPushInterval = TimeSpan.FromMilliseconds(MinPushIntervalMilliseconds);
    private static readonly TimeSpan OutOfRangeRefreshInterval = TimeSpan.FromSeconds(10);
    private const int OutOfRangeManifestBatchSize = 64;
    private static readonly Action<ILogger, string, ushort, Exception?> LogMcdfApplyFailure = LoggerMessage.Define<string, ushort>(
        LogLevel.Warning,
        new EventId(1, nameof(LoadMcdfToObjectAsync)),
        "Public IPC failed to apply MCDF {Path} to object index {ObjectIndex}");
    private static readonly Action<ILogger, string, Exception?> LogEventFailure = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(2, nameof(TrySend)),
        "Public IPC event {Label} failed");
    private static readonly Action<ILogger, Exception?> LogOutOfRangeRefreshFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(3, nameof(RefreshOutOfRangeDataAsync)),
        "Public IPC out-of-range extension data refresh failed");

    private readonly HashSet<GameObjectHandler> _activeHandlers = [];
    private readonly ApiController _apiController;
    private readonly ConcurrentDictionary<string, PairApplicationStatus> _applicationStates = new(StringComparer.Ordinal);
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly CharaDataManager _charaDataManager;
    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly Dictionary<string, ExtensionRegistration> _extensions = new(StringComparer.Ordinal);
    private readonly Lock _extensionLock = new();
    private readonly ILogger<PublicIpcProvider> _logger;
    private readonly INotificationManager _notificationManager;
    private readonly IObjectTable _objectTable;
    private readonly PairManager _pairManager;
    private readonly Lock _pairSnapshotLock = new();
    private readonly HashSet<string> _knownPairUids = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingExtensionNotifications = new(StringComparer.Ordinal);
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ConcurrentDictionary<string, OutOfRangeManifestCache> _outOfRangeManifestCache = new(StringComparer.Ordinal);
    private readonly Lock _outOfRangeRefreshLock = new();
    private readonly SemaphoreSlim _outOfRangeRefreshSignal = new(0, 1);
    private readonly ConcurrentDictionary<RemoteExtensionKey, RemoteExtensionState> _remoteData = new();
    private readonly ConcurrentDictionary<RemoteExtensionKey, RemoteExtensionTombstone> _remoteDataTombstones = new();
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly SnowProfileManager _snowProfileManager;
    private readonly List<ICallGateProvider> _functionProviders = [];

    private volatile HashSet<ushort> _handledIndices = [];
    private volatile Dictionary<ushort, PairInfo> _pairsByIndex = [];
    private volatile Dictionary<string, PairInfo> _pairsByUid = new(StringComparer.Ordinal);
    private volatile SnowcloakConnectionState _connectionState;
    private SelfInfo? _self;
    private int _started;

    private ICallGateProvider<object?>? _available;
    private ICallGateProvider<object?>? _unavailable;
    private ICallGateProvider<int, object?>? _connectionStateChanged;

    public SnowMediator Mediator { get; init; }

    public PublicIpcProvider(
        ILogger<PublicIpcProvider> logger,
        IDalamudPluginInterface pluginInterface,
        IObjectTable objectTable,
        INotificationManager notificationManager,
        PairManager pairManager,
        ApiController apiController,
        SnowProfileManager snowProfileManager,
        SnowcloakConfigService configService,
        DalamudUtilService dalamudUtil,
        CharaDataManager charaDataManager,
        SnowMediator mediator)
    {
        _logger = logger;
        _pluginInterface = pluginInterface;
        _objectTable = objectTable;
        _notificationManager = notificationManager;
        _pairManager = pairManager;
        _apiController = apiController;
        _snowProfileManager = snowProfileManager;
        _configService = configService;
        _dalamudUtil = dalamudUtil;
        _charaDataManager = charaDataManager;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        Mediator = mediator;

        Mediator.Subscribe<GameObjectHandlerCreatedMessage>(this, OnGameObjectHandlerCreated);
        Mediator.Subscribe<GameObjectHandlerDestroyedMessage>(this, OnGameObjectHandlerDestroyed);
        Mediator.Subscribe<PairHandlerVisibleMessage>(this, OnPairVisible);
        Mediator.Subscribe<PlayerVisibilityMessage>(this, OnPlayerVisibility);
        Mediator.Subscribe<PairDataReceivedMessage>(this, OnPairDataReceived);
        Mediator.Subscribe<PairApplicationCompletedMessage>(this, OnPairApplicationCompleted);
        Mediator.Subscribe<PairApplicationStateChangedMessage>(this, OnPairApplicationStateChanged);
        Mediator.Subscribe<PauseMessage>(this, OnPairPauseChanged);
        Mediator.Subscribe<PairOnlineStateChangedMessage>(this, OnPairOnlineStateChanged);
        Mediator.Subscribe<ClearProfileDataMessage>(this, OnPairStateChanged);
        Mediator.Subscribe<ClearCharacterProfileDataMessage>(this, OnProfileInvalidated);
        Mediator.Subscribe<ProfileCacheUpdatedMessage>(this, OnProfileCacheUpdated);
        Mediator.Subscribe<ConnectedMessage>(this, OnConnected);
        Mediator.Subscribe<DisconnectedMessage>(this, _ => OnDisconnected());
        Mediator.Subscribe<HubReconnectingMessage>(this, _ => SetConnectionState(SnowcloakConnectionState.Connecting));
        Mediator.Subscribe<HubReconnectedMessage>(this, _ => SetConnectionState(SnowcloakConnectionState.Connected));
        Mediator.Subscribe<PluginChangeMessage>(this, OnPluginChanged);
        Mediator.Subscribe<LocalCharacterDataPushedMessage>(this, OnLocalCharacterDataPushed);
        Mediator.Subscribe<LocalCharacterDataPushFailedMessage>(this, OnLocalCharacterDataPushFailed);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RefreshPairSnapshots();

        RegisterFunction(_pluginInterface.GetIpcProvider<(int Major, int Minor)>(SnowcloakIpcLabels.ApiVersion), () => (1, 4));
        RegisterFunction(_pluginInterface.GetIpcProvider<int>(SnowcloakIpcLabels.GetConnectionState), () => (int)_connectionState);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string?>(SnowcloakIpcLabels.GetSelf), GetSelf);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, List<ushort>>(SnowcloakIpcLabels.GetHandledObjectIndices), GetHandledObjectIndices);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, ushort, bool>(SnowcloakIpcLabels.IsHandled), IsHandled);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, ushort, string?>(SnowcloakIpcLabels.ResolvePair), ResolvePair);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string, string?>(SnowcloakIpcLabels.GetPairByUid), GetPairByUid);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string>(SnowcloakIpcLabels.GetVisiblePairs), GetVisiblePairs);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string>(SnowcloakIpcLabels.GetAllPairs), GetAllPairs);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, int>(SnowcloakIpcLabels.GetVisiblePairCount), GetVisiblePairCount);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string, string?>(SnowcloakIpcLabels.GetProfileSummary), GetProfileSummary);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string, bool>(SnowcloakIpcLabels.OpenProfile), OpenProfile);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string, ushort, Task<bool>>(SnowcloakIpcLabels.LoadMcdfToObject), LoadMcdfToObjectAsync);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string, string?>(SnowcloakIpcLabels.GetApplicationStatus), GetApplicationStatus);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string, string>(SnowcloakIpcLabels.OpenProfileResult), OpenProfileResult);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string, ushort, Task<string>>(SnowcloakIpcLabels.LoadMcdfToObjectResult), LoadMcdfToObjectResultAsync);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string>(SnowcloakIpcLabels.OpenPluginIntegrations), OpenPluginIntegrations);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, ushort, string>(SnowcloakIpcLabels.OpenPairRequest), OpenPairRequest);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, int, string>(SnowcloakIpcLabels.ExtensionRegister), RegisterExtension);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string>(SnowcloakIpcLabels.ExtensionGetGrant), GetExtensionGrant);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, bool>(SnowcloakIpcLabels.ExtensionUnregister), UnregisterExtension);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string?, bool>(SnowcloakIpcLabels.ExtensionSetLocalData), SetLocalExtensionData);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string, string?>(SnowcloakIpcLabels.ExtensionGetRemoteData), GetRemoteExtensionData);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, List<string>>(SnowcloakIpcLabels.ExtensionGetRegisteredKeys), GetRegisteredExtensionKeys);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string?, string>(SnowcloakIpcLabels.ExtensionSetLocalDataResult), SetLocalExtensionDataResult);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string>(SnowcloakIpcLabels.ExtensionUnregisterResult), UnregisterExtensionResult);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string>(SnowcloakIpcLabels.ExtensionGetLocalDataStatus), GetLocalExtensionDataStatus);
        RegisterFunction(_pluginInterface.GetIpcProvider<string, string, string, string?>(SnowcloakIpcLabels.ExtensionGetRemoteDataState), GetRemoteExtensionDataState);

        _available = _pluginInterface.GetIpcProvider<object?>(SnowcloakIpcLabels.Available);
        _unavailable = _pluginInterface.GetIpcProvider<object?>(SnowcloakIpcLabels.Unavailable);
        _connectionStateChanged = _pluginInterface.GetIpcProvider<int, object?>(SnowcloakIpcLabels.ConnectionStateChanged);
        Volatile.Write(ref _started, 1);
        _ = _backgroundTasks.Run(RefreshOutOfRangeLoopAsync, nameof(RefreshOutOfRangeLoopAsync), _runtimeCts.Token);
        TrySend(() => _available.SendMessage(), SnowcloakIpcLabels.Available);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        ClearAllRemoteData();
        TrySend(() => _unavailable?.SendMessage(), SnowcloakIpcLabels.Unavailable);
        foreach (var provider in _functionProviders)
        {
            provider.UnregisterFunc();
        }

        Mediator.UnsubscribeAll(this);
        _backgroundTasks.StopAccepting();
        await _runtimeCts.CancelAsync().ConfigureAwait(false);
        await _backgroundTasks.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _outOfRangeRefreshSignal.Dispose();
        _runtimeCts.Dispose();
    }

    public IReadOnlyDictionary<string, string> GetLocalExtensionDataSnapshot()
    {
        List<string> changed = [];
        Dictionary<string, string> snapshot;
        lock (_extensionLock)
        {
            snapshot = _extensions.Values
                .Where(registration => !string.IsNullOrEmpty(registration.Data)
                                       && HasGrantedPermission(registration, SnowcloakIpcCapability.TransmitExtensionData))
                .OrderBy(static registration => registration.Key, StringComparer.Ordinal)
                .ToDictionary(static registration => registration.Key, static registration => registration.Data!, StringComparer.Ordinal);

            var attemptedAt = DateTimeOffset.UtcNow;
            foreach (var registration in _extensions.Values.Where(registration =>
                         registration.Revision > registration.AcknowledgedRevision
                         && HasGrantedPermission(registration, SnowcloakIpcCapability.TransmitExtensionData)))
            {
                registration.PublicationState = ExtensionPublicationState.Transmitting;
                registration.LastAttemptedAt = attemptedAt;
                changed.Add(registration.Key);
            }
        }

        foreach (var key in changed)
        {
            SendLocalDataStatus(key);
        }
        return snapshot;
    }

    public IReadOnlyList<string> GetRegisteredExtensionKeys()
    {
        lock (_extensionLock)
        {
            return _extensions.Keys.Order(StringComparer.Ordinal).ToArray();
        }
    }

    private List<string> GetRegisteredExtensionKeys(string internalName, string registrationToken)
        => TryGetRegistration(internalName, registrationToken, out _)
            ? GetRegisteredExtensionKeys().ToList()
            : [];

    public IReadOnlyList<ExtensionDiagnostic> GetExtensionDiagnostics(string uid)
    {
        List<ExtensionDiagnostic> result = [];
        lock (_extensionLock)
        {
            foreach (var registration in _extensions.Values.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                _remoteData.TryGetValue(new RemoteExtensionKey(uid, registration.Key), out var remote);
                result.Add(new ExtensionDiagnostic(
                    registration.Key,
                    ByteCount(registration.Data),
                    remote?.Bytes ?? 0,
                    registration.LastChangedAt,
                    remote?.DeliveredAt));
            }
        }

        return result;
    }

    public IReadOnlyList<PluginIntegrationDiagnostic> GetPluginIntegrationDiagnostics()
    {
        var installedPlugins = _pluginInterface.InstalledPlugins
            .GroupBy(static plugin => plugin.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static plugin => plugin.IsLoaded).ThenByDescending(static plugin => plugin.Version).First(),
                StringComparer.OrdinalIgnoreCase);

        var knownKeys = _configService.Current.IpcPluginPermissionRequests.Keys
            .Concat(_configService.Current.IpcPluginPermissions.Keys)
            .ToHashSet(StringComparer.Ordinal);
        List<PluginIntegrationDiagnostic> result = [];
        lock (_extensionLock)
        {
            knownKeys.UnionWith(_extensions.Keys);
            foreach (var key in knownKeys)
            {
                _extensions.TryGetValue(key, out var registration);
                installedPlugins.TryGetValue(key, out var plugin);
                var remoteStates = _remoteData
                    .Where(entry => string.Equals(entry.Key.PluginKey, key, StringComparison.Ordinal))
                    .Select(static entry => entry.Value)
                    .ToArray();
                var requestedPermissions = registration?.RequestedPermissions
                    ?? GetPermissionMask(_configService.Current.IpcPluginPermissionRequests, key);
                var grantedPermissions = GetPermissionMask(_configService.Current.IpcPluginPermissions, key)
                                         & requestedPermissions;
                result.Add(new PluginIntegrationDiagnostic(
                    plugin?.Name ?? key,
                    key,
                    plugin?.Version.ToString(),
                    plugin?.IsLoaded ?? false,
                    registration != null,
                    ByteCount(registration?.Data),
                    remoteStates.Sum(static state => (long)state.Bytes),
                    remoteStates.Length,
                    registration?.TransmittedBytes ?? 0,
                    registration?.ReceivedBytes ?? 0,
                    requestedPermissions,
                    grantedPermissions,
                    registration?.LastChangedAt,
                    registration?.LastTransmittedAt,
                    registration?.LastReceivedAt));
            }
        }

        return result.OrderBy(static entry => entry.PluginName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void SetPluginPermission(string? key, SnowcloakIpcCapability permission, bool enabled)
    {
        if (string.IsNullOrEmpty(key) || !IsSinglePermission(permission) || !IsValidPluginKey(key))
        {
            return;
        }

        if (!enabled && permission == SnowcloakIpcCapability.ReceiveExtensionData)
        {
            ClearRemoteDataForKey(key);
        }
        else if (!enabled && permission == SnowcloakIpcCapability.ReceiveExtensionDataOutOfRange)
        {
            ClearOutOfRangeRemoteDataForKey(key);
        }

        _configService.Update(config =>
        {
            var current = GetPermissionMask(config.IpcPluginPermissions, key);
            var updated = enabled ? current | permission : current & ~permission;
            config.IpcPluginPermissions[key] = (int)updated;
        });

        if (permission == SnowcloakIpcCapability.TransmitExtensionData)
        {
            Mediator.Publish(new ExtensionDataChangedMessage(key));
        }
        else if (enabled && permission == SnowcloakIpcCapability.ReceiveExtensionData)
        {
            ReplayRemoteData(key);
        }

        if (permission is SnowcloakIpcCapability.ReceiveExtensionData
            or SnowcloakIpcCapability.ReceiveExtensionDataOutOfRange)
        {
            RequestOutOfRangeRefresh();
        }

        SendPermissionChanged(key);
    }

    private string RegisterExtension(string internalName, string registrationToken, int requestedPermissionValue)
    {
        var requestedPermissions = (SnowcloakIpcCapability)requestedPermissionValue;
        if ((requestedPermissions & ~SnowcloakIpcCapability.All) != SnowcloakIpcCapability.None)
        {
            return Serialize(CreateGrant(false, "Registration requested an unknown permission.", null,
                SnowcloakIpcCapability.None, SnowcloakIpcCapability.None));
        }

        if (!TryResolvePluginKey(internalName, out var key, out var reason))
        {
            return Serialize(CreateGrant(false, reason, null, requestedPermissions, SnowcloakIpcCapability.None));
        }

        if (!Guid.TryParseExact(registrationToken, "N", out _))
        {
            return Serialize(CreateGrant(false, "Registration requires a valid per-load token.", null,
                requestedPermissions, SnowcloakIpcCapability.None));
        }

        var accepted = false;
        var replay = false;
        lock (_extensionLock)
        {
            if (_extensions.TryGetValue(key, out var existing))
            {
                accepted = string.Equals(existing.RegistrationToken, registrationToken, StringComparison.Ordinal);
                if (accepted)
                {
                    existing.RequestedPermissions = requestedPermissions;
                }
                replay = accepted;
            }
            else if (_extensions.Count >= MaxRegisteredPlugins)
            {
                return Serialize(CreateGrant(false, "All plugin integration slots are in use.", null,
                    requestedPermissions, SnowcloakIpcCapability.None));
            }
            else
            {
                _extensions[key] = new ExtensionRegistration(
                    key,
                    registrationToken,
                    requestedPermissions,
                    new PluginEventProviders(_pluginInterface, key, registrationToken));
                accepted = true;
                replay = true;
            }
        }

        if (!accepted)
        {
            return Serialize(CreateGrant(false, "This plugin identity already owns a slot.", null,
                requestedPermissions, SnowcloakIpcCapability.None));
        }

        var previousRequest = GetPermissionMask(_configService.Current.IpcPluginPermissionRequests, key);
        var newlyRequested = requestedPermissions & ~previousRequest;
        var newlyPending = newlyRequested & ~GetPermissionMask(_configService.Current.IpcPluginPermissions, key);
        _configService.Update(config => config.IpcPluginPermissionRequests[key] = (int)requestedPermissions);

        if (newlyPending != SnowcloakIpcCapability.None)
        {
            ShowPermissionRequestNotification(key, newlyPending);
        }

        if (replay)
        {
            ReconcileRemoteDataPermissions(key);
            ReplayRemoteData(key);
            RequestOutOfRangeRefresh();
        }

        return GetExtensionGrant(internalName, registrationToken);
    }

    private string GetExtensionGrant(string internalName, string registrationToken)
    {
        if (!TryGetRegistration(internalName, registrationToken, out var registration))
        {
            return Serialize(CreateGrant(false, "The plugin is not registered for this load.", null,
                SnowcloakIpcCapability.None, SnowcloakIpcCapability.None));
        }

        var granted = GetGrantedPermissions(registration);
        var reason = granted == registration.RequestedPermissions
            ? null
            : "One or more requested permissions are awaiting approval in Snowcloak settings.";
        return Serialize(CreateGrant(true, reason, registration.Key, registration.RequestedPermissions, granted));
    }

    private bool UnregisterExtension(string internalName, string registrationToken)
        => UnregisterExtensionCore(internalName, registrationToken).Success;

    private string UnregisterExtensionResult(string internalName, string registrationToken)
        => Serialize(UnregisterExtensionCore(internalName, registrationToken));

    private SnowcloakOperationResult UnregisterExtensionCore(string internalName, string registrationToken)
    {
        if (!TryResolvePluginKey(internalName, out var key, out var reason))
        {
            return OperationFailure(SnowcloakOperationCode.InvalidArgument, reason);
        }

        lock (_extensionLock)
        {
            if (!_extensions.TryGetValue(key, out var registration)
                || !string.Equals(registration.RegistrationToken, registrationToken, StringComparison.Ordinal))
            {
                return OperationFailure(SnowcloakOperationCode.NotRegistered, "The plugin is not registered for this load.");
            }
        }

        _pendingExtensionNotifications.TryRemove(key, out _);
        ClearRemoteDataForKey(key);
        lock (_extensionLock)
        {
            _extensions.Remove(key);
        }
        Mediator.Publish(new ExtensionDataChangedMessage(key));
        RequestOutOfRangeRefresh();
        return OperationSuccess();
    }

    private bool SetLocalExtensionData(string internalName, string registrationToken, string? data)
        => SetLocalExtensionDataCore(internalName, registrationToken, data).Success;

    private string SetLocalExtensionDataResult(string internalName, string registrationToken, string? data)
        => Serialize(SetLocalExtensionDataCore(internalName, registrationToken, data));

    private SnowcloakOperationResult SetLocalExtensionDataCore(string internalName, string registrationToken, string? data)
    {
        if (!TryResolvePluginKey(internalName, out var key, out var reason))
        {
            return OperationFailure(SnowcloakOperationCode.InvalidArgument, reason);
        }

        data = string.IsNullOrEmpty(data) ? null : data;
        if (ByteCount(data) > MaxBytesPerPlugin)
        {
            return OperationFailure(SnowcloakOperationCode.LimitExceeded, $"Extension data exceeds the {MaxBytesPerPlugin}-byte slot limit.");
        }

        TimeSpan delay;
        long revision;
        lock (_extensionLock)
        {
            if (!_extensions.TryGetValue(key, out var registration)
                || !string.Equals(registration.RegistrationToken, registrationToken, StringComparison.Ordinal))
            {
                return OperationFailure(SnowcloakOperationCode.NotRegistered, "The plugin is not registered for this load.");
            }

            if (!HasGrantedPermission(registration, SnowcloakIpcCapability.TransmitExtensionData))
            {
                return OperationFailure(SnowcloakOperationCode.PermissionDenied, "Extension data transmission is not permitted.");
            }

            if (string.Equals(registration.Data, data, StringComparison.Ordinal))
            {
                return new SnowcloakOperationResult(true, SnowcloakOperationCode.Unchanged, null, registration.Revision);
            }

            registration.Data = data;
            registration.Revision++;
            revision = registration.Revision;
            registration.LastChangedAt = DateTimeOffset.UtcNow;
            registration.LastFailure = null;
            delay = registration.LastPublishedAt + MinPushInterval - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero)
            {
                registration.LastPublishedAt = DateTimeOffset.UtcNow;
                registration.PublicationState = ExtensionPublicationState.Pending;
            }
            else
            {
                registration.PublicationState = ExtensionPublicationState.Debounced;
            }
        }

        SendLocalDataStatus(key);
        if (delay <= TimeSpan.Zero)
        {
            Mediator.Publish(new ExtensionDataChangedMessage(key));
        }
        else
        {
            ScheduleExtensionNotification(key, delay);
        }

        return OperationSuccess(revision);
    }

    private string? GetRemoteExtensionData(string internalName, string registrationToken, string uid)
    {
        if (!TryResolvePluginKey(internalName, out var key, out _))
        {
            return null;
        }

        var outOfRangeAllowed = false;
        lock (_extensionLock)
        {
            if (!_extensions.TryGetValue(key, out var registration)
                || !string.Equals(registration.RegistrationToken, registrationToken, StringComparison.Ordinal)
                || !HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionData))
            {
                return null;
            }

            outOfRangeAllowed = HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionDataOutOfRange);
        }

        var pair = _pairManager.GetPairByUID(uid);
        if (pair == null || !pair.IsOnline || pair.IsPaused
            || !_remoteData.TryGetValue(new RemoteExtensionKey(uid, key), out var state)
            || state.Availability == RemoteExtensionDataAvailability.AvailableOutOfRange && !outOfRangeAllowed)
        {
            return null;
        }

        return state.Data;
    }

    private string? GetRemoteExtensionDataState(string internalName, string registrationToken, string uid)
    {
        if (!TryResolvePluginKey(internalName, out var key, out _))
        {
            return null;
        }

        var outOfRangeAllowed = false;
        lock (_extensionLock)
        {
            if (!_extensions.TryGetValue(key, out var registration)
                || !string.Equals(registration.RegistrationToken, registrationToken, StringComparison.Ordinal)
                || !HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionData))
            {
                return null;
            }

            outOfRangeAllowed = HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionDataOutOfRange);
        }

        var cacheKey = new RemoteExtensionKey(uid, key);
        var pair = _pairManager.GetPairByUID(uid);
        if (pair == null)
        {
            return null;
        }

        if (!pair.IsOnline)
        {
            _remoteDataTombstones.TryGetValue(cacheKey, out var offlineTombstone);
            return Serialize(new RemoteExtensionDataState(uid, null,
                RemoteExtensionDataAvailability.Offline, null, 0, offlineTombstone?.ClearedAt));
        }

        if (pair.IsPaused)
        {
            _remoteDataTombstones.TryGetValue(cacheKey, out var pausedTombstone);
            return Serialize(new RemoteExtensionDataState(uid, null,
                RemoteExtensionDataAvailability.NotVisible, null, 0, pausedTombstone?.ClearedAt));
        }

        if (_remoteData.TryGetValue(cacheKey, out var remote))
        {
            if (remote.Availability == RemoteExtensionDataAvailability.AvailableOutOfRange && !outOfRangeAllowed)
            {
                return Serialize(new RemoteExtensionDataState(uid, null,
                    RemoteExtensionDataAvailability.NotVisible, null, 0, null));
            }

            return Serialize(ToRemoteDataState(uid, remote));
        }

        if (!pair.IsVisible && !outOfRangeAllowed)
        {
            return Serialize(new RemoteExtensionDataState(uid, null,
                RemoteExtensionDataAvailability.NotVisible, null, 0, null));
        }

        if (_remoteDataTombstones.TryGetValue(cacheKey, out var tombstone))
        {
            return Serialize(new RemoteExtensionDataState(uid, tombstone.ObjectIndex,
                tombstone.Availability, null, 0, tombstone.ClearedAt));
        }

        if (pair.IsVisible)
        {
            return Serialize(new RemoteExtensionDataState(uid, pair.ObjectIndex,
                RemoteExtensionDataAvailability.NoData, null, 0, null));
        }

        return Serialize(new RemoteExtensionDataState(uid, null,
            outOfRangeAllowed ? RemoteExtensionDataAvailability.NoData : RemoteExtensionDataAvailability.NotVisible,
            null, 0, null));
    }

    private void ScheduleExtensionNotification(string key, TimeSpan delay)
    {
        if (!_pendingExtensionNotifications.TryAdd(key, 0))
        {
            return;
        }

        _ = _backgroundTasks.Run(async cancellationToken =>
        {
            long publishedRevision = -1;
            var publish = false;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                lock (_extensionLock)
                {
                    if (_extensions.TryGetValue(key, out var registration))
                    {
                        publishedRevision = registration.Revision;
                        publish = HasGrantedPermission(registration, SnowcloakIpcCapability.TransmitExtensionData);
                        if (publish)
                        {
                            registration.LastPublishedAt = DateTimeOffset.UtcNow;
                            registration.PublicationState = ExtensionPublicationState.Pending;
                        }
                    }
                    else
                    {
                        return;
                    }
                }

                if (publish)
                {
                    SendLocalDataStatus(key);
                    Mediator.Publish(new ExtensionDataChangedMessage(key));
                }
            }
            finally
            {
                _pendingExtensionNotifications.TryRemove(key, out _);
            }

            TimeSpan? nextDelay = null;
            lock (_extensionLock)
            {
                if (_extensions.TryGetValue(key, out var registration) && registration.Revision != publishedRevision)
                {
                    nextDelay = registration.LastPublishedAt + MinPushInterval - DateTimeOffset.UtcNow;
                }
            }

            if (nextDelay.HasValue)
            {
                ScheduleExtensionNotification(key, nextDelay.Value > TimeSpan.Zero ? nextDelay.Value : TimeSpan.Zero);
            }
        }, nameof(ScheduleExtensionNotification), _runtimeCts.Token);
    }

    private bool TryGetRegistration(string internalName, string registrationToken, out ExtensionRegistration registration)
    {
        registration = null!;
        if (!TryResolvePluginKey(internalName, out var key, out _))
        {
            return false;
        }

        lock (_extensionLock)
        {
            if (!_extensions.TryGetValue(key, out var found)
                || !string.Equals(found.RegistrationToken, registrationToken, StringComparison.Ordinal))
            {
                return false;
            }

            registration = found;
            return true;
        }
    }

    private bool HasPermission(string internalName, string registrationToken, SnowcloakIpcCapability permission)
        => TryGetRegistration(internalName, registrationToken, out var registration)
           && HasGrantedPermission(registration, permission);

    private bool HasGrantedPermission(ExtensionRegistration registration, SnowcloakIpcCapability permission)
        => (GetGrantedPermissions(registration) & permission) == permission;

    private SnowcloakIpcCapability GetGrantedPermissions(ExtensionRegistration registration)
        => GetPermissionMask(_configService.Current.IpcPluginPermissions, registration.Key)
           & registration.RequestedPermissions;

    private static SnowcloakIpcCapability GetPermissionMask(Dictionary<string, int> values, string key)
        => values.TryGetValue(key, out var value)
            ? (SnowcloakIpcCapability)value & SnowcloakIpcCapability.All
            : SnowcloakIpcCapability.None;

    private static bool IsSinglePermission(SnowcloakIpcCapability permission)
    {
        var value = (int)permission;
        return permission != SnowcloakIpcCapability.None
               && (permission & ~SnowcloakIpcCapability.All) == SnowcloakIpcCapability.None
               && (value & (value - 1)) == 0;
    }

    private static ExtensionGrant CreateGrant(
        bool accepted,
        string? reason,
        string? pluginKey,
        SnowcloakIpcCapability requestedPermissions,
        SnowcloakIpcCapability grantedPermissions)
        => new(
            accepted,
            reason,
            pluginKey,
            MaxBytesPerPlugin,
            MaxTotalBytes,
            MaxRegisteredPlugins,
            MinPushIntervalMilliseconds,
            requestedPermissions,
            grantedPermissions);

    private void ShowPermissionRequestNotification(string key, SnowcloakIpcCapability permissions)
    {
        var pluginName = GetPluginDisplayName(key);
        var activeNotification = _notificationManager.AddNotification(new Notification
        {
            Title = $"{pluginName} is trying to integrate with Snowcloak",
            Content = $"Requested: {FormatPermissions(permissions)}. Click to review and choose permissions.",
            MinimizedText = $"{pluginName} permission request",
            Type = Dalamud.Interface.ImGuiNotification.NotificationType.Info,
            InitialDuration = TimeSpan.FromMinutes(30),
            ExtensionDurationSinceLastInterest = TimeSpan.FromMinutes(30),
        });
        activeNotification.Click += _ =>
        {
            Mediator.Publish(new OpenPluginIntegrationsSettingsMessage());
            activeNotification.DismissNow();
        };
    }

    private string GetPluginDisplayName(string key)
        => _pluginInterface.InstalledPlugins
            .FirstOrDefault(plugin => string.Equals(plugin.InternalName, key, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? key;

    private static string FormatPermissions(SnowcloakIpcCapability permissions)
    {
        List<string> names = [];
        if ((permissions & SnowcloakIpcCapability.ReadPairData) != SnowcloakIpcCapability.None) names.Add("visible pair data");
        if ((permissions & SnowcloakIpcCapability.ReadProfileData) != SnowcloakIpcCapability.None) names.Add("profile summaries");
        if ((permissions & SnowcloakIpcCapability.OpenProfileWindow) != SnowcloakIpcCapability.None) names.Add("opening profile windows");
        if ((permissions & SnowcloakIpcCapability.ApplyMcdf) != SnowcloakIpcCapability.None) names.Add("GPose MCDF application");
        if ((permissions & SnowcloakIpcCapability.TransmitExtensionData) != SnowcloakIpcCapability.None) names.Add("extension data transmission");
        if ((permissions & SnowcloakIpcCapability.ReceiveExtensionData) != SnowcloakIpcCapability.None) names.Add("extension data reception");
        if ((permissions & SnowcloakIpcCapability.OpenPairRequestWindow) != SnowcloakIpcCapability.None) names.Add("opening pair-request confirmation");
        if ((permissions & SnowcloakIpcCapability.ReadPairDataOutOfRange) != SnowcloakIpcCapability.None) names.Add("out-of-range pair data");
        if ((permissions & SnowcloakIpcCapability.ReceiveExtensionDataOutOfRange) != SnowcloakIpcCapability.None) names.Add("out-of-range extension data reception");
        return string.Join(", ", names);
    }

    private bool TryResolvePluginKey(string internalName, out string key, out string reason)
    {
        key = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(internalName))
        {
            reason = "Registration requires the caller's plugin internal name.";
            return false;
        }

        key = internalName.ToLowerInvariant();
        if (!IsValidPluginKey(key))
        {
            reason = "The plugin InternalName cannot be represented as an extension key.";
            return false;
        }

        if (string.Equals(key, _pluginInterface.InternalName, StringComparison.OrdinalIgnoreCase)
            || !_pluginInterface.InstalledPlugins.Any(plugin => string.Equals(plugin.InternalName, internalName, StringComparison.Ordinal)))
        {
            reason = "The supplied plugin identity is not installed.";
            return false;
        }

        return true;
    }

    private static bool IsValidPluginKey(string key)
    {
        if (key.Length is < 3 or > 64 || !char.IsAsciiLetterOrDigit(key[0]))
        {
            return false;
        }

        return key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private void OnPluginChanged(PluginChangeMessage message)
    {
        if (message.IsLoaded)
        {
            return;
        }

        var key = message.InternalName.ToLowerInvariant();
        var removed = false;
        lock (_extensionLock)
        {
            removed = _extensions.Remove(key);
        }

        if (removed)
        {
            ClearRemoteDataForKey(key);
            Mediator.Publish(new ExtensionDataChangedMessage(key));
            RequestOutOfRangeRefresh();
        }
    }

    private void OnLocalCharacterDataPushed(LocalCharacterDataPushedMessage message)
    {
        var transmittedAt = DateTimeOffset.UtcNow;
        List<string> changed = [];
        lock (_extensionLock)
        {
            foreach (var registration in _extensions.Values)
            {
                if (!MatchesCurrentData(registration, message.ExtensionData))
                {
                    continue;
                }

                if (message.ExtensionData.TryGetValue(registration.Key, out var data))
                {
                    registration.TransmittedBytes += ByteCount(data);
                }
                registration.AcknowledgedRevision = registration.Revision;
                registration.PublicationState = ExtensionPublicationState.Acknowledged;
                registration.LastTransmittedAt = transmittedAt;
                registration.LastAcknowledgedAt = transmittedAt;
                registration.LastFailure = null;
                changed.Add(registration.Key);
            }
        }

        foreach (var key in changed)
        {
            SendLocalDataStatus(key);
        }
    }

    private void OnLocalCharacterDataPushFailed(LocalCharacterDataPushFailedMessage message)
    {
        List<string> changed = [];
        lock (_extensionLock)
        {
            foreach (var registration in _extensions.Values)
            {
                if (!MatchesCurrentData(registration, message.ExtensionData)
                    || registration.Revision <= registration.AcknowledgedRevision)
                {
                    continue;
                }

                registration.PublicationState = ExtensionPublicationState.Failed;
                registration.LastFailure = message.Reason;
                changed.Add(registration.Key);
            }
        }

        foreach (var key in changed)
        {
            SendLocalDataStatus(key);
        }
    }

    private static bool MatchesCurrentData(ExtensionRegistration registration, IReadOnlyDictionary<string, string> data)
        => data.TryGetValue(registration.Key, out var value)
            ? string.Equals(registration.Data, value, StringComparison.Ordinal)
            : string.IsNullOrEmpty(registration.Data);

    private void ReplayRemoteData(string key)
    {
        var enabled = false;
        lock (_extensionLock)
        {
            enabled = _extensions.TryGetValue(key, out var registration)
                      && HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionData);
        }
        foreach (var pair in _pairManager.GetVisiblePairs())
        {
            if (pair.LastReceivedCharacterData == null || pair.IsPaused || pair.ObjectIndex is not ushort objectIndex)
            {
                continue;
            }

            pair.LastReceivedCharacterData.ExtensionData.TryGetValue(key, out var data);
            if (!enabled)
            {
                data = null;
            }
            UpdateRemoteData(key, pair.UserData.UID, objectIndex, data, RemoteExtensionDataAvailability.Available);
        }

        RequestOutOfRangeRefresh();
    }

    private async Task RefreshOutOfRangeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshOutOfRangeDataAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogOutOfRangeRefreshFailure(_logger, ex);
            }

            try
            {
                await _outOfRangeRefreshSignal.WaitAsync(OutOfRangeRefreshInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RefreshOutOfRangeDataAsync(CancellationToken cancellationToken)
    {
        if (_connectionState != SnowcloakConnectionState.Connected)
        {
            return;
        }

        string[] keys;
        lock (_extensionLock)
        {
            keys = _extensions.Values
                .Where(registration => HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionData)
                                       && HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionDataOutOfRange))
                .Select(static registration => registration.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        if (keys.Length == 0)
        {
            _outOfRangeManifestCache.Clear();
            return;
        }

        var pairs = _pairManager.GetPairsSnapshot()
            .Where(static pair => pair.IsOnline && !pair.IsVisible && !pair.IsPaused)
            .ToArray();
        var eligibleUids = pairs.Select(static pair => pair.UserData.UID).ToHashSet(StringComparer.Ordinal);
        foreach (var cachedUid in _outOfRangeManifestCache.Keys.Where(uid => !eligibleUids.Contains(uid)).ToArray())
        {
            _outOfRangeManifestCache.TryRemove(cachedUid, out _);
        }

        if (pairs.Length == 0)
        {
            return;
        }

        var keySignature = string.Join('\n', keys);
        foreach (var batch in pairs.Chunk(OutOfRangeManifestBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchUids = batch.Select(static pair => pair.UserData.UID).ToList();
            var batchUidSet = batchUids.ToHashSet(StringComparer.Ordinal);
            var knownHashes = _outOfRangeManifestCache
                .Where(entry => batchUidSet.Contains(entry.Key)
                                && string.Equals(entry.Value.KeySignature, keySignature, StringComparison.Ordinal))
                .ToDictionary(static entry => entry.Key, static entry => entry.Value.ManifestHash, StringComparer.Ordinal);
            var snapshots = await _apiController.UserGetCurrentExtensionData(
                batchUids, keys.ToList(), knownHashes).ConfigureAwait(false);
            var subscribedKeys = keys.ToHashSet(StringComparer.Ordinal);

            foreach (var snapshot in snapshots)
            {
                if (snapshot == null
                    || !batchUidSet.Contains(snapshot.User.UID)
                    || string.IsNullOrEmpty(snapshot.ManifestHash))
                {
                    continue;
                }

                var received = snapshot.ExtensionData
                    .Where(entry => subscribedKeys.Contains(entry.Key) && ByteCount(entry.Value) <= MaxBytesPerPlugin)
                    .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
                _outOfRangeManifestCache[snapshot.User.UID] = new OutOfRangeManifestCache(
                    snapshot.ManifestHash, keySignature, received, keys);
            }

            foreach (var pair in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_outOfRangeManifestCache.TryGetValue(pair.UserData.UID, out var cache)
                    && string.Equals(cache.KeySignature, keySignature, StringComparison.Ordinal))
                {
                    ApplyOutOfRangeData(pair.UserData.UID, cache.ExtensionData, cache.PluginKeys);
                }
            }
        }
    }

    private void ApplyOutOfRangeData(
        string uid,
        IReadOnlyDictionary<string, string> extensionData,
        IReadOnlyList<string> keys)
    {
        var pair = _pairManager.GetPairByUID(uid);
        if (pair == null || !pair.IsOnline || pair.IsVisible || pair.IsPaused)
        {
            return;
        }

        foreach (var key in keys)
        {
            if (!HasOutOfRangeReceivePermission(key))
            {
                continue;
            }

            extensionData.TryGetValue(key, out var data);
            UpdateRemoteData(key, uid, null, data, RemoteExtensionDataAvailability.AvailableOutOfRange);
        }
    }

    private void TransitionRemoteDataOutOfRange(string uid)
    {
        var pair = _pairManager.GetPairByUID(uid);
        if (pair == null || !pair.IsOnline)
        {
            ClearRemoteDataForPair(uid, RemoteExtensionDataAvailability.Offline);
            return;
        }

        foreach (var entry in _remoteData.Where(entry => string.Equals(entry.Key.Uid, uid, StringComparison.Ordinal)).ToArray())
        {
            if (HasOutOfRangeReceivePermission(entry.Key.PluginKey))
            {
                UpdateRemoteData(entry.Key.PluginKey, uid, null, entry.Value.Data,
                    RemoteExtensionDataAvailability.AvailableOutOfRange);
            }
            else if (_remoteData.TryRemove(entry.Key, out var removed))
            {
                PublishRemoteDataCleared(entry.Key, removed, RemoteExtensionDataAvailability.NotVisible);
            }
        }

        RequestOutOfRangeRefresh();
    }

    private void PromoteOutOfRangeData(string uid, ushort objectIndex)
    {
        if (_outOfRangeManifestCache.TryRemove(uid, out var cache))
        {
            foreach (var key in cache.PluginKeys)
            {
                if (!HasReceivePermission(key))
                {
                    continue;
                }

                cache.ExtensionData.TryGetValue(key, out var data);
                UpdateRemoteData(key, uid, objectIndex, data, RemoteExtensionDataAvailability.Available);
            }
            return;
        }

        foreach (var entry in _remoteData.Where(entry => string.Equals(entry.Key.Uid, uid, StringComparison.Ordinal)
                                                         && entry.Value.Availability == RemoteExtensionDataAvailability.AvailableOutOfRange).ToArray())
        {
            if (HasReceivePermission(entry.Key.PluginKey))
            {
                UpdateRemoteData(entry.Key.PluginKey, uid, objectIndex, entry.Value.Data,
                    RemoteExtensionDataAvailability.Available);
            }
        }
    }

    private bool HasReceivePermission(string key)
    {
        lock (_extensionLock)
        {
            return _extensions.TryGetValue(key, out var registration)
                   && HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionData);
        }
    }

    private bool HasOutOfRangeReceivePermission(string key)
    {
        lock (_extensionLock)
        {
            return _extensions.TryGetValue(key, out var registration)
                   && HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionData)
                   && HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionDataOutOfRange);
        }
    }

    private void ReconcileRemoteDataPermissions(string key)
    {
        if (!HasReceivePermission(key))
        {
            ClearRemoteDataForKey(key);
        }
        else if (!HasOutOfRangeReceivePermission(key))
        {
            ClearOutOfRangeRemoteDataForKey(key);
        }
    }

    private void RequestOutOfRangeRefresh()
    {
        lock (_outOfRangeRefreshLock)
        {
            if (_outOfRangeRefreshSignal.CurrentCount == 0)
            {
                _outOfRangeRefreshSignal.Release();
            }
        }
    }

    private void ApplyRemoteData(Pair pair, CharacterData characterData)
    {
        if (!pair.IsOnline)
        {
            ClearRemoteDataForPair(pair.UserData.UID, RemoteExtensionDataAvailability.Offline);
            return;
        }

        if (pair.IsPaused)
        {
            ClearRemoteDataForPair(pair.UserData.UID, RemoteExtensionDataAvailability.NotVisible);
            return;
        }

        var visible = pair.IsVisible && pair.ObjectIndex is ushort;
        var objectIndex = visible ? pair.ObjectIndex : null;
        var availability = visible
            ? RemoteExtensionDataAvailability.Available
            : RemoteExtensionDataAvailability.AvailableOutOfRange;
        string[] keys;
        lock (_extensionLock)
        {
            keys = _extensions.Keys.ToArray();
        }

        foreach (var key in keys)
        {
            characterData.ExtensionData.TryGetValue(key, out var data);
            lock (_extensionLock)
            {
                if (!_extensions.TryGetValue(key, out var registration)
                    || !HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionData)
                    || !visible && !HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionDataOutOfRange))
                {
                    data = null;
                }
            }

            if (visible || HasOutOfRangeReceivePermission(key))
            {
                UpdateRemoteData(key, pair.UserData.UID, objectIndex, data, availability);
            }
        }
    }

    private void UpdateRemoteData(
        string key,
        string uid,
        ushort? objectIndex,
        string? data,
        RemoteExtensionDataAvailability availability)
    {
        if (ByteCount(data) > MaxBytesPerPlugin)
        {
            data = null;
        }

        var cacheKey = new RemoteExtensionKey(uid, key);
        if (string.IsNullOrEmpty(data))
        {
            if (_remoteData.TryRemove(cacheKey, out var removed))
            {
                var clearedAt = DateTimeOffset.UtcNow;
                var clearedObjectIndex = removed.Availability == RemoteExtensionDataAvailability.Available
                    ? removed.ObjectIndex
                    : null;
                _remoteDataTombstones[cacheKey] = new RemoteExtensionTombstone(
                    clearedObjectIndex,
                    clearedAt,
                    RemoteExtensionDataAvailability.Reverted);
                if (removed.ObjectIndex.HasValue && removed.Availability == RemoteExtensionDataAvailability.Available)
                {
                    SendExtensionData(key, uid, removed.ObjectIndex.Value, null);
                }
                SendRemoteDataState(key, new RemoteExtensionDataState(uid, clearedObjectIndex,
                    RemoteExtensionDataAvailability.Reverted, null, 0, clearedAt));
            }

            return;
        }

        if (_remoteData.TryGetValue(cacheKey, out var existing)
            && existing.ObjectIndex == objectIndex
            && existing.Availability == availability
            && string.Equals(existing.Data, data, StringComparison.Ordinal))
        {
            return;
        }

        var deliveredAt = DateTimeOffset.UtcNow;
        var state = new RemoteExtensionState(data, objectIndex, ByteCount(data), deliveredAt, availability);
        _remoteDataTombstones.TryRemove(cacheKey, out _);
        _remoteData[cacheKey] = state;
        if (existing == null || !string.Equals(existing.Data, data, StringComparison.Ordinal))
        {
            lock (_extensionLock)
            {
                if (_extensions.TryGetValue(key, out var registration))
                {
                    registration.ReceivedBytes += state.Bytes;
                    registration.LastReceivedAt = state.DeliveredAt;
                }
            }
        }

        var existingWasVisible = existing is
        {
            Availability: RemoteExtensionDataAvailability.Available,
            ObjectIndex: not null,
        };
        var stateIsVisible = state.Availability == RemoteExtensionDataAvailability.Available && state.ObjectIndex.HasValue;
        if (existingWasVisible && (!stateIsVisible || existing!.ObjectIndex != state.ObjectIndex))
        {
            SendExtensionData(key, uid, existing!.ObjectIndex!.Value, null);
        }
        if (stateIsVisible && (!existingWasVisible
                               || existing!.ObjectIndex != state.ObjectIndex
                               || !string.Equals(existing.Data, state.Data, StringComparison.Ordinal)))
        {
            SendExtensionData(key, uid, state.ObjectIndex!.Value, state.Data);
        }
        SendRemoteDataState(key, ToRemoteDataState(uid, state));
    }

    private void ClearRemoteDataForPair(
        string uid,
        RemoteExtensionDataAvailability availability = RemoteExtensionDataAvailability.Reverted)
    {
        foreach (var entry in _remoteData.Where(entry => string.Equals(entry.Key.Uid, uid, StringComparison.Ordinal)).ToArray())
        {
            if (_remoteData.TryRemove(entry.Key, out var removed))
            {
                PublishRemoteDataCleared(entry.Key, removed, availability);
            }
        }
    }

    private void ClearRemoteDataForKey(string key)
    {
        foreach (var entry in _remoteData.Where(entry => string.Equals(entry.Key.PluginKey, key, StringComparison.Ordinal)).ToArray())
        {
            if (_remoteData.TryRemove(entry.Key, out var removed))
            {
                PublishRemoteDataCleared(entry.Key, removed, RemoteExtensionDataAvailability.Reverted);
            }
        }

        foreach (var tombstone in _remoteDataTombstones.Keys.Where(entry => string.Equals(entry.PluginKey, key, StringComparison.Ordinal)).ToArray())
        {
            _remoteDataTombstones.TryRemove(tombstone, out _);
        }
    }

    private void ClearAllRemoteData()
    {
        foreach (var entry in _remoteData.ToArray())
        {
            if (_remoteData.TryRemove(entry.Key, out var removed))
            {
                PublishRemoteDataCleared(entry.Key, removed, RemoteExtensionDataAvailability.Offline);
            }
        }
    }

    private void ClearOutOfRangeRemoteDataForKey(string key)
    {
        foreach (var entry in _remoteData.Where(entry => string.Equals(entry.Key.PluginKey, key, StringComparison.Ordinal)
                                                         && entry.Value.Availability == RemoteExtensionDataAvailability.AvailableOutOfRange).ToArray())
        {
            if (_remoteData.TryRemove(entry.Key, out var removed))
            {
                PublishRemoteDataCleared(entry.Key, removed, RemoteExtensionDataAvailability.NotVisible);
            }
        }
    }

    private void PublishRemoteDataCleared(
        RemoteExtensionKey cacheKey,
        RemoteExtensionState removed,
        RemoteExtensionDataAvailability availability)
    {
        var clearedAt = DateTimeOffset.UtcNow;
        var objectIndex = availability == RemoteExtensionDataAvailability.Reverted
            && removed.Availability == RemoteExtensionDataAvailability.Available
            ? removed.ObjectIndex
            : null;
        _remoteDataTombstones[cacheKey] = new RemoteExtensionTombstone(objectIndex, clearedAt, availability);
        if (removed.Availability == RemoteExtensionDataAvailability.Available && removed.ObjectIndex.HasValue)
        {
            SendExtensionData(cacheKey.PluginKey, cacheKey.Uid, removed.ObjectIndex.Value, null);
        }
        SendRemoteDataState(cacheKey.PluginKey, new RemoteExtensionDataState(
            cacheKey.Uid, objectIndex, availability, null, 0, clearedAt));
    }

    private void SendExtensionData(string key, string uid, ushort objectIndex, string? data)
    {
        PluginEventProviders? events = null;
        lock (_extensionLock)
        {
            if (_extensions.TryGetValue(key, out var registration)
                && HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionData))
            {
                events = registration.Events;
            }
        }

        if (events != null)
        {
            TrySend(() => events.ExtensionDataApplied.SendMessage(uid, objectIndex, data), SnowcloakIpcLabels.ExtensionApplyData);
        }
    }

    private void SendRemoteDataState(string key, RemoteExtensionDataState state)
    {
        PluginEventProviders? events = null;
        lock (_extensionLock)
        {
            if (_extensions.TryGetValue(key, out var registration)
                && HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionData)
                && (state.Availability != RemoteExtensionDataAvailability.AvailableOutOfRange
                    || HasGrantedPermission(registration, SnowcloakIpcCapability.ReceiveExtensionDataOutOfRange)))
            {
                events = registration.Events;
            }
        }

        if (events != null)
        {
            TrySend(() => events.ExtensionRemoteDataStateChanged.SendMessage(Serialize(state)),
                SnowcloakIpcLabels.ExtensionRemoteDataStateChanged);
        }
    }

    private static RemoteExtensionDataState ToRemoteDataState(string uid, RemoteExtensionState state)
        => new(uid, state.ObjectIndex, state.Availability, state.Data, state.Bytes, state.DeliveredAt);

    private void SendPermissionChanged(string key)
    {
        PluginEventProviders? events = null;
        lock (_extensionLock)
        {
            if (_extensions.TryGetValue(key, out var registration))
            {
                events = registration.Events;
            }
        }

        if (events != null)
        {
            TrySend(() => events.PermissionsChanged.SendMessage(), "PermissionsChanged");
        }
    }

    private string GetLocalExtensionDataStatus(string internalName, string registrationToken)
    {
        if (!TryGetRegistration(internalName, registrationToken, out var registration)
            || !HasGrantedPermission(registration, SnowcloakIpcCapability.TransmitExtensionData))
        {
            return string.Empty;
        }

        lock (_extensionLock)
        {
            return Serialize(BuildPublicationStatus(registration));
        }
    }

    private void SendLocalDataStatus(string key)
    {
        PluginEventProviders? events = null;
        ExtensionPublicationStatus? status = null;
        lock (_extensionLock)
        {
            if (_extensions.TryGetValue(key, out var registration)
                && HasGrantedPermission(registration, SnowcloakIpcCapability.TransmitExtensionData))
            {
                events = registration.Events;
                status = BuildPublicationStatus(registration);
            }
        }

        if (events != null && status != null)
        {
            TrySend(() => events.ExtensionLocalDataStatusChanged.SendMessage(Serialize(status)),
                SnowcloakIpcLabels.ExtensionLocalDataStatusChanged);
        }
    }

    private static ExtensionPublicationStatus BuildPublicationStatus(ExtensionRegistration registration)
        => new(
            registration.Key,
            registration.Revision,
            registration.AcknowledgedRevision,
            registration.PublicationState,
            ByteCount(registration.Data),
            registration.LastChangedAt,
            registration.LastAttemptedAt,
            registration.LastAcknowledgedAt,
            registration.LastFailure);

    private void SendToPermitted(
        SnowcloakIpcCapability permission,
        Action<PluginEventProviders> send,
        string label)
    {
        PluginEventProviders[] recipients;
        lock (_extensionLock)
        {
            recipients = _extensions.Values
                .Where(registration => HasGrantedPermission(registration, permission))
                .Select(static registration => registration.Events)
                .ToArray();
        }

        foreach (var recipient in recipients)
        {
            TrySend(() => send(recipient), label);
        }
    }

    private void OnGameObjectHandlerCreated(GameObjectHandlerCreatedMessage message)
    {
        if (message.OwnedObject)
        {
            return;
        }

        lock (_activeHandlers)
        {
            _activeHandlers.Add(message.GameObjectHandler);
        }
        RefreshPairSnapshots();
    }

    private void OnGameObjectHandlerDestroyed(GameObjectHandlerDestroyedMessage message)
    {
        if (message.OwnedObject)
        {
            return;
        }

        lock (_activeHandlers)
        {
            _activeHandlers.Remove(message.GameObjectHandler);
        }
        RefreshPairSnapshots();
    }

    private void OnPairVisible(PairHandlerVisibleMessage message)
    {
        var pair = message.Player.Pair;
        if (pair.LastReceivedCharacterData != null)
        {
            UpdateApplicationStatus(pair.UserData.UID, SnowcloakApplicationState.Waiting, null);
        }
        else
        {
            RefreshPairSnapshots();
        }
        if (message.Player.ObjectIndex is ushort objectIndex)
        {
            SendToPermitted(
                SnowcloakIpcCapability.ReadPairData,
                events => events.PairVisibilityChanged.SendMessage(pair.UserData.UID, objectIndex, true),
                SnowcloakIpcLabels.PairVisibilityChanged);

            if (pair.LastReceivedCharacterData != null)
            {
                ApplyRemoteData(pair, pair.LastReceivedCharacterData);
            }
            PromoteOutOfRangeData(pair.UserData.UID, objectIndex);
        }
    }

    private void OnPlayerVisibility(PlayerVisibilityMessage message)
    {
        if (message.IsVisible)
        {
            return;
        }

        var pair = _pairManager.GetPairsSnapshot().FirstOrDefault(candidate => string.Equals(candidate.Ident, message.Ident, StringComparison.Ordinal));
        if (pair == null)
        {
            return;
        }

        var objectIndex = pair.ObjectIndex
                          ?? (_pairsByUid.TryGetValue(pair.UserData.UID, out var snapshot) ? snapshot.ObjectIndex : null);
        TransitionRemoteDataOutOfRange(pair.UserData.UID);
        UpdateApplicationStatus(pair.UserData.UID, SnowcloakApplicationState.Idle, null);
        if (objectIndex.HasValue)
        {
            SendToPermitted(
                SnowcloakIpcCapability.ReadPairData,
                events => events.PairVisibilityChanged.SendMessage(pair.UserData.UID, objectIndex.Value, false),
                SnowcloakIpcLabels.PairVisibilityChanged);
        }
    }

    private void OnPairDataReceived(PairDataReceivedMessage message)
    {
        UpdateApplicationStatus(message.UID, SnowcloakApplicationState.Waiting, null);
        var pair = _pairManager.GetPairByUID(message.UID);
        if (pair != null)
        {
            ApplyRemoteData(pair, message.CharacterData);
        }
    }

    private void OnPairApplicationCompleted(PairApplicationCompletedMessage message)
    {
        UpdateApplicationStatus(message.UID, SnowcloakApplicationState.Applied, null);
        if (_pairsByUid.TryGetValue(message.UID, out var pair)
            && pair.ObjectIndex.HasValue)
        {
            SendToPermitted(
                SnowcloakIpcCapability.ReadPairData,
                events => events.PairDataApplied.SendMessage(message.UID, pair.ObjectIndex.Value),
                SnowcloakIpcLabels.PairDataApplied);
        }
    }

    private void OnPairPauseChanged(PauseMessage message)
    {
        var pair = _pairManager.GetPairByUID(message.UserData.UID);
        if (pair == null || pair.IsPaused)
        {
            ClearRemoteDataForPair(message.UserData.UID, RemoteExtensionDataAvailability.NotVisible);
            UpdateApplicationStatus(message.UserData.UID, SnowcloakApplicationState.Blocked, "Pair synchronisation is paused.");
        }
        else if (pair.IsVisible && pair.LastReceivedCharacterData != null)
        {
            UpdateApplicationStatus(message.UserData.UID, SnowcloakApplicationState.Waiting, null);
            ApplyRemoteData(pair, pair.LastReceivedCharacterData);
        }
        else
        {
            UpdateApplicationStatus(message.UserData.UID, SnowcloakApplicationState.Idle, null);
            RequestOutOfRangeRefresh();
        }
    }

    private void OnPairOnlineStateChanged(PairOnlineStateChangedMessage message)
    {
        RefreshPairSnapshots();
        _outOfRangeManifestCache.TryRemove(message.UID, out _);
        if (!message.IsOnline)
        {
            ClearRemoteDataForPair(message.UID, RemoteExtensionDataAvailability.Offline);
            _applicationStates.TryRemove(message.UID, out _);
            return;
        }

        RequestOutOfRangeRefresh();
    }

    private void OnPairApplicationStateChanged(PairApplicationStateChangedMessage message)
        => UpdateApplicationStatus(message.UID, message.State, message.Reason);

    private void OnPairStateChanged(ClearProfileDataMessage message)
    {
        var oldUids = _pairsByUid.Keys.ToHashSet(StringComparer.Ordinal);
        RefreshPairSnapshots();
        foreach (var removedUid in oldUids.Except(_pairsByUid.Keys, StringComparer.Ordinal))
        {
            ClearRemoteDataForPair(removedUid);
            _outOfRangeManifestCache.TryRemove(removedUid, out _);
            _applicationStates.TryRemove(removedUid, out _);
        }
    }

    private void OnProfileInvalidated(ClearCharacterProfileDataMessage message)
    {
        if (string.IsNullOrEmpty(message.Ident))
        {
            return;
        }

        SendProfileUpdatedForIdent(message.Ident);
    }

    private void OnProfileCacheUpdated(ProfileCacheUpdatedMessage message) => SendProfileUpdatedForIdent(message.Ident);

    private void SendProfileUpdatedForIdent(string ident)
    {
        var pair = _pairManager.GetPairsSnapshot().FirstOrDefault(candidate => string.Equals(candidate.Ident, ident, StringComparison.Ordinal));
        if (pair != null)
        {
            SendToPermitted(
                SnowcloakIpcCapability.ReadProfileData,
                events => events.ProfileUpdated.SendMessage(pair.UserData.UID),
                SnowcloakIpcLabels.ProfileUpdated);
        }
    }

    private void OnConnected(ConnectedMessage message)
    {
        _self = new SelfInfo(message.Connection.User.UID, message.Connection.User.Alias, message.Connection.User.DisplayColour);
        SetConnectionState(SnowcloakConnectionState.Connected);
    }

    private void OnDisconnected()
    {
        ClearAllRemoteData();
        _remoteDataTombstones.Clear();
        _outOfRangeManifestCache.Clear();
        _self = null;
        _applicationStates.Clear();
        _pairsByUid = new(StringComparer.Ordinal);
        _pairsByIndex = [];
        SetConnectionState(SnowcloakConnectionState.Disconnected);
    }

    private void SetConnectionState(SnowcloakConnectionState state)
    {
        var previous = _connectionState;
        _connectionState = state;
        if (previous != state && Volatile.Read(ref _started) != 0)
        {
            TrySend(() => _connectionStateChanged?.SendMessage((int)state), SnowcloakIpcLabels.ConnectionStateChanged);
        }
        if (state == SnowcloakConnectionState.Connected)
        {
            RefreshPairSnapshots();
            RequestOutOfRangeRefresh();
        }
    }

    private void RefreshPairSnapshots()
    {
        HashSet<ushort> previousHandled;
        Dictionary<string, PairInfo> previousByUid;
        Dictionary<string, PairInfo> byUid = new(StringComparer.Ordinal);
        Dictionary<ushort, PairInfo> byIndex = [];
        HashSet<ushort> handled = [];
        string[] added;
        string[] removed;

        lock (_pairSnapshotLock)
        {
            previousHandled = _handledIndices;
            previousByUid = _pairsByUid;

            lock (_activeHandlers)
            {
                foreach (var index in _activeHandlers.Select(static handler => handler.ObjectIndex).OfType<ushort>())
                {
                    handled.Add(index);
                }
            }

            foreach (var pair in _pairManager.GetPairsSnapshot())
            {
                var snapshot = BuildPairInfo(pair);
                byUid[snapshot.Uid] = snapshot;
                if (snapshot.IsVisible && snapshot.ObjectIndex.HasValue)
                {
                    byIndex[snapshot.ObjectIndex.Value] = snapshot;
                    handled.Add(snapshot.ObjectIndex.Value);
                }
            }

            added = byUid.Keys.Except(_knownPairUids, StringComparer.Ordinal).ToArray();
            removed = _knownPairUids.Except(byUid.Keys, StringComparer.Ordinal).ToArray();
            _knownPairUids.Clear();
            _knownPairUids.UnionWith(byUid.Keys);

            _pairsByUid = byUid;
            _pairsByIndex = byIndex;
            _handledIndices = handled;
        }

        if (Volatile.Read(ref _started) == 0)
        {
            return;
        }

        if (!previousHandled.SetEquals(handled))
        {
            SendToPermitted(
                SnowcloakIpcCapability.ReadPairData,
                static events => events.HandledCharactersChanged.SendMessage(),
                SnowcloakIpcLabels.HandledCharactersChanged);
        }

        foreach (var uid in added)
        {
            var snapshot = byUid[uid];
            SendPairSnapshotEvent(
                null,
                snapshot,
                events => events.PairAdded.SendMessage(uid),
                SnowcloakIpcLabels.PairAdded);
        }

        foreach (var uid in removed)
        {
            previousByUid.TryGetValue(uid, out var previous);
            SendPairSnapshotEvent(
                previous,
                null,
                events => events.PairRemoved.SendMessage(uid),
                SnowcloakIpcLabels.PairRemoved);
        }

        foreach (var (uid, snapshot) in byUid)
        {
            if (previousByUid.TryGetValue(uid, out var previous) && previous != snapshot)
            {
                SendPairSnapshotEvent(
                    previous,
                    snapshot,
                    events => events.PairStateChanged.SendMessage(uid),
                    SnowcloakIpcLabels.PairStateChanged);
            }
        }
    }

    private void SendPairSnapshotEvent(
        PairInfo? previous,
        PairInfo? current,
        Action<PluginEventProviders> send,
        string label)
    {
        var permission = previous?.IsVisible == true || current?.IsVisible == true
            ? SnowcloakIpcCapability.ReadPairData
            : SnowcloakIpcCapability.ReadPairData | SnowcloakIpcCapability.ReadPairDataOutOfRange;
        SendToPermitted(permission, send, label);
    }

    private PairInfo BuildPairInfo(Pair pair)
    {
        var application = GetEffectiveApplicationStatus(pair);
        var applied = pair.IsVisible && !pair.IsPaused && application.State == SnowcloakApplicationState.Applied;

        return new PairInfo(
            pair.UserData.UID,
            pair.UserData.Alias,
            pair.UserData.HexString,
            pair.IsVisible,
            pair.IsVisible ? pair.ObjectIndex : null,
            pair.IsPaused,
            applied,
            pair.UserPair != null)
        {
            IsOnline = pair.IsOnline,
            ApplicationState = application.State,
            ApplicationStateChangedAtUtc = application.ChangedAtUtc,
        };
    }

    private PairApplicationStatus GetEffectiveApplicationStatus(Pair pair)
    {
        if (pair.IsPaused)
        {
            return new PairApplicationStatus(pair.UserData.UID, SnowcloakApplicationState.Blocked,
                "Pair synchronisation is paused.", DateTimeOffset.UnixEpoch);
        }

        if (!pair.IsVisible)
        {
            return new PairApplicationStatus(pair.UserData.UID, SnowcloakApplicationState.Idle, null, DateTimeOffset.UnixEpoch);
        }

        if (_applicationStates.TryGetValue(pair.UserData.UID, out var status))
        {
            return status;
        }

        return new PairApplicationStatus(pair.UserData.UID,
            pair.LastReceivedCharacterData == null ? SnowcloakApplicationState.Idle : SnowcloakApplicationState.Waiting,
            null,
            DateTimeOffset.UnixEpoch);
    }

    private void UpdateApplicationStatus(string uid, SnowcloakApplicationState state, string? reason)
    {
        if (_applicationStates.TryGetValue(uid, out var current)
            && current.State == state
            && string.Equals(current.Reason, reason, StringComparison.Ordinal))
        {
            return;
        }

        var status = new PairApplicationStatus(uid, state, reason, DateTimeOffset.UtcNow);
        _applicationStates[uid] = status;

        SendToPermitted(
            SnowcloakIpcCapability.ReadPairData,
            events => events.ApplicationStatusChanged.SendMessage(Serialize(status)),
            SnowcloakIpcLabels.ApplicationStatusChanged);
        RefreshPairSnapshots();
    }

    private string? GetSelf(string internalName, string registrationToken)
        => HasPermission(internalName, registrationToken, SnowcloakIpcCapability.ReadPairData) && _self != null
            ? Serialize(_self)
            : null;

    private string? GetApplicationStatus(string internalName, string registrationToken, string uid)
    {
        var pair = _pairManager.GetPairByUID(uid);
        var permission = pair?.IsVisible == true
            ? SnowcloakIpcCapability.ReadPairData
            : SnowcloakIpcCapability.ReadPairData | SnowcloakIpcCapability.ReadPairDataOutOfRange;
        return pair == null || !HasPermission(internalName, registrationToken, permission)
            ? null
            : Serialize(GetEffectiveApplicationStatus(pair));
    }

    private List<ushort> GetHandledObjectIndices(string internalName, string registrationToken)
        => HasPermission(internalName, registrationToken, SnowcloakIpcCapability.ReadPairData)
            ? _handledIndices.Order().ToList()
            : [];

    private bool IsHandled(string internalName, string registrationToken, ushort objectIndex)
        => HasPermission(internalName, registrationToken, SnowcloakIpcCapability.ReadPairData)
           && _handledIndices.Contains(objectIndex);

    private string? ResolvePair(string internalName, string registrationToken, ushort objectIndex)
    {
        if (!HasPermission(internalName, registrationToken, SnowcloakIpcCapability.ReadPairData)
            || !_pairsByIndex.TryGetValue(objectIndex, out var pair))
        {
            return null;
        }
        return Serialize(pair);
    }

    private string? GetPairByUid(string internalName, string registrationToken, string uid)
    {
        if (!_pairsByUid.TryGetValue(uid, out var pair))
        {
            return null;
        }

        var permission = pair.IsVisible
            ? SnowcloakIpcCapability.ReadPairData
            : SnowcloakIpcCapability.ReadPairData | SnowcloakIpcCapability.ReadPairDataOutOfRange;
        if (!HasPermission(internalName, registrationToken, permission))
        {
            return null;
        }
        return Serialize(pair);
    }

    private string GetVisiblePairs(string internalName, string registrationToken)
    {
        if (!HasPermission(internalName, registrationToken, SnowcloakIpcCapability.ReadPairData))
        {
            return "[]";
        }

        var pairs = _pairsByUid.Values.Where(static pair => pair.IsVisible).OrderBy(static pair => pair.Uid, StringComparer.Ordinal).ToList();
        return JsonSerializer.Serialize(pairs, SnowcloakIpcJsonContext.Default.ListPairInfo);
    }

    private string GetAllPairs(string internalName, string registrationToken)
    {
        var permission = SnowcloakIpcCapability.ReadPairData | SnowcloakIpcCapability.ReadPairDataOutOfRange;
        if (!HasPermission(internalName, registrationToken, permission))
        {
            return "[]";
        }

        var pairs = _pairsByUid.Values.OrderBy(static pair => pair.Uid, StringComparer.Ordinal).ToList();
        return JsonSerializer.Serialize(pairs, SnowcloakIpcJsonContext.Default.ListPairInfo);
    }

    private int GetVisiblePairCount(string internalName, string registrationToken)
        => HasPermission(internalName, registrationToken, SnowcloakIpcCapability.ReadPairData)
            ? _pairsByUid.Values.Count(static pair => pair.IsVisible)
            : 0;

    private string? GetProfileSummary(string internalName, string registrationToken, string uid)
    {
        if (!HasPermission(internalName, registrationToken, SnowcloakIpcCapability.ReadProfileData))
        {
            return null;
        }

        var pair = _pairManager.GetPairByUID(uid);
        if (pair == null || string.IsNullOrEmpty(pair.Ident))
        {
            return null;
        }

        var cached = _snowProfileManager.GetCachedProfile(pair.Ident);
        if (cached != null)
        {
            return Serialize(new ProfileSummary(
                uid,
                cached.Description,
                (int)cached.Document.ContentRating,
                cached.Tags.Select(static tag => $"{tag.Type}:{tag.Value}").ToArray(),
                cached.Revision,
                cached.UpdatedAtUtc,
                cached.Disabled));
        }

        var summary = _snowProfileManager.GetSummary(pair.Ident);
        return summary == null
            ? null
            : Serialize(new ProfileSummary(
                uid,
                summary.Tagline,
                (int)summary.ContentRating,
                summary.Tags.Select(static tag => $"{tag.Type}:{tag.Value}").ToArray(),
                0,
                null,
                false));
    }

    private bool OpenProfile(string internalName, string registrationToken, string uid)
        => OpenProfileCore(internalName, registrationToken, uid).Success;

    private string OpenProfileResult(string internalName, string registrationToken, string uid)
        => Serialize(OpenProfileCore(internalName, registrationToken, uid));

    private SnowcloakOperationResult OpenProfileCore(string internalName, string registrationToken, string uid)
    {
        if (!HasPermission(internalName, registrationToken, SnowcloakIpcCapability.OpenProfileWindow))
        {
            return OperationFailure(SnowcloakOperationCode.PermissionDenied, "Opening profile windows is not permitted.");
        }

        var pair = _pairManager.GetPairByUID(uid);
        if (pair == null)
        {
            return OperationFailure(SnowcloakOperationCode.NotFound, "The requested pair is not known.");
        }

        Mediator.Publish(new ProfileOpenStandaloneMessage(pair.UserData, pair, FallbackName: pair.PlayerName));
        return OperationSuccess();
    }

    private async Task<bool> LoadMcdfToObjectAsync(string internalName, string registrationToken, string path, ushort objectIndex)
        => (await LoadMcdfToObjectCoreAsync(internalName, registrationToken, path, objectIndex).ConfigureAwait(false)).Success;

    private async Task<string> LoadMcdfToObjectResultAsync(string internalName, string registrationToken, string path, ushort objectIndex)
        => Serialize(await LoadMcdfToObjectCoreAsync(internalName, registrationToken, path, objectIndex).ConfigureAwait(false));

    private async Task<SnowcloakOperationResult> LoadMcdfToObjectCoreAsync(string internalName, string registrationToken, string path, ushort objectIndex)
    {
        if (!HasPermission(internalName, registrationToken, SnowcloakIpcCapability.ApplyMcdf))
        {
            return OperationFailure(SnowcloakOperationCode.PermissionDenied, "MCDF application is not permitted.");
        }
        if (!_dalamudUtil.IsInGpose)
        {
            return OperationFailure(SnowcloakOperationCode.InvalidState, "MCDF application is only available in GPose.");
        }
        if (objectIndex < 200)
        {
            return OperationFailure(SnowcloakOperationCode.InvalidArgument, "MCDF application requires a GPose-range object index.");
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return OperationFailure(SnowcloakOperationCode.NotFound, "The MCDF file does not exist.");
        }

        try
        {
            var target = await Service.RunOnFrameworkAsync(() => _objectTable[objectIndex]).ConfigureAwait(false);
            if (target is not ICharacter)
            {
                return OperationFailure(SnowcloakOperationCode.NotFound, "The target character is not available.");
            }

            _charaDataManager.LoadMcdf(path);
            var header = _charaDataManager.LoadedMcdfHeader;
            if (header == null)
            {
                return OperationFailure(SnowcloakOperationCode.InvalidArgument, "The MCDF file could not be read.");
            }
            await header.ConfigureAwait(false);
            _charaDataManager.McdfApplyToTarget(target.Name.TextValue);
            var application = _charaDataManager.McdfApplication.Task;
            if (application == null)
            {
                return OperationFailure(SnowcloakOperationCode.Failed, "Snowcloak did not start the MCDF application.");
            }
            await application.ConfigureAwait(false);
            return application.IsCompletedSuccessfully
                ? OperationSuccess()
                : OperationFailure(SnowcloakOperationCode.Failed, "The MCDF application did not complete successfully.");
        }
        catch (Exception ex)
        {
            LogMcdfApplyFailure(_logger, path, objectIndex, ex);
            return OperationFailure(SnowcloakOperationCode.Failed, "The MCDF application failed.");
        }
    }

    private string OpenPluginIntegrations(string internalName, string registrationToken)
    {
        if (!TryGetRegistration(internalName, registrationToken, out _))
        {
            return Serialize(OperationFailure(SnowcloakOperationCode.NotRegistered, "The plugin is not registered for this load."));
        }

        Mediator.Publish(new OpenPluginIntegrationsSettingsMessage());
        return Serialize(OperationSuccess());
    }

    private string OpenPairRequest(string internalName, string registrationToken, ushort objectIndex)
    {
        if (!TryGetRegistration(internalName, registrationToken, out var registration))
        {
            return Serialize(OperationFailure(SnowcloakOperationCode.NotRegistered, "The plugin is not registered for this load."));
        }

        if (!HasGrantedPermission(registration, SnowcloakIpcCapability.OpenPairRequestWindow))
        {
            return Serialize(OperationFailure(SnowcloakOperationCode.PermissionDenied, "Opening pair-request confirmation is not permitted."));
        }

        var target = _objectTable[objectIndex] as IPlayerCharacter;
        if (target == null || target.HomeWorld.RowId == 0 || string.IsNullOrWhiteSpace(target.Name.TextValue))
        {
            return Serialize(OperationFailure(SnowcloakOperationCode.NotFound, "The target player is not available."));
        }

        var ident = (target.Name.TextValue + target.HomeWorld.RowId.ToString(CultureInfo.InvariantCulture)).GetHash256();
        if (_pairManager.GetPairsSnapshot().Any(pair => string.Equals(pair.Ident, ident, StringComparison.Ordinal)))
        {
            return Serialize(OperationFailure(SnowcloakOperationCode.InvalidState, "The target is already a Snowcloak pair."));
        }

        Mediator.Publish(new OpenPairRequestConfirmationMessage(
            ident,
            target.Name.TextValue,
            GetPluginDisplayName(registration.Key)));
        return Serialize(OperationSuccess());
    }

    private void RegisterFunction<TReturn>(ICallGateProvider<TReturn> provider, Func<TReturn> function)
    {
        provider.RegisterFunc(function);
        _functionProviders.Add(provider);
    }

    private void RegisterFunction<T1, T2, TReturn>(ICallGateProvider<T1, T2, TReturn> provider, Func<T1, T2, TReturn> function)
    {
        provider.RegisterFunc(function);
        _functionProviders.Add(provider);
    }

    private void RegisterFunction<T1, T2, T3, TReturn>(ICallGateProvider<T1, T2, T3, TReturn> provider, Func<T1, T2, T3, TReturn> function)
    {
        provider.RegisterFunc(function);
        _functionProviders.Add(provider);
    }

    private void RegisterFunction<T1, T2, T3, T4, TReturn>(
        ICallGateProvider<T1, T2, T3, T4, TReturn> provider,
        Func<T1, T2, T3, T4, TReturn> function)
    {
        provider.RegisterFunc(function);
        _functionProviders.Add(provider);
    }

    private void TrySend(Action action, string label)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogEventFailure(_logger, label, ex);
        }
    }

    private static int ByteCount(string? data) => string.IsNullOrEmpty(data) ? 0 : Encoding.UTF8.GetByteCount(data);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SnowcloakIpcJsonContext.Default.Options);

    private static SnowcloakOperationResult OperationSuccess(long? revision = null)
        => new(true, SnowcloakOperationCode.Success, null, revision);

    private static SnowcloakOperationResult OperationFailure(SnowcloakOperationCode code, string reason)
        => new(false, code, reason);

    private sealed class ExtensionRegistration(
        string key,
        string registrationToken,
        SnowcloakIpcCapability requestedPermissions,
        PluginEventProviders events)
    {
        public string Key { get; } = key;
        public string RegistrationToken { get; } = registrationToken;
        public PluginEventProviders Events { get; } = events;
        public SnowcloakIpcCapability RequestedPermissions { get; set; } = requestedPermissions;
        public string? Data { get; set; }
        public long Revision { get; set; }
        public long AcknowledgedRevision { get; set; }
        public ExtensionPublicationState PublicationState { get; set; }
        public long TransmittedBytes { get; set; }
        public long ReceivedBytes { get; set; }
        public DateTimeOffset? LastChangedAt { get; set; }
        public DateTimeOffset LastPublishedAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset? LastTransmittedAt { get; set; }
        public DateTimeOffset? LastReceivedAt { get; set; }
        public DateTimeOffset? LastAttemptedAt { get; set; }
        public DateTimeOffset? LastAcknowledgedAt { get; set; }
        public string? LastFailure { get; set; }
    }

    private sealed class PluginEventProviders
    {
        public PluginEventProviders(IDalamudPluginInterface pluginInterface, string pluginKey, string registrationToken)
        {
            string Event(string name) => SnowcloakIpcLabels.PluginEvent(pluginKey, registrationToken, name);

            HandledCharactersChanged = pluginInterface.GetIpcProvider<object?>(Event("HandledCharactersChanged"));
            ProfileUpdated = pluginInterface.GetIpcProvider<string, object?>(Event("ProfileUpdated"));
            PairVisibilityChanged = pluginInterface.GetIpcProvider<string, ushort, bool, object?>(Event("PairVisibilityChanged"));
            PairDataApplied = pluginInterface.GetIpcProvider<string, ushort, object?>(Event("PairDataApplied"));
            PairAdded = pluginInterface.GetIpcProvider<string, object?>(Event("PairAdded"));
            PairRemoved = pluginInterface.GetIpcProvider<string, object?>(Event("PairRemoved"));
            PairStateChanged = pluginInterface.GetIpcProvider<string, object?>(Event("PairStateChanged"));
            ApplicationStatusChanged = pluginInterface.GetIpcProvider<string, object?>(Event("ApplicationStatusChanged"));
            ExtensionDataApplied = pluginInterface.GetIpcProvider<string, ushort, string?, object?>(Event("ExtensionDataApplied"));
            ExtensionLocalDataStatusChanged = pluginInterface.GetIpcProvider<string, object?>(Event("ExtensionLocalDataStatusChanged"));
            ExtensionRemoteDataStateChanged = pluginInterface.GetIpcProvider<string, object?>(Event("ExtensionRemoteDataStateChanged"));
            PermissionsChanged = pluginInterface.GetIpcProvider<object?>(Event("PermissionsChanged"));
        }

        public ICallGateProvider<object?> HandledCharactersChanged { get; }
        public ICallGateProvider<string, object?> ProfileUpdated { get; }
        public ICallGateProvider<string, ushort, bool, object?> PairVisibilityChanged { get; }
        public ICallGateProvider<string, ushort, object?> PairDataApplied { get; }
        public ICallGateProvider<string, object?> PairAdded { get; }
        public ICallGateProvider<string, object?> PairRemoved { get; }
        public ICallGateProvider<string, object?> PairStateChanged { get; }
        public ICallGateProvider<string, object?> ApplicationStatusChanged { get; }
        public ICallGateProvider<string, ushort, string?, object?> ExtensionDataApplied { get; }
        public ICallGateProvider<string, object?> ExtensionLocalDataStatusChanged { get; }
        public ICallGateProvider<string, object?> ExtensionRemoteDataStateChanged { get; }
        public ICallGateProvider<object?> PermissionsChanged { get; }
    }

    private readonly record struct RemoteExtensionKey(string Uid, string PluginKey);

    private sealed record RemoteExtensionState(
        string Data,
        ushort? ObjectIndex,
        int Bytes,
        DateTimeOffset DeliveredAt,
        RemoteExtensionDataAvailability Availability);

    private sealed record RemoteExtensionTombstone(
        ushort? ObjectIndex,
        DateTimeOffset ClearedAt,
        RemoteExtensionDataAvailability Availability);

    private sealed record OutOfRangeManifestCache(
        string ManifestHash,
        string KeySignature,
        IReadOnlyDictionary<string, string> ExtensionData,
        IReadOnlyList<string> PluginKeys);
}

public sealed record ExtensionDiagnostic(
    string PluginKey,
    int SentBytes,
    int ReceivedBytes,
    DateTimeOffset? LastLocalChangeAt,
    DateTimeOffset? LastReceivedAt);

public sealed record PluginIntegrationDiagnostic(
    string PluginName,
    string PluginKey,
    string? Version,
    bool IsLoaded,
    bool IsRegistered,
    int OutgoingBytes,
    long IncomingBytes,
    int IncomingPairs,
    long TransmittedBytes,
    long ReceivedBytes,
    SnowcloakIpcCapability RequestedPermissions,
    SnowcloakIpcCapability GrantedPermissions,
    DateTimeOffset? LastLocalChangeAt,
    DateTimeOffset? LastTransmittedAt,
    DateTimeOffset? LastReceivedAt);
