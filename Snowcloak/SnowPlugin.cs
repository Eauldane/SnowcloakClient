using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Initialization;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using System.Reflection;
using ElezenTools;

namespace Snowcloak;

public sealed class SnowPlugin : MediatorSubscriberBase, IHostedService, IDisposable, IAsyncDisposable
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly Lock _launchTaskLock = new();
    private readonly SemaphoreSlim _runtimeScopeLock = new(1, 1);
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly ServerRegistry _serverConfigurationManager;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly RuntimeServicePlan _runtimeServicePlan;
    private readonly CancellationTokenSource _launchCts = new();
    private AsyncServiceScope? _runtimeServiceScope;
    private Task? _launchTask = null;
    private int _disposed;

    public SnowPlugin(ILogger<SnowPlugin> logger, SnowcloakConfigService snowcloakConfigService,
        ServerRegistry serverConfigurationManager,
        DalamudUtilService dalamudUtil,
        IServiceScopeFactory serviceScopeFactory, RuntimeServicePlan runtimeServicePlan, SnowMediator mediator) : base(logger, mediator)
    {
        _snowcloakConfigService = snowcloakConfigService;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtil = dalamudUtil;
        _serviceScopeFactory = serviceScopeFactory;
        _runtimeServicePlan = runtimeServicePlan;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version!;
        Logger.LogInformation("Launching {name} {major}.{minor}.{build}.{rev}", "Snowcloak Sync", version.Major, version.Minor, version.Build, version.Revision);
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(SnowPlugin), Services.Events.EventSeverity.Informational,
            $"Starting Snowcloak Sync {version.Major}.{version.Minor}.{version.Build}.{version.Revision}")));

        Mediator.Subscribe<SwitchToMainUiMessage>(this, _ => StartLaunchTask());
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => DalamudUtilOnLogIn());
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => DalamudUtilOnLogOut());

        Mediator.StartQueueProcessing();

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeAll();
        await _launchCts.CancelAsync().ConfigureAwait(false);

        if (_launchTask != null)
        {
            try
            {
                await _launchTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_launchCts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                // expected during shutdown
            }
        }

        await DisposeRuntimeScopeAsync().ConfigureAwait(false);

        Logger.LogDebug("Halting SnowSnowPlugin");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisposeRuntimeScopeAsync().GetAwaiter().GetResult();
        _launchCts.Dispose();
        _runtimeScopeLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await DisposeRuntimeScopeAsync().ConfigureAwait(false);
        _launchCts.Dispose();
        _runtimeScopeLock.Dispose();
    }

    private void DalamudUtilOnLogIn()
    {
        Logger?.LogDebug("Client login");
        StartLaunchTask();
    }

    private void DalamudUtilOnLogOut()
    {
        Logger?.LogDebug("Client logout");

        DisposeRuntimeScopeInBackground();
    }

    private void StartLaunchTask()
    {
        if (_launchCts.IsCancellationRequested)
        {
            return;
        }

        lock (_launchTaskLock)
        {
            if (_launchTask == null || _launchTask.IsCompleted)
            {
                _launchTask = Task.Run(() => LaunchManagersAsync(_launchCts.Token), _launchCts.Token);
            }
        }
    }

    private async Task LaunchManagersAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Launch is event-driven: it is triggered by DalamudLoginMessage (raised once the
            // local player is present and valid) and by SwitchToMainUiMessage (raised from the
            // configured intro flow, while in-world). This single guard covers the rare case
            // where a trigger arrives before presence is observable; if so we bail and the next
            // DalamudLoginMessage re-triggers the launch. No polling loop.
            if (!await _dalamudUtil.GetIsPlayerPresentAsync().ConfigureAwait(false))
            {
                Logger?.LogDebug("Launch deferred: player not present yet");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            Logger?.LogDebug("Launching Managers");

            await CreateRuntimeScopeAsync(cancellationToken).ConfigureAwait(false);

#if !DEBUG
            if (_snowcloakConfigService.Current.LogLevel != LogLevel.Information)
            {
                Mediator.Publish(new NotificationMessage("Abnormal Log Level",
                    $"Your log level is set to '{_snowcloakConfigService.Current.LogLevel}' which is not recommended for normal usage. Set it to '{LogLevel.Information}' in \"Snowcloak Settings -> Debug\" unless instructed otherwise.",
                    Snowcloak.Configuration.Models.NotificationType.Error, TimeSpan.FromSeconds(15000)));
            }
#endif
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger?.LogDebug("Launch of managers cancelled");
        }
        catch (Exception ex)
        {
            Logger?.LogCritical(ex, "Error during launch of managers");
            Mediator.Publish(new NotificationMessage("Error during launch of internal management services",
                ex.ToString(),
                Configuration.Models.NotificationType.Error));
        }
    }

    private async Task CreateRuntimeScopeAsync(CancellationToken cancellationToken)
    {
        await _runtimeScopeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeRuntimeScopeCoreAsync().ConfigureAwait(false);

            var scope = _serviceScopeFactory.CreateAsyncScope();
            var scopeCommitted = false;
            try
            {
                var provider = scope.ServiceProvider;

                // Base services must exist even on the intro screen.
                ActivateRuntimeServices(provider, _runtimeServicePlan.BaseServices);

                if (!_snowcloakConfigService.Current.HasValidSetup() || !_serverConfigurationManager.HasValidConfig())
                {
                    _runtimeServiceScope = scope;
                    scopeCommitted = true;
                    Mediator.Publish(new SwitchToIntroUiMessage());
                    return;
                }

                // Full runtime pipeline, only once setup and server config are valid.
                ActivateRuntimeServices(provider, _runtimeServicePlan.ConfiguredServices);

                _runtimeServiceScope = scope;
                scopeCommitted = true;
            }
            finally
            {
                if (!scopeCommitted)
                {
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _runtimeScopeLock.Release();
        }
    }

    private static void ActivateRuntimeServices(IServiceProvider provider, IReadOnlyList<Type> serviceTypes)
    {
        // Resolving each service is its activation: constructors set up mediator
        // subscriptions, command handlers, and other side effects.
        foreach (var serviceType in serviceTypes)
        {
            provider.GetRequiredService(serviceType);
        }
    }

    private void DisposeRuntimeScopeInBackground()
    {
        var disposeTask = DisposeRuntimeScopeAsync();
        if (!disposeTask.IsCompletedSuccessfully)
        {
            _ = ObserveRuntimeScopeDisposalAsync(disposeTask);
        }
    }

    private async Task ObserveRuntimeScopeDisposalAsync(Task disposeTask)
    {
        try
        {
            await disposeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error disposing runtime service scope");
        }
    }

    private async Task DisposeRuntimeScopeAsync()
    {
        await _runtimeScopeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeRuntimeScopeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _runtimeScopeLock.Release();
        }
    }

    private async ValueTask DisposeRuntimeScopeCoreAsync()
    {
        if (_runtimeServiceScope is not { } scope)
        {
            return;
        }

        _runtimeServiceScope = null;
        await scope.DisposeAsync().ConfigureAwait(false);
    }
}
