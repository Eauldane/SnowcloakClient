using Snowcloak.API.Dto;
using Snowcloak.API.Routes;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;

namespace Snowcloak.WebAPI.SignalR;

public sealed class TokenProvider : IDisposable, IMediatorSubscriber
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TokenProvider> _logger;
    private readonly ServerConfigurationManager _serverManager;
    private readonly ConcurrentDictionary<JwtIdentifier, string> _tokenCache = new();
    private readonly ConcurrentDictionary<string, string?> _wellKnownCache = new(StringComparer.Ordinal);

    public TokenProvider(ILogger<TokenProvider> logger, ServerConfigurationManager serverManager,
        DalamudUtilService dalamudUtil, SnowMediator snowMediator)
    {
        _logger = logger;
        _serverManager = serverManager;
        _dalamudUtil = dalamudUtil;
        _httpClient = new(
            new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            }
        );
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        Mediator = snowMediator;
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) =>
        {
            _lastJwtIdentifier = null;
            _tokenCache.Clear();
            _wellKnownCache.Clear();
        });
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) =>
        {
            _lastJwtIdentifier = null;
            _tokenCache.Clear();
            _wellKnownCache.Clear();
        });
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Snowcloak", ver!.Major + "." + ver!.Minor + "." + ver!.Build));
    }

    public SnowMediator Mediator { get; }

    private JwtIdentifier? _lastJwtIdentifier;

    public void Dispose()
    {
        Mediator.UnsubscribeAll(this);
        _httpClient.Dispose();
    }

    public async Task<string> GetNewToken(JwtIdentifier identifier, CancellationToken token)
    {
        Uri tokenUri;
        HttpResponseMessage result;

        try
        {
            _logger.LogDebug("GetNewToken: Requesting");

            tokenUri = SnowAuth.AuthV2FullPath(new Uri(_serverManager.CurrentApiUrl
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)));
            var secretKey = _serverManager.GetSecretKey(out _)!;
            var auth = secretKey.GetHash256();
            result = await _httpClient.PostAsync(tokenUri, new FormUrlEncodedContent([
                new("auth", auth),
                new("charaIdent", await _dalamudUtil.GetPlayerNameHashedAsync().ConfigureAwait(false)),
            ]), token).ConfigureAwait(false);

            if (!result.IsSuccessStatusCode)
            {
                var textResponse = await result.Content.ReadAsStringAsync(token).ConfigureAwait(false) ?? string.Empty;
                Mediator.Publish(new NotificationMessage("Error refreshing token", "Your authentication token could not be renewed. Try reconnecting manually.", NotificationType.Error));
                Mediator.Publish(new DisconnectedMessage());
                throw new SnowAuthFailureException(textResponse);
            }

            var response = await result.Content.ReadFromJsonAsync<AuthReplyDto>(token).ConfigureAwait(false) ?? new();
            _tokenCache[identifier] = response.Token;
            _wellKnownCache[_serverManager.CurrentApiUrl] = response.WellKnown;
            return response.Token;
        }
        catch (HttpRequestException ex)
        {
            _tokenCache.TryRemove(identifier, out _);
            _wellKnownCache.TryRemove(_serverManager.CurrentApiUrl, out _);

            _logger.LogError(ex, "GetNewToken: Failure to get token");

            if (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                Mediator.Publish(new NotificationMessage("Error refreshing token", "Your authentication token could not be renewed. Try reconnecting manually.", NotificationType.Error));
                Mediator.Publish(new DisconnectedMessage());
                throw new SnowAuthFailureException(ex.Message);
            }

            throw;
        }
    }

    private async Task<JwtIdentifier?> GetIdentifier()
    {
        JwtIdentifier jwtIdentifier;
        try
        {
            var playerIdentifier = await _dalamudUtil.GetPlayerNameHashedAsync().ConfigureAwait(false);

            if (string.IsNullOrEmpty(playerIdentifier))
            {
                _logger.LogTrace("GetIdentifier: PlayerIdentifier was null, returning last identifier {identifier}", _lastJwtIdentifier);
                return _lastJwtIdentifier;
            }

            jwtIdentifier = new(_serverManager.CurrentApiUrl,
                                playerIdentifier,
                                _serverManager.GetSecretKey(out _)!);
            _lastJwtIdentifier = jwtIdentifier;
        }
        catch (Exception ex)
        {
            if (_lastJwtIdentifier == null)
            {
                _logger.LogError("GetIdentifier: No last identifier found, aborting");
                return null;
            }

            _logger.LogWarning(ex, "GetIdentifier: Could not get JwtIdentifier for some reason or another, reusing last identifier {identifier}", _lastJwtIdentifier);
            jwtIdentifier = _lastJwtIdentifier;
        }

        _logger.LogDebug("GetIdentifier: Using identifier {identifier}", jwtIdentifier);
        return jwtIdentifier;
    }

    public async Task<string?> GetToken()
    {
        JwtIdentifier? jwtIdentifier = await GetIdentifier().ConfigureAwait(false);
        if (jwtIdentifier == null) return null;

        if (_tokenCache.TryGetValue(jwtIdentifier, out var token))
        {
            return token;
        }

        throw new InvalidOperationException("No token present");
    }

    public async Task<string?> GetOrUpdateToken(CancellationToken ct)
    {
        JwtIdentifier? jwtIdentifier = await GetIdentifier().ConfigureAwait(false);
        if (jwtIdentifier == null) return null;

        if (_tokenCache.TryGetValue(jwtIdentifier, out var token))
            return token;

        _logger.LogTrace("GetOrUpdate: Getting new token");
        return await GetNewToken(jwtIdentifier, ct).ConfigureAwait(false);
    }

    public string? GetStapledWellKnown(string apiUrl)
    {
        _wellKnownCache.TryGetValue(apiUrl, out var wellKnown);
        // Treat an empty string as null -- it won't decode as JSON anyway
        if (string.IsNullOrEmpty(wellKnown))
            return null;
        return wellKnown;
    }
}
