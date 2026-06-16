using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ElezenTools;
using ElezenTools.Logging;
using Snowcloak.Configuration;
using Snowcloak.Initialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Snowcloak;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private static readonly TimeSpan HostStopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HostDisposeTimeout = TimeSpan.FromSeconds(10);

    private readonly IHost _host;
    private readonly IPluginLog _pluginLog;
    private int _disposeStarted;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IDataManager gameData,
        IFramework framework, IObjectTable objectTable, IPlayerState playerState, IClientState clientState, ICondition condition, IChatGui chatGui,
        IGameGui gameGui, IDtrBar dtrBar, IToastGui toastGui, IPluginLog pluginLog, ITargetManager targetManager, INotificationManager notificationManager,
        ITextureProvider textureProvider, IContextMenu contextMenu, IGameInteropProvider gameInteropProvider,
        INamePlateGui namePlateGui, IGameConfig gameConfig, IPartyList partyList)
    {
        _pluginLog = pluginLog;
        ElezenInit.Init(pluginInterface, this);
        _host = new HostBuilder()
        .UseContentRoot(pluginInterface.ConfigDirectory.FullName)
        .ConfigureLogging(lb =>
        {
            lb.ClearProviders();
            lb.AddDalamudLogging(pluginLog, sp =>
            {
                var configService = sp.GetService<SnowcloakConfigService>();
                return configService == null ? null : () => configService.Current.LogLevel;
            });
            lb.SetMinimumLevel(LogLevel.Trace);
        })
        .ConfigureServices(collection =>
        {
            collection
                .AddDalamudServices(pluginInterface, commandManager, gameData, framework, objectTable, playerState,
                    clientState, condition, chatGui, gameGui, dtrBar, toastGui, pluginLog, targetManager,
                    notificationManager, textureProvider, contextMenu, gameInteropProvider, namePlateGui, gameConfig,
                    partyList)
                .AddSnowcloakConfiguration(pluginInterface.ConfigDirectory.FullName)
                .AddSnowcloakCore()
                .AddSnowcloakWebApi()
                .AddSnowcloakIpc()
                .AddSnowcloakPlayerData()
                .AddSnowcloakCharaData()
                .AddSnowcloakVenue()
                .AddSnowcloakUi()
                .AddSnowcloakRuntimePlan()
                .AddSnowcloakHostedServices();
        })
        .Build();
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _pluginLog.Error(e, "HostBuilder startup exception");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        var stopCts = new CancellationTokenSource(HostStopTimeout);
        var stopTask = Task.Run(() => _host.StopAsync(stopCts.Token));

        if (!await CompleteWithinAsync(stopTask, HostStopTimeout).ConfigureAwait(false))
        {
            await stopCts.CancelAsync().ConfigureAwait(false);
            _pluginLog.Warning("Timed out stopping Snowcloak host after {Timeout}. Continuing plugin unload; cleanup will finish in the background.", HostStopTimeout);
            _ = FinishTimedOutStopAsync(stopTask, stopCts);
            return;
        }

        stopCts.Dispose();
        await ObserveStopAsync(stopTask).ConfigureAwait(false);
        await DisposeHostAsync().ConfigureAwait(false);
    }

    private async Task FinishTimedOutStopAsync(Task stopTask, CancellationTokenSource stopCts)
    {
        try
        {
            await ObserveStopAsync(stopTask).ConfigureAwait(false);
            await DisposeHostAsync().ConfigureAwait(false);
        }
        finally
        {
            stopCts.Dispose();
        }
    }

    private async Task ObserveStopAsync(Task stopTask)
    {
        try
        {
            await stopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pluginLog.Warning("Snowcloak host stop was cancelled.");
        }
        catch (Exception e)
        {
            _pluginLog.Error(e, "Snowcloak host stop failed.");
        }
    }

    private async Task DisposeHostAsync()
    {
        Task disposeTask;
        if (_host is IAsyncDisposable asyncDisposable)
        {
            disposeTask = asyncDisposable.DisposeAsync().AsTask();
        }
        else
        {
            disposeTask = Task.Run(_host.Dispose);
        }

        if (!await CompleteWithinAsync(disposeTask, HostDisposeTimeout).ConfigureAwait(false))
        {
            _pluginLog.Warning("Timed out disposing Snowcloak host after {Timeout}. Continuing plugin unload.", HostDisposeTimeout);
            _ = ObserveDisposeAsync(disposeTask);
            return;
        }

        await ObserveDisposeAsync(disposeTask).ConfigureAwait(false);
        ElezenInit.Dispose();
    }

    private async Task ObserveDisposeAsync(Task disposeTask)
    {
        try
        {
            await disposeTask.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _pluginLog.Error(e, "Snowcloak host disposal failed.");
        }
    }

    private static async Task<bool> CompleteWithinAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == task;
    }
}
