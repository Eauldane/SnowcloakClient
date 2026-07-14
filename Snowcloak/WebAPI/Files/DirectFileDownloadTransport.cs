using Microsoft.Extensions.Logging;
using Snowcloak.WebAPI.Files.Models;
using System.Globalization;
using System.Net;

namespace Snowcloak.WebAPI.Files;

public sealed partial class DirectFileDownloadTransport : IFileDownloadTransport
{
    private const string DownloadSizeHeaderName = "X-Snowcloak-Download-Size";
    private static readonly TimeSpan RetryAfterFallback = TimeSpan.FromSeconds(5);

    private readonly ILogger<DirectFileDownloadTransport> _logger;
    private readonly FileTransferOrchestrator _orchestrator;

    public DirectFileDownloadTransport(ILogger<DirectFileDownloadTransport> logger, FileTransferOrchestrator orchestrator)
    {
        _logger = logger;
        _orchestrator = orchestrator;
    }

    public async Task<DownloadResponse> OpenAsync(DownloadFileRequest request, Action<DownloadStatus>? onPhase, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestUrl = request.DownloadUri;

        while (true)
        {
            onPhase?.Invoke(DownloadStatus.WaitingForQueue);
            var response = await _orchestrator.SendFileDownloadRequestAsync(requestUrl, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? RetryAfterFallback;
                LogRetryAfter(_logger, requestUrl, retryAfter);
                response.Dispose();
                await Task.Delay(retryAfter, ct).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new FileGrantRejectedException();
            }

            response.EnsureSuccessStatusCode();
            onPhase?.Invoke(DownloadStatus.Downloading);

            var reportedTotal = TryGetReportedDownloadSize(response, out var totalBytes) ? totalBytes : (long?)null;
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new DownloadResponse(response, stream, reportedTotal);
        }
    }

    private static bool TryGetReportedDownloadSize(HttpResponseMessage response, out long totalBytes)
    {
        totalBytes = 0;

        if (response.Headers.TryGetValues(DownloadSizeHeaderName, out var headerValues))
        {
            var headerValue = headerValues.FirstOrDefault();
            if (long.TryParse(headerValue, NumberStyles.None, CultureInfo.InvariantCulture, out totalBytes) && totalBytes > 0)
            {
                return true;
            }
        }

        if (response.Content.Headers.ContentLength is > 0)
        {
            totalBytes = response.Content.Headers.ContentLength.Value;
            return true;
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Download admission deferred by {requestUrl}; retrying after {retryAfter}")]
    private static partial void LogRetryAfter(ILogger logger, Uri requestUrl, TimeSpan retryAfter);
}
