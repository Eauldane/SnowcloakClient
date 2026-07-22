using Microsoft.Extensions.Logging;
using Snowcloak.Infrastructure.Transfers;
using Snowcloak.WebAPI.Files.Models;
using System.Globalization;
using System.Net;

namespace Snowcloak.WebAPI.Files;

public sealed partial class DirectFileDownloadTransport : IFileDownloadTransport
{
    private const string DownloadSizeHeaderName = "X-Snowcloak-Download-Size";
    private const string QueuePositionHeaderName = "X-Snowcloak-Queue-Position";
    private static readonly TimeSpan RetryAfterFallback = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MissingLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TransientLifetime = TimeSpan.FromSeconds(30);

    private readonly ILogger<DirectFileDownloadTransport> _logger;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly FileDownloadNegativeCache _negativeCache;

    public DirectFileDownloadTransport(ILogger<DirectFileDownloadTransport> logger,
        FileTransferOrchestrator orchestrator, FileDownloadNegativeCache negativeCache)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _negativeCache = negativeCache;
    }

    public async Task<DownloadResponse> OpenAsync(DownloadFileRequest request, Action<DownloadStatus>? onPhase,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_negativeCache.TryGet(request.Hash, out var cached))
        {
            throw new FileDownloadUnavailableException(cached);
        }

        if (request.Purpose == FileDownloadPurpose.OptionalPrefetch
            && !_orchestrator.TryReserveOptionalPrefetch(request.ExpectedBytes))
        {
            throw new FileDownloadUnavailableException(_negativeCache.Record(request.Hash,
                FileDownloadNegativeReason.PrefetchBudgetExceeded, TimeSpan.FromMinutes(5),
                "Optional file prefetch is paused because its byte budget has been reached."));
        }

        onPhase?.Invoke(DownloadStatus.WaitingForQueue);
        HttpResponseMessage response;
        while (true)
        {
            try
            {
                response = await _orchestrator.SendFileDownloadRequestAsync(request.DownloadUri, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new FileDownloadUnavailableException(_negativeCache.Record(request.Hash,
                    FileDownloadNegativeReason.TemporarilyUnavailable, TransientLifetime,
                    "The file service could not be reached. Snowcloak will retry shortly."), ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new FileDownloadUnavailableException(_negativeCache.Record(request.Hash,
                    FileDownloadNegativeReason.TemporarilyUnavailable, TransientLifetime,
                    "The file service did not respond in time. Snowcloak will retry shortly."), ex);
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests) break;
            var retryAfter = GetRetryAfter(response);
            var queuePosition = GetQueuePosition(response);
            LogRetryAfter(_logger, request.DownloadUri, retryAfter, queuePosition);
            response.Dispose();
            await Task.Delay(retryAfter, ct).ConfigureAwait(false);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new FileGrantRejectedException();
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            response.Dispose();
            throw new FileDownloadUnavailableException(_negativeCache.Record(request.Hash,
                FileDownloadNegativeReason.Missing, MissingLifetime,
                "The requested file is not available on the server. Snowcloak will check again later."));
        }

        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            var retryAfter = GetRetryAfter(response, TransientLifetime);
            response.Dispose();
            throw new FileDownloadUnavailableException(_negativeCache.Record(request.Hash,
                FileDownloadNegativeReason.TemporarilyUnavailable, retryAfter,
                $"The file service is temporarily unavailable. Snowcloak will retry after {FormatDelay(retryAfter)}."));
        }

        response.EnsureSuccessStatusCode();
        _negativeCache.Clear(request.Hash);
        onPhase?.Invoke(DownloadStatus.Downloading);
        var reportedTotal = TryGetReportedDownloadSize(response, out var totalBytes) ? totalBytes : (long?)null;
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new DownloadResponse(response, stream, reportedTotal);
    }

    private static TimeSpan GetRetryAfter(HttpResponseMessage response, TimeSpan? fallback = null)
    {
        var delay = response.Headers.RetryAfter?.Delta;
        if (delay == null && response.Headers.RetryAfter?.Date is { } retryAt)
        {
            delay = retryAt - DateTimeOffset.UtcNow;
        }
        return delay is { } value && value > TimeSpan.Zero ? value : fallback ?? RetryAfterFallback;
    }

    private static int? GetQueuePosition(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues(QueuePositionHeaderName, out var values)
               && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var position)
               && position > 0
            ? position
            : null;
    }

    private static string FormatDelay(TimeSpan delay)
    {
        return delay >= TimeSpan.FromMinutes(1)
            ? $"{Math.Ceiling(delay.TotalMinutes).ToString(CultureInfo.InvariantCulture)} minute(s)"
            : $"{Math.Max(1, Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture)} second(s)";
    }

    private static bool TryGetReportedDownloadSize(HttpResponseMessage response, out long totalBytes)
    {
        totalBytes = 0;
        if (response.Headers.TryGetValues(DownloadSizeHeaderName, out var headerValues)
            && long.TryParse(headerValues.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out totalBytes)
            && totalBytes > 0)
        {
            return true;
        }

        if (response.Content.Headers.ContentLength is > 0)
        {
            totalBytes = response.Content.Headers.ContentLength.Value;
            return true;
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Download admission deferred by {RequestUrl}; retry after {RetryAfter}; queue position {QueuePosition}")]
    private static partial void LogRetryAfter(ILogger logger, Uri requestUrl, TimeSpan retryAfter, int? queuePosition);
}
