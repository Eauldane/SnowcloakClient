using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    private static readonly TimeSpan SystemInfoPollInterval = TimeSpan.FromSeconds(15);

    private void StartSystemInfoPolling()
    {
        var scope = _systemInfoPollFlight.Begin();
        var token = scope.Token;
        _systemInfoPollToken = token;

        _ = _backgroundTasks.Run(async () =>
        {
            using (scope)
            {
                await SystemInfoPollingLoop(token).ConfigureAwait(false);
            }
        }, nameof(SystemInfoPollingLoop));
    }

    private void StopSystemInfoPolling()
    {
        _systemInfoPollFlight.Cancel();
        _systemInfoPollToken = new CancellationToken(canceled: true);
    }

    private async Task SystemInfoPollingLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollSystemInfoOnce(ct).ConfigureAwait(false);
                await Task.Delay(SystemInfoPollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "System info poll failed");
            }
        }
    }

    private void TriggerSystemInfoRefresh()
    {
        _ = _backgroundTasks.Run(async () =>
        {
            try
            {
                await PollSystemInfoOnce(_systemInfoPollToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Initial system info refresh failed");
            }
        }, nameof(TriggerSystemInfoRefresh));
    }

    private async Task PollSystemInfoOnce(CancellationToken ct)
    {
        if (!IsConnected)
        {
            return;
        }

        var dto = await GetSystemInfo(ct).ConfigureAwait(false);
        if (dto != null)
        {
            await Client_UpdateSystemInfo(dto).ConfigureAwait(false);
        }
    }

    public Task<SystemInfoDto> GetSystemInfo() => GetSystemInfo(CancellationToken.None);

    private async Task<SystemInfoDto> GetSystemInfo(CancellationToken cancellationToken)
    {
        var hub = _snowHub;
        if (hub == null || !IsConnected)
        {
            return new SystemInfoDto();
        }

        return await hub.InvokeAsync<SystemInfoDto>(nameof(GetSystemInfo), cancellationToken).ConfigureAwait(false);
    }
}
