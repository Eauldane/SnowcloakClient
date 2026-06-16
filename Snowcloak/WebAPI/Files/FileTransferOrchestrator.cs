using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Core.Async;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.WebAPI.SignalR;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;

namespace Snowcloak.WebAPI.Files;

public partial class FileTransferOrchestrator : DisposableMediatorSubscriberBase
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UntrackedRequestTimeout = TimeSpan.FromSeconds(100);

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
        using var requestMessage = new HttpRequestMessage(method, uri);
        return await SendRequestInternalAsync(requestMessage, ct, httpCompletionOption).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestAsync<T>(HttpMethod method, Uri uri, T content, CancellationToken ct) where T : class
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        if (content is not ByteArrayContent)
            requestMessage.Content = JsonContent.Create(content);
        else
            requestMessage.Content = content as ByteArrayContent;
        return await SendRequestInternalAsync(requestMessage, ct).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestStreamAsync(HttpMethod method, Uri uri, ProgressableStreamContent content, CancellationToken ct)
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        requestMessage.Content = content;
        return await SendRequestInternalAsync(requestMessage, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendRequestInternalAsync(HttpRequestMessage requestMessage,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead)
    {
        var token = await _tokenProvider.GetToken().ConfigureAwait(false);
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
            return await _httpClient.SendAsync(requestMessage, httpCompletionOption, requestToken).ConfigureAwait(false);
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
            throw new HttpRequestException($"Error during file transfer request for {requestMessage.RequestUri}", ex, ex.StatusCode);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Request to {Uri} was cancelled")]
    private static partial void LogRequestCancelled(ILogger logger, Exception ex, Uri? uri);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request to {Uri} timed out after {Timeout}")]
    private static partial void LogRequestTimedOut(ILogger logger, Exception ex, Uri? uri, TimeSpan timeout);
}
