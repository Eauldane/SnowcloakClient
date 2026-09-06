using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ElezenTools;
using ElezenTools.Logging;
using System.Diagnostics;
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
    private static readonly TimeSpan DependencyLoadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DependencyLoadPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly string[] RequiredPluginInternalNames = ["Penumbra", "Glamourer"];

    private readonly IHost _host;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _pluginLog;
    private int _disposeStarted;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IDataManager gameData,
        IFramework framework, IObjectTable objectTable, IPlayerState playerState, IClientState clientState, ICondition condition, IChatGui chatGui,
        IGameGui gameGui, IDtrBar dtrBar, IToastGui toastGui, IPluginLog pluginLog, ITargetManager targetManager, INotificationManager notificationManager,
        ITextureProvider textureProvider, IContextMenu contextMenu, IGameInteropProvider gameInteropProvider,
        INamePlateGui namePlateGui, IGameConfig gameConfig, IPartyList partyList)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        _pluginInterface = pluginInterface;
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
            await WaitForRequiredPluginsAsync(cancellationToken).ConfigureAwait(false);
            await _host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _pluginLog.Error(e, "HostBuilder startup exception");
            throw;
        }
    }

    private async Task WaitForRequiredPluginsAsync(CancellationToken cancellationToken)
    {
        var pending = GetPendingInstalledRequiredPlugins();
        if (pending.Length == 0)
        {
            return;
        }

        _pluginLog.Information("Waiting up to {Timeout} for required plugins to finish loading: {Plugins}",
            DependencyLoadTimeout, string.Join(", ", pending));

        var started = Stopwatch.GetTimestamp();
        while (pending.Length > 0)
        {
            var remaining = DependencyLoadTimeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < DependencyLoadPollInterval ? remaining : DependencyLoadPollInterval,
                cancellationToken).ConfigureAwait(false);
            pending = GetPendingInstalledRequiredPlugins();
        }

        if (pending.Length == 0)
        {
            _pluginLog.Information("Required plugins finished loading after {Elapsed}",
                Stopwatch.GetElapsedTime(started));
            return;
        }

        _pluginLog.Warning("Timed out after {Timeout} waiting for required plugins: {Plugins}. Starting Snowcloak in degraded mode.",
            DependencyLoadTimeout, string.Join(", ", pending));
    }

    private string[] GetPendingInstalledRequiredPlugins()
    {
        try
        {
            return _pluginInterface.InstalledPlugins
                .Where(plugin => RequiredPluginInternalNames.Contains(plugin.InternalName, StringComparer.OrdinalIgnoreCase))
                .GroupBy(plugin => plugin.InternalName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.All(plugin => !plugin.IsLoaded))
                .Select(group => group.Key)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (InvalidOperationException ex)
        {
            _pluginLog.Warning(ex, "Could not inspect required plugin load state; continuing Snowcloak startup.");
            return [];
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
