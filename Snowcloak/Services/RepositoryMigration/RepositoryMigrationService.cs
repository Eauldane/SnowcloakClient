using System.Diagnostics;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Snowcloak.Services.RepositoryMigration;

public sealed class RepositoryMigrationService(ILogger<RepositoryMigrationService> logger,
    IFramework framework, IChatGui chat) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var token = _lifetime.Token;
        _task = Task.Run(() => MigrateAsync(token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_task != null) await _task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _lifetime.Dispose();

    private async Task MigrateAsync(CancellationToken token)
    {
        var committed = false;
        try
        {
            var host = await framework.RunOnFrameworkThread(DalamudRepositoryAccess.Open).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            while (!await framework.RunOnFrameworkThread(() => host.Ready).ConfigureAwait(false))
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(30))
                {
                    Report("Dalamud is still loading repositories, waiting until next plugin load to update.");
                    return;
                }
                await Task.Delay(250, token).ConfigureAwait(false);
            }

            var needed = await framework.RunOnFrameworkThread(() => host.Needed).ConfigureAwait(false);
            if (!needed)
            {
                Report("Repository migration is already complete.");
                return;
            }
            var description = await framework.RunOnFrameworkThread(host.Describe).ConfigureAwait(false);
            logger.LogInformation("Repository migration adapter ready: {Description}", description);
            token.ThrowIfCancellationRequested();
            var changed = await framework.RunOnFrameworkThread(() => host.Migrate(token)).ConfigureAwait(false);
            committed = true;
            Report($"Repository list updated; {changed} installed plugin source(s) transferred. Refreshing");
            await ReloadAsync(host, token).ConfigureAwait(false);
            var available = await framework.RunOnFrameworkThread(() => host.DestinationAvailable).ConfigureAwait(false);
            Report(available
                ? "Repository migration complete."
                : "Repository settings updated, but still waiting for Dalamud to reresh.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Report(committed
                ? "Repository settings updated; refreshing stopped during plugin shutdown."
                : "Repository migration stopped; incomplete work can be retried on the next load.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Repository migration or refresh did not complete");
            Report(committed
                ? "Repository settings updated; refreshing stopped during plugin shutdown."
                : "Repository migration stopped; incomplete work can be retried on the next load.");
        }
    }

    private async Task ReloadAsync(DalamudRepositoryAccess host, CancellationToken token)
    {
        var reload = await framework.RunOnFrameworkThread(() => new ReloadOperation(host.Reload())).ConfigureAwait(false);
        await reload.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
    }

    private void Report(string message) => logger.LogInformation("{RepositoryMigrationStatus}", message);

    private sealed record ReloadOperation(Task Task);
}
