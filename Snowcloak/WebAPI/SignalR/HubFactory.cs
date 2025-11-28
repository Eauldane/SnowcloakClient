using Snowcloak.API.SignalR;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Snowcloak.WebAPI.SignalR;

public class HubFactory : MediatorSubscriberBase
{
    private readonly ILoggerProvider _loggingProvider;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly TokenProvider _tokenProvider;
    private HubConnection? _instance;
    private string _cachedConfigFor = string.Empty;
    private HubConnectionConfig? _cachedConfig;
    private bool _isDisposed = false;

    public HubFactory(ILogger<HubFactory> logger, SnowMediator mediator,
        ServerConfigurationManager serverConfigurationManager, 
        TokenProvider tokenProvider, ILoggerProvider pluginLog) : base(logger, mediator)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _tokenProvider = tokenProvider;
        _loggingProvider = pluginLog;
    }

    public async Task DisposeHubAsync()
    {
        if (_instance == null || _isDisposed) return;

        Logger.LogDebug("Disposing current HubConnection");

        _isDisposed = true;

        _instance.Closed -= HubOnClosed;
        _instance.Reconnecting -= HubOnReconnecting;
        _instance.Reconnected -= HubOnReconnected;

        await _instance.StopAsync().ConfigureAwait(false);
        await _instance.DisposeAsync().ConfigureAwait(false);

        _instance = null;

        Logger.LogDebug("Current HubConnection disposed");
    }

    public async Task<HubConnection> GetOrCreate(CancellationToken ct)
    {
        if (!_isDisposed && _instance != null) return _instance;

        _cachedConfig = await ResolveHubConfig().ConfigureAwait(false);
        _cachedConfigFor = _serverConfigurationManager.CurrentApiUrl;

        return BuildHubConnection(_cachedConfig, ct);
    }

    private async Task<HubConnectionConfig> ResolveHubConfig()
    {
        var stapledWellKnown = _tokenProvider.GetStapledWellKnown(_serverConfigurationManager.CurrentApiUrl);

        var apiUrl = new Uri(_serverConfigurationManager.CurrentApiUrl);

        HubConnectionConfig defaultConfig;

        if (_cachedConfig != null && _serverConfigurationManager.CurrentApiUrl.Equals(_cachedConfigFor, StringComparison.Ordinal))
        {
            defaultConfig = _cachedConfig;
        }
        else
        {
            defaultConfig = new HubConnectionConfig
            {
                HubUrl = _serverConfigurationManager.CurrentApiUrl.TrimEnd('/') + ISnowHub.Path,
                Transports = []
            };
        }

        string jsonResponse;

        if (stapledWellKnown != null)
        {
            jsonResponse = stapledWellKnown;
            Logger.LogTrace("Using stapled hub config for {url}", _serverConfigurationManager.CurrentApiUrl);
        }
        else
        {
            try
            {
                var httpScheme = apiUrl.Scheme.ToLowerInvariant() switch
                {
                    "ws" => "http",
                    "wss" => "https",
                    _ => apiUrl.Scheme
                };

                var wellKnownUrl = $"{httpScheme}://{apiUrl.Host}/.well-known/Snowcloak/client";
                Logger.LogTrace("Fetching hub config for {uri} via {wk}", _serverConfigurationManager.CurrentApiUrl, wellKnownUrl);

                using var httpClient = new HttpClient(
                    new HttpClientHandler
                    {
                        AllowAutoRedirect = true,
                        MaxAutomaticRedirections = 5
                    }
                );

                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Snowcloak", ver!.Major + "." + ver!.Minor + "." + ver!.Build));

                var response = await httpClient.GetAsync(wellKnownUrl).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return defaultConfig;

                var contentType = response.Content.Headers.ContentType?.MediaType;

                if (contentType == null || !contentType.Equals("application/json", StringComparison.Ordinal))
                    return defaultConfig;

                jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogWarning(ex, "HTTP request failed for .well-known");
                return defaultConfig;
            }
        }

        try
        {
            var config = JsonSerializer.Deserialize<HubConnectionConfig>(jsonResponse);

            if (config == null)
                return defaultConfig;

            if (string.IsNullOrEmpty(config.ApiUrl))
                config.ApiUrl = defaultConfig.ApiUrl;

            if (string.IsNullOrEmpty(config.HubUrl))
                config.HubUrl = defaultConfig.HubUrl;

            config.Transports ??= defaultConfig.Transports ?? [];

            return config;
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Invalid JSON in .well-known response");
            return defaultConfig;
        }
    }

    private HubConnection BuildHubConnection(HubConnectionConfig hubConfig, CancellationToken ct)
    {
        Logger.LogDebug("Building new HubConnection");

        _instance = new HubConnectionBuilder()
            .WithUrl(hubConfig.HubUrl, options =>
            {
                var transports =  hubConfig.TransportType;
                options.AccessTokenProvider = () => _tokenProvider.GetOrUpdateToken(ct);
                options.SkipNegotiation = hubConfig.SkipNegotiation && (transports == HttpTransportType.WebSockets);
                options.Transports = transports;
            })
            .AddMessagePackProtocol(opt =>
            {
                var resolver = CompositeResolver.Create(StandardResolverAllowPrivate.Instance,
                    BuiltinResolver.Instance,
                    AttributeFormatterResolver.Instance,
                    // replace enum resolver
                    DynamicEnumAsStringResolver.Instance,
                    DynamicGenericResolver.Instance,
                    DynamicUnionResolver.Instance,
                    DynamicObjectResolver.Instance,
                    PrimitiveObjectResolver.Instance,
                    // final fallback(last priority)
                    StandardResolver.Instance);

                opt.SerializerOptions =
                    MessagePackSerializerOptions.Standard
                        .WithCompression(MessagePackCompression.Lz4Block)
                        .WithResolver(resolver);
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy(Mediator))
            .ConfigureLogging(a =>
            {
                a.ClearProviders().AddProvider(_loggingProvider);
                a.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        _instance.Closed += HubOnClosed;
        _instance.Reconnecting += HubOnReconnecting;
        _instance.Reconnected += HubOnReconnected;

        _isDisposed = false;

        return _instance;
    }

    private Task HubOnClosed(Exception? arg)
    {
        Mediator.Publish(new HubClosedMessage(arg));
        return Task.CompletedTask;
    }

    private Task HubOnReconnected(string? arg)
    {
        Mediator.Publish(new HubReconnectedMessage(arg));
        return Task.CompletedTask;
    }

    private Task HubOnReconnecting(Exception? arg)
    {
        Mediator.Publish(new HubReconnectingMessage(arg));
        return Task.CompletedTask;
    }
}