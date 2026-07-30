using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Core.Async;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.WebAPI.SignalR;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;

namespace Snowcloak.WebAPI.Files;

public partial class FileTransferOrchestrator : DisposableMediatorSubscriberBase
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UntrackedRequestTimeout = TimeSpan.FromSeconds(100);
    
    private const int MaxTransientAttempts = 4;
    private const int MaxRateLimitAttempts = 4;
    private static readonly TimeSpan TransientRetryBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TransientRetryMaxDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RateLimitRetryFallback = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RateLimitRetryMaximum = TimeSpan.FromMinutes(1);
    private static readonly HashSet<HttpStatusCode> TransientStatusCodes =
    [
        HttpStatusCode.RequestTimeout,      // 408
        HttpStatusCode.BadGateway,          // 502
        HttpStatusCode.ServiceUnavailable,  // 503
        HttpStatusCode.GatewayTimeout,      // 504
    ];

    private readonly HttpClient _httpClient;
    private readonly SocketsHttpHandler _httpHandler;
    private readonly SnowcloakConfigService _snowcloakConfig;
    private readonly TokenProvider _tokenProvider;
    private readonly DownloadSlotGate _downloadSlots;
    private readonly DownloadSlotGate _decompressionSlots;
    private readonly Lock _forbiddenLock = new();
    private readonly List<ForbiddenTransfer> _forbiddenTransfers = [];
    private readonly HashSet<string> _allowedFileDownloadHosts = new(StringComparer.OrdinalIgnoreCase);
    private long _optionalPrefetchBytesRemaining;

    public FileTransferOrchestrator(ILogger<FileTransferOrchestrator> logger, SnowcloakConfigService snowcloakConfig,
        SnowMediator mediator, TokenProvider tokenProvider) : base(logger, mediator)
    {
        ArgumentNullException.ThrowIfNull(snowcloakConfig);
        _snowcloakConfig = snowcloakConfig;
        _tokenProvider = tokenProvider;
        _httpHandler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        _httpClient = new HttpClient(_httpHandler, false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Snowcloak", version));

        ProcessorThreadCount = Environment.ProcessorCount;
        DecompressionWorkerLimit = Math.Max(1, ProcessorThreadCount - 2);
        _downloadSlots = new DownloadSlotGate(snowcloakConfig.Current.ParallelDownloads);
        _decompressionSlots = new DownloadSlotGate(DecompressionWorkerLimit);
        ResetOptionalPrefetchBudget();
        
        Mediator.Subscribe<FileServerInfoReceivedMessage>(this, (msg) =>
        {
            FilesCdnUri = msg.Connection.ServerInfo.FileServerAddress;
            SetAllowedDownloadHosts(msg.Connection.ServerInfo.AllowedFileDownloadHosts);
        });

        Mediator.Subscribe<ConnectedMessage>(this, (msg) =>
        {
            FilesCdnUri = msg.Connection.ServerInfo.FileServerAddress;
            SetAllowedDownloadHosts(msg.Connection.ServerInfo.AllowedFileDownloadHosts);
            ResetOptionalPrefetchBudget();
        });

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            FilesCdnUri = null;
            lock (_allowedFileDownloadHosts)
            {
                _allowedFileDownloadHosts.Clear();
            }
        });
    }

    public Uri? FilesCdnUri { private set; get; }
    public bool IsInitialized => FilesCdnUri != null;
    public int ProcessorThreadCount { get; }
    public int DecompressionWorkerLimit { get; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
            _httpHandler.Dispose();
        }

        base.Dispose(disposing);
    }

    public string PreferredDownloadTypeQueryValue()
    {
        return _snowcloakConfig.Current.PreferredDownloadType.ToString();
    }

    public void AddForbiddenTransfer(ForbiddenTransfer transfer)
    {
        lock (_forbiddenLock)
        {
            if (!_forbiddenTransfers.Exists(f => string.Equals(f.Hash, transfer.Hash, StringComparison.Ordinal)))
            {
                _forbiddenTransfers.Add(transfer);
            }
        }
    }

    public bool IsForbidden(string hash)
    {
        lock (_forbiddenLock)
        {
            return _forbiddenTransfers.Exists(f => string.Equals(f.Hash, hash, StringComparison.Ordinal));
        }
    }

    public IReadOnlyList<ForbiddenTransfer> ForbiddenTransfers
    {
        get
        {
            lock (_forbiddenLock)
            {
                return [.. _forbiddenTransfers];
            }
        }
    }

    public async Task WaitForDownloadSlotAsync(CancellationToken token)
    {
        _downloadSlots.UpdateLimit(_snowcloakConfig.Current.ParallelDownloads);
        await _downloadSlots.WaitAsync(token).ConfigureAwait(false);
        Mediator.Publish(new DownloadLimitChangedMessage());
    }

    public void ReleaseDownloadSlot()
    {
        _downloadSlots.Release();
        Mediator.Publish(new DownloadLimitChangedMessage());
    }

    public async Task WaitForDecompressionSlotAsync(CancellationToken token)
    {
        await _decompressionSlots.WaitAsync(token).ConfigureAwait(false);
    }

    public void ReleaseDecompressionSlot()
    {
        _decompressionSlots.Release();
    }

    public long DownloadLimitPerSlot()
    {
        var limit = _snowcloakConfig.Current.DownloadSpeedLimitInBytes;
        if (limit <= 0) return 0;
        limit = _snowcloakConfig.Current.DownloadSpeedType switch
        {
            Configuration.Models.DownloadSpeeds.Bps => limit,
            Configuration.Models.DownloadSpeeds.KBps => limit * 1024,
            Configuration.Models.DownloadSpeeds.MBps => limit * 1024 * 1024,
            _ => limit,
        };
        var activeSlots = Math.Max(1, _downloadSlots.InUse);
        var dividedLimit = limit / activeSlots;
        if (dividedLimit < 0)
        {
            LogInvalidDownloadLimit(Logger, dividedLimit, activeSlots, limit, _downloadSlots.Limit);
            return long.MaxValue;
        }
        return Math.Clamp(dividedLimit, 1, long.MaxValue);
    }

    public async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, Uri uri,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead)
    {
        return await SendRequestInternalAsync(() => new HttpRequestMessage(method, uri), ct, httpCompletionOption, allowRetry: true).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestAsync<T>(HttpMethod method, Uri uri, T content, CancellationToken ct,
        HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead) where T : class
    {
        if (content is ByteArrayContent byteContent)
        {
            return await SendRequestInternalAsync(() => new HttpRequestMessage(method, uri) { Content = byteContent }, ct, httpCompletionOption, allowRetry: false).ConfigureAwait(false);
        }

        return await SendRequestInternalAsync(() => new HttpRequestMessage(method, uri) { Content = JsonContent.Create(content) }, ct, httpCompletionOption, allowRetry: true).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestStreamAsync(HttpMethod method, Uri uri, ProgressableStreamContent content, CancellationToken ct)
    {
        return await SendRequestInternalAsync(() => new HttpRequestMessage(method, uri) { Content = content }, ct, allowRetry: false).ConfigureAwait(false);
    }

    public bool TryReserveOptionalPrefetch(long bytes)
    {
        if (bytes <= 0) return false;
        while (true)
        {
            var remaining = Interlocked.Read(ref _optionalPrefetchBytesRemaining);
            if (bytes > remaining) return false;
            if (Interlocked.CompareExchange(ref _optionalPrefetchBytesRemaining, remaining - bytes, remaining) == remaining)
            {
                return true;
            }
        }
    }

    private void ResetOptionalPrefetchBudget()
    {
        Interlocked.Exchange(ref _optionalPrefetchBytesRemaining,
            Math.Max(0, _snowcloakConfig.Current.OptionalPrefetchByteBudget));
    }

    public async Task<HttpResponseMessage> SendFileDownloadRequestAsync(Uri uri, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            EnsureAllowedDownloadUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location == null)
            {
                throw new HttpRequestException("File download redirect did not include a destination.");
            }

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }

        throw new HttpRequestException("File download exceeded the redirect limit.");
    }

    private void SetAllowedDownloadHosts(IEnumerable<string> configuredHosts)
    {
        lock (_allowedFileDownloadHosts)
        {
            _allowedFileDownloadHosts.Clear();
            if (FilesCdnUri != null)
            {
                _allowedFileDownloadHosts.Add(FilesCdnUri.IdnHost);
            }

            foreach (var configuredHost in configuredHosts)
            {
                if (Uri.TryCreate(configuredHost, UriKind.Absolute, out var uri))
                {
                    _allowedFileDownloadHosts.Add(uri.IdnHost);
                }
                else if (!string.IsNullOrWhiteSpace(configuredHost))
                {
                    _allowedFileDownloadHosts.Add(configuredHost.Trim());
                }
            }
        }
    }

    private void EnsureAllowedDownloadUri(Uri uri)
    {
        var baseUri = FilesCdnUri ?? throw new InvalidOperationException("File transfer service is not initialised.");
        var schemeAllowed = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        lock (_allowedFileDownloadHosts)
        {
            if (!schemeAllowed || !_allowedFileDownloadHosts.Contains(uri.IdnHost))
            {
                throw new InvalidOperationException($"The server returned an unapproved file download address: {uri.Host}");
            }
        }
    }

    private async Task<HttpResponseMessage> SendRequestInternalAsync(Func<HttpRequestMessage> requestFactory,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead, bool allowRetry = true)
    {
        var token = await _tokenProvider.GetFilesToken(ct ?? CancellationToken.None).ConfigureAwait(false);

        var attempt = 0;
        while (true)
        {
            attempt++;
            using var requestMessage = requestFactory();
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var untrackedTimeout = ct == null ? new CancellationTokenSource(UntrackedRequestTimeout) : null;
            var requestToken = ct ?? untrackedTimeout!.Token;

            if (Logger.IsEnabled(LogLevel.Debug)
                && requestMessage.Content != null && requestMessage.Content is not StreamContent && requestMessage.Content is not ByteArrayContent)
            {
                var content = await ((JsonContent)requestMessage.Content).ReadAsStringAsync(requestToken).ConfigureAwait(false);
                LogSendingRequestWithContent(Logger, requestMessage.Method, requestMessage.RequestUri, content);
            }
            else if (Logger.IsEnabled(LogLevel.Debug))
            {
                LogSendingRequest(Logger, requestMessage.Method, requestMessage.RequestUri);
            }

            try
            {
                var response = await _httpClient.SendAsync(requestMessage, httpCompletionOption, requestToken).ConfigureAwait(false);

                if (allowRetry && attempt < MaxRateLimitAttempts && response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var delay = GetRateLimitRetryDelay(response.Headers.RetryAfter);
                    LogRateLimitRetry(Logger, requestMessage.RequestUri, attempt, delay);
                    response.Dispose();
                    await Task.Delay(delay, requestToken).ConfigureAwait(false);
                    continue;
                }

                if (allowRetry && attempt < MaxTransientAttempts && TransientStatusCodes.Contains(response.StatusCode))
                {
                    var delay = GetTransientRetryDelay(response.Headers.RetryAfter?.Delta, attempt);
                    LogTransientRetry(Logger, requestMessage.RequestUri, (int)response.StatusCode, attempt, delay);
                    response.Dispose();
                    await Task.Delay(delay, requestToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException ex) when (ct is { IsCancellationRequested: true })
            {
                LogRequestCancelled(Logger, ex, requestMessage.RequestUri);
                throw;
            }
            catch (OperationCanceledException ex) when (untrackedTimeout?.IsCancellationRequested == true)
            {
                LogRequestTimedOut(Logger, ex, requestMessage.RequestUri, UntrackedRequestTimeout);
                throw;
            }
            catch (HttpRequestException ex)
            {
                if (allowRetry && attempt < MaxTransientAttempts)
                {
                    var delay = GetTransientRetryDelay(null, attempt);
                    LogTransientRetry(Logger, requestMessage.RequestUri,
                        ex.StatusCode is { } statusCode ? (int)statusCode : 0, attempt, delay);
                    await Task.Delay(delay, requestToken).ConfigureAwait(false);
                    continue;
                }
                throw new HttpRequestException($"Error during file transfer request for {requestMessage.RequestUri}", ex, ex.StatusCode);
            }
        }
    }

    private static TimeSpan GetTransientRetryDelay(TimeSpan? retryAfter, int attempt)
    {
        if (retryAfter is { } delta && delta > TimeSpan.Zero)
        {
            return delta < TransientRetryMaxDelay ? delta : TransientRetryMaxDelay;
        }

        var backoff = TransientRetryBaseDelay * Math.Pow(2, attempt - 1);
        return backoff < TransientRetryMaxDelay ? backoff : TransientRetryMaxDelay;
    }

    private static TimeSpan GetRateLimitRetryDelay(RetryConditionHeaderValue? retryAfter)
    {
        var delay = retryAfter?.Delta;
        if (delay == null && retryAfter?.Date is { } retryAt)
        {
            delay = retryAt - DateTimeOffset.UtcNow;
        }

        if (delay == null || delay <= TimeSpan.Zero)
        {
            return RateLimitRetryFallback;
        }

        return delay < RateLimitRetryMaximum ? delay.Value : RateLimitRetryMaximum;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Request to {Uri} was cancelled")]
    private static partial void LogRequestCancelled(ILogger logger, Exception ex, Uri? uri);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request to {Uri} timed out after {Timeout}")]
    private static partial void LogRequestTimedOut(ILogger logger, Exception ex, Uri? uri, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient error {StatusCode} from {Uri} (attempt {Attempt}); retrying after {Delay}")]
    private static partial void LogTransientRetry(ILogger logger, Uri? uri, int statusCode, int attempt, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rate limited by {Uri} (attempt {Attempt}); retrying after {Delay}")]
    private static partial void LogRateLimitRetry(ILogger logger, Uri? uri, int attempt, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Calculated bandwidth limit is negative, returning infinity: {Value}, active slots: {Slots}, download speed limit: {Limit}, configured slots: {Configured}")]
    private static partial void LogInvalidDownloadLimit(ILogger logger, long value, int slots, long limit, int configured);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending {Method} to {Uri} (Content: {Content})")]
    private static partial void LogSendingRequestWithContent(ILogger logger, HttpMethod method, Uri? uri, string content);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending {Method} to {Uri}")]
    private static partial void LogSendingRequest(ILogger logger, HttpMethod method, Uri? uri);
}
