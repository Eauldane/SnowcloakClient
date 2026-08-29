using Snowcloak.API.Protocol;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto;
using Snowcloak.API.Routes;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;

namespace Snowcloak.WebAPI.SignalR;

public sealed class TokenProvider : IDisposable, IMediatorSubscriber
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(90);
    private static readonly Action<ILogger, Exception?> LogIdentityUnavailable =
        LoggerMessage.Define(LogLevel.Error, new EventId(1, nameof(LogIdentityUnavailable)), "Unable to resolve an authentication identity");
    private readonly DalamudUtilService _dalamudUtil;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TokenProvider> _logger;
    private readonly ServerRegistry _serverManager;
    private readonly ConcurrentDictionary<JwtIdentifier, CachedTokenBundle> _tokenCache = new();
    private readonly ConcurrentDictionary<string, string?> _wellKnownCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private sealed record MigrationPrompt(JwtIdentifier Identifier, CharacterIdentityMigrationDto Response);
    private MigrationPrompt? _migrationPrompt;
    private readonly ConcurrentDictionary<JwtIdentifier, (Guid? ProfileId, bool Separate)> _migrationChoices = new();

    public CharacterIdentityMigrationDto? PendingMigration
    {
        get
        {
            var prompt = Volatile.Read(ref _migrationPrompt);
            var character = _serverManager.GetCurrentCharacterIdentity();
            return prompt != null && character.IsValid && prompt.Identifier.ContentId == character.ContentId
                && prompt.Identifier.ApiUrl == _serverManager.CurrentApiUrl ? prompt.Response : null;
        }
    }

    public void ResolveMigration(Guid? profileId, bool separate)
    {
        var prompt = Volatile.Read(ref _migrationPrompt);
        if (prompt == null || PendingMigration == null
            || (separate ? !prompt.Response.CanCreateSeparateIdentity : !prompt.Response.Profiles.Any(p => p.ProfileId == profileId)))
            return;
        _migrationChoices[prompt.Identifier] = (profileId, separate);
        Remove(prompt.Identifier);
        Volatile.Write(ref _migrationPrompt, null);
    }
    private bool _disposed;

    public TokenProvider(ILogger<TokenProvider> logger, ServerRegistry serverManager,
        DalamudUtilService dalamudUtil, SnowMediator snowMediator)
    {
        _logger = logger;
        _serverManager = serverManager;
        _dalamudUtil = dalamudUtil;
#pragma warning disable CA2000
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CheckCertificateRevocationList = true,
            MaxAutomaticRedirections = 5
        };
        _httpClient = new(handler, disposeHandler: true);
#pragma warning restore CA2000
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        Mediator = snowMediator;
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => Clear());
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => Clear());
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Snowcloak", ver!.Major + "." + ver.Minor + "." + ver.Build));
    }

    public SnowMediator Mediator { get; }

    public Task<string?> GetOrUpdateToken(CancellationToken cancellationToken)
    {
        return GetToken(static bundle => bundle.HubToken, cancellationToken);
    }

    public Task<string?> GetFilesToken(CancellationToken cancellationToken = default)
    {
        return GetToken(static bundle => bundle.FilesToken, cancellationToken);
    }

    public Task<string?> GetAuthToken(CancellationToken cancellationToken)
    {
        return GetToken(static bundle => bundle.AuthToken, cancellationToken);
    }

    public string? GetStapledWellKnown(Uri apiUri)
    {
        ArgumentNullException.ThrowIfNull(apiUri);
        _wellKnownCache.TryGetValue(apiUri.ToString(), out var wellKnown);
        return string.IsNullOrEmpty(wellKnown) ? null : wellKnown;
    }

    private async Task<string?> GetToken(Func<CachedTokenBundle, CachedToken> selector, CancellationToken cancellationToken)
    {
        var identifier = await GetIdentifier().ConfigureAwait(false);
        if (identifier == null)
        {
            return null;
        }

        if (_tokenCache.TryGetValue(identifier, out var cached) && selector(cached).IsUsable)
        {
            return selector(cached).Value;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokenCache.TryGetValue(identifier, out cached) && selector(cached).IsUsable)
            {
                return selector(cached).Value;
            }

            cached = await RequestTokens(identifier, cancellationToken).ConfigureAwait(false);
            return selector(cached).Value;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<CachedTokenBundle> RequestTokens(JwtIdentifier identifier, CancellationToken cancellationToken)
    {
        try
        {
            var tokenUri = SnowAuth.AuthContentIdFullPath(new Uri(identifier.ApiUrl
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)));
            var aliases = _serverManager.GetPendingIdentityAliases(identifier.ApiUrl, identifier.ContentId, identifier.SecretKey);
            _migrationChoices.TryGetValue(identifier, out var choice);
            var request = new CharacterIdentityLoginDto
            {
                Auth = identifier.SecretKey.GetHash256(), CharacterIdent = identifier.CharaHash,
                LegacyIdents = aliases, RecoverProfileId = choice.ProfileId, CreateSeparateIdentity = choice.Separate
            };
            using var result = await _httpClient.PostAsJsonAsync(tokenUri, request, cancellationToken).ConfigureAwait(false);
            if (result.StatusCode == HttpStatusCode.Conflict)
            {
                var migration = await result.Content.ReadFromJsonAsync<CharacterIdentityMigrationDto>(cancellationToken).ConfigureAwait(false)
                    ?? new() { Message = "Character identity migration needs attention." };
                Volatile.Write(ref _migrationPrompt, new MigrationPrompt(identifier, migration));
                throw new SnowAuthFailureException(migration.Message + " Open Settings > Service to resolve the profile migration.");
            }
            if (!result.IsSuccessStatusCode)
            {
                var textResponse = await result.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
                Remove(identifier);
                if (result.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new SnowAuthFailureException(textResponse);
                }

                throw new HttpRequestException(textResponse, null, result.StatusCode);
            }

            var response = await result.Content.ReadFromJsonAsync<AuthReplyDto>(cancellationToken).ConfigureAwait(false) ?? new();
            if (!response.CharacterIdentityId.HasValue)
                throw new SnowAuthFailureException("The server did not acknowledge the ContentID binding. API 4203 is required.");
            if (await GetIdentifier().ConfigureAwait(false) != identifier)
                throw new OperationCanceledException("The active character or key changed during authentication.");
            _serverManager.AcknowledgeIdentityAliases(identifier.ApiUrl, identifier.ContentId, identifier.SecretKey,
                response.CharacterIdentityId.Value, response.AcknowledgedLegacyIdents.Intersect(aliases, StringComparer.Ordinal).ToArray());
            _migrationChoices.TryRemove(identifier, out _);
            Volatile.Write(ref _migrationPrompt, null);
            var accessTokens = response.AccessTokens;
            var fallback = response.Token;
            var bundle = new CachedTokenBundle(
                CachedToken.Create(string.IsNullOrWhiteSpace(accessTokens.HubToken) ? fallback : accessTokens.HubToken),
                CachedToken.Create(string.IsNullOrWhiteSpace(accessTokens.FilesToken) ? fallback : accessTokens.FilesToken),
                CachedToken.Create(string.IsNullOrWhiteSpace(accessTokens.AuthToken) ? fallback : accessTokens.AuthToken));
            _tokenCache[identifier] = bundle;
            _wellKnownCache[identifier.ApiUrl] = response.WellKnown;
            return bundle;
        }
        catch (HttpRequestException ex)
        {
            Remove(identifier);
            if (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new SnowAuthFailureException(ex.Message);
            }

            throw;
        }
    }

    private async Task<JwtIdentifier?> GetIdentifier()
    {
        try
        {
            var character = await _dalamudUtil.GetCurrentCharacterIdentityAsync().ConfigureAwait(false);
            if (!character.IsValid)
                return null;
            var server = _serverManager.CurrentServer;
            var assignment = _serverManager.GetCharacterAssignment(server, character, out var ambiguous);
            if (ambiguous || assignment == null || !server.SecretKeys.TryGetValue(assignment.SecretKeyIdx, out var key))
                return null;
            return new JwtIdentifier(server.ServerUri, CharacterIdentityProtocol.FromContentId(character.ContentId), key.Key, character.ContentId);
        }
        catch (InvalidOperationException ex)
        {
            return HandleIdentifierFailure(ex);
        }
        catch (ArgumentException ex)
        {
            return HandleIdentifierFailure(ex);
        }
        catch (KeyNotFoundException ex)
        {
            return HandleIdentifierFailure(ex);
        }
    }

    private JwtIdentifier? HandleIdentifierFailure(Exception ex)
    {
        LogIdentityUnavailable(_logger, ex);
        return null;
    }

    private void Remove(JwtIdentifier identifier)
    {
        _tokenCache.TryRemove(identifier, out _);
        _wellKnownCache.TryRemove(_serverManager.CurrentApiUrl, out _);
    }

    private void Clear()
    {
        Volatile.Write(ref _migrationPrompt, null);
        _migrationChoices.Clear();
        _tokenCache.Clear();
        _wellKnownCache.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Mediator.UnsubscribeAll(this);
        _httpClient.Dispose();
        _refreshLock.Dispose();
    }

    private sealed record CachedTokenBundle(CachedToken HubToken, CachedToken FilesToken, CachedToken AuthToken);

    private sealed record CachedToken(string Value, DateTimeOffset RefreshAtUtc)
    {
        public bool IsUsable => !string.IsNullOrWhiteSpace(Value) && DateTimeOffset.UtcNow < RefreshAtUtc;

        public static CachedToken Create(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new CachedToken(string.Empty, DateTimeOffset.MinValue);
            }

            var token = new JwtSecurityTokenHandler().ReadJwtToken(value);
            var expiry = token.ValidTo == DateTime.MinValue
                ? DateTimeOffset.UtcNow.AddMinutes(5)
                : new DateTimeOffset(token.ValidTo, TimeSpan.Zero);
            var jitter = TimeSpan.FromSeconds(RandomNumberGenerator.GetInt32(0, 31));
            return new CachedToken(value, expiry - RefreshSkew - jitter);
        }
    }
}
