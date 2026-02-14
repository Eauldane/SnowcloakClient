using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    private static readonly TimeSpan SystemInfoPollInterval = TimeSpan.FromMinutes(1);

    private void StartSystemInfoPolling()
    {
        _systemInfoPollTokenSource?.Cancel();
        _systemInfoPollTokenSource?.Dispose();
        _systemInfoPollTokenSource = new CancellationTokenSource();

        _ = Task.Run(() => SystemInfoPollingLoop(_systemInfoPollTokenSource.Token));
    }

    private void StopSystemInfoPolling()
    {
        _systemInfoPollTokenSource?.Cancel();
        _systemInfoPollTokenSource?.Dispose();
        _systemInfoPollTokenSource = null;
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

    private async Task PollSystemInfoOnce(CancellationToken ct)
    {
        if (!IsConnected)
        {
            return;
        }

        var dto = await GetSystemInfo().ConfigureAwait(false);
        if (dto != null)
        {
            SystemInfoDto = dto;
        }
    }

    public async Task<SystemInfoDto> GetSystemInfo()
    {
        var hub = _snowHub;
        if (hub == null || !IsConnected)
        {
            return new SystemInfoDto();
        }

        return await hub.InvokeAsync<SystemInfoDto>(nameof(GetSystemInfo)).ConfigureAwait(false);
    }
}
