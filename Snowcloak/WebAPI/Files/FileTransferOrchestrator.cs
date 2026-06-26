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
    private static readonly TimeSpan TransientRetryBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TransientRetryMaxDelay = TimeSpan.FromSeconds(8);
    private static readonly HashSet<HttpStatusCode> TransientStatusCodes =
    [
        HttpStatusCode.RequestTimeout,      // 408
        HttpStatusCode.BadGateway,          // 502
        HttpStatusCode.ServiceUnavailable,  // 503
        HttpStatusCode.GatewayTimeout,      // 504
    ];

    private readonly HttpClient _httpClient;
    private readonly SnowcloakConfigService _snowcloakConfig;
    private readonly TokenProvider _tokenProvider;
    private readonly DownloadSlotGate _downloadSlots;
    private readonly Lock _forbiddenLock = new();
    private readonly List<ForbiddenTransfer> _forbiddenTransfers = [];

    public FileTransferOrchestrator(ILogger<FileTransferOrchestrator> logger, SnowcloakConfigService snowcloakConfig,
        SnowMediator mediator, TokenProvider tokenProvider) : base(logger, mediator)
    {
        _snowcloakConfig = snowcloakConfig;
        _tokenProvider = tokenProvider;
        _httpClient = new HttpClient(new SocketsHttpHandler { ConnectTimeout = ConnectTimeout })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Snowcloak", ver!.Major + "." + ver!.Minor + "." + ver!.Build));

        _downloadSlots = new DownloadSlotGate(snowcloakConfig.Current.ParallelDownloads);
        
        Mediator.Subscribe<FileServerInfoReceivedMessage>(this, (msg) =>
        {
            FilesCdnUri = msg.Connection.ServerInfo.FileServerAddress;
        });

        Mediator.Subscribe<ConnectedMessage>(this, (msg) =>
        {
            FilesCdnUri = msg.Connection.ServerInfo.FileServerAddress;
        });

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            FilesCdnUri = null;
        });
    }

    public Uri? FilesCdnUri { private set; get; }
    public bool IsInitialized => FilesCdnUri != null;

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

    public IReadOnlyList<ForbiddenTransfer> GetForbiddenTransfers()
    {
        lock (_forbiddenLock)
        {
            return [.. _forbiddenTransfers];
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
            Logger.LogWarning("Calculated Bandwidth Limit is negative, returning Infinity: {value}, active slots: {slots}, " +
                "DownloadSpeedLimit is {limit}, configured slots: {configured}", dividedLimit, activeSlots, limit, _downloadSlots.Limit);
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

    private async Task<HttpResponseMessage> SendRequestInternalAsync(Func<HttpRequestMessage> requestFactory,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead, bool allowRetry = true)
    {
        var token = await _tokenProvider.GetToken().ConfigureAwait(false);

        var attempt = 0;
        while (true)
        {
            attempt++;
            using var requestMessage = requestFactory();
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            if (requestMessage.Content != null && requestMessage.Content is not StreamContent && requestMessage.Content is not ByteArrayContent)
            {
                var content = await ((JsonContent)requestMessage.Content).ReadAsStringAsync().ConfigureAwait(false);
                Logger.LogDebug("Sending {method} to {uri} (Content: {content})", requestMessage.Method, requestMessage.RequestUri, content);
            }
            else
            {
                Logger.LogDebug("Sending {method} to {uri}", requestMessage.Method, requestMessage.RequestUri);
            }

            using var untrackedTimeout = ct == null ? new CancellationTokenSource(UntrackedRequestTimeout) : null;
            var requestToken = ct ?? untrackedTimeout!.Token;

            try
            {
                var response = await _httpClient.SendAsync(requestMessage, httpCompletionOption, requestToken).ConfigureAwait(false);

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
                    LogTransientRetry(Logger, requestMessage.RequestUri, (int)(ex.StatusCode ?? 0), attempt, delay);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Request to {Uri} was cancelled")]
    private static partial void LogRequestCancelled(ILogger logger, Exception ex, Uri? uri);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request to {Uri} timed out after {Timeout}")]
    private static partial void LogRequestTimedOut(ILogger logger, Exception ex, Uri? uri, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient error {StatusCode} from {Uri} (attempt {Attempt}); retrying after {Delay}")]
    private static partial void LogTransientRetry(ILogger logger, Uri? uri, int statusCode, int attempt, TimeSpan delay);
}
