using Dalamud.Utility;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.Account;
using Snowcloak.API.Routes;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.Services;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using Snowcloak.WebAPI.SignalR;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;

namespace Snowcloak.WebAPI;

public sealed class AccountRegistrationService : IDisposable
{
    public sealed record PatreonStatusResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public bool IsLinked { get; set; }
        public bool IsPayingPatron { get; set; }
        public bool HasBenefits { get; set; }
        public bool IsCompetitionWinner { get; set; }
        public bool IsTestOverride { get; set; }
        public bool IsCreatorForCampaign { get; set; }
    }

    public sealed record PatreonLoginResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public bool IsLinked { get; set; }
        public bool IsPayingPatron { get; set; }
        public bool HasBenefits { get; set; }
        public bool IsCompetitionWinner { get; set; }
        public bool IsTestOverride { get; set; }
        public bool IsCreatorForCampaign { get; set; }
    }

    public sealed record AccountOperationResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string Uid { get; set; } = string.Empty;
        public Guid? UserAccountId { get; set; }
        public int SecretKeyCount { get; set; }
        public int NewSecretKeyCount { get; set; }
        public int LinkedLocalSecretKeyCount { get; set; }
    }

    private const string PatreonStartRoute = "/auth/patreon/link/start";
    private const string PatreonPollRoutePrefix = "/auth/patreon/link/poll/";
    private const string PatreonStatusRoute = "/auth/patreon/status";
    private const string PasswordLoginRoute = "/auth/password/login";
    private const string AccountPasswordRoute = "/auth/account/password";
    private const string AccountKeysRoute = "/auth/account/keys";
    private const string AccountKeyLinkRoute = "/auth/account/keys/link";
    private const string AccountUidRoute = "/auth/account/uid";

    private readonly HttpClient _httpClient;
    private readonly ILogger<AccountRegistrationService> _logger;
    private readonly ServerConfigurationManager _serverManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly TokenProvider _tokenProvider;

    private static string GenerateSecretKey()
    {
        return Convert.ToHexString(SHA256.HashData(RandomNumberGenerator.GetBytes(64)));
    }

    public AccountRegistrationService(ILogger<AccountRegistrationService> logger, DalamudUtilService dalamudUtilService, ServerConfigurationManager serverManager, TokenProvider tokenProvider)
    {
        _logger = logger;
        _serverManager = serverManager;
        _dalamudUtilService = dalamudUtilService;
        _tokenProvider = tokenProvider;
        _httpClient = new(
            new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            }
        );
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Snowcloak", ver!.Major + "." + ver!.Minor + "." + ver!.Build));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public async Task<RegisterReplyDto> XIVAuth(CancellationToken token)
    {
        var secretKey = GenerateSecretKey();
        var hashedSecretKey = secretKey.GetHash256();
        var playerName = _dalamudUtilService.GetPlayerNameAsync().GetAwaiter().GetResult();
        var worldId = (ushort)_dalamudUtilService.GetHomeWorldIdAsync().GetAwaiter().GetResult();
        var worldName = _dalamudUtilService.WorldData.Value[(worldId)];
            
            
        
        var sessionID = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Uri handshakeUri = new Uri("https://account.snowcloak-sync.com/register");


        var handshakePayload = new { session_id = sessionID, hashed_secret = hashedSecretKey, character_name = playerName, home_world = worldName };
        var handshakeResponse = await _httpClient.PostAsJsonAsync(handshakeUri, handshakePayload, token).ConfigureAwait(false);
        handshakeResponse.EnsureSuccessStatusCode();
        var register = await handshakeResponse.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken: token)
            .ConfigureAwait(false);
        if (register is null || string.IsNullOrWhiteSpace(register.link_url) ||
            string.IsNullOrWhiteSpace(register.poll_url))
        {
            return new RegisterReplyDto() { Success = false, ErrorMessage = "Malformed registration response." };
        }

        Util.OpenLink(register.link_url);
        const int maxAttempts = 600 / 5; // Try once every 15 seconds for 10 minutes
        var pollUri = new Uri(register.poll_url);
        PollResponse? lastPoll = null;
        for (int i = 0; i < maxAttempts; i++)
        {
            token.ThrowIfCancellationRequested();
            using var resp = await _httpClient.GetAsync(pollUri, token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Gone)
            {
                // Server marked this as having been consumed already OR it got TLL'd out
                return new RegisterReplyDto()
                {
                    Success = false, ErrorMessage = "Registration session expired. Please try again."
                };
            }

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                lastPoll = await resp.Content.ReadFromJsonAsync<PollResponse>(cancellationToken: token)
                    .ConfigureAwait(false);
                if (lastPoll?.status?.Equals("bound", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // yay
                    return new RegisterReplyDto()
                    {
                        Success = true, ErrorMessage = null, UID = lastPoll?.uid, SecretKey = secretKey
                    };
                }
                // Pending, keep polling
            }
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
        // Timed out
        return new RegisterReplyDto()
        {
            Success = false,
            ErrorMessage =
                "Timed out waiting for authorisation. Please try again, and complete the process within 10 minutes."
        };

    }
    public async Task<RegisterReplyDto> RegisterAccount(CancellationToken token)
    {
        var secretKey = GenerateSecretKey();
        var hashedSecretKey = secretKey.GetHash256();

        Uri postUri = SnowAuth.AuthRegisterV2FullPath(new Uri(_serverManager.CurrentApiUrl
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)));

        using var result = await _httpClient.PostAsync(postUri, new FormUrlEncodedContent([
            new("hashedSecretKey", hashedSecretKey)
        ]), token).ConfigureAwait(false);
        if (!result.IsSuccessStatusCode)
        {
            return new RegisterReplyDto
            {
                Success = false,
                ErrorMessage = await ReadErrorAsync(result, token).ConfigureAwait(false)
            };
        }

        var response = await result.Content.ReadFromJsonAsync<RegisterReplyV2Dto>(token).ConfigureAwait(false) ?? new();

        return new RegisterReplyDto()
        {
            Success = response.Success,
            ErrorMessage = response.ErrorMessage,
            UID = response.UID,
            SecretKey = secretKey
        };
    }

    public async Task<AccountOperationResult> CreateAccountWithPassword(string username, string password, CancellationToken token,
        Action<string>? reportProgress = null)
    {
        reportProgress?.Invoke("Registering a character key with the selected service...");
        var register = await RegisterAccount(token).ConfigureAwait(false);
        if (!register.Success)
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(register.ErrorMessage)
                    ? "Secret-key registration failed."
                    : register.ErrorMessage
            };
        }

        reportProgress?.Invoke("Character key registered. Creating the password account on the selected service...");
        await StoreRegisteredSecretKeyAsync(register, assignCurrentCharacter: true).ConfigureAwait(false);
        var result = await AttachPasswordToCurrentAccount(username, password, token).ConfigureAwait(false);
        if (!result.Success)
        {
            result.ErrorMessage = "Secret-key registration succeeded, but password account setup failed: " + result.ErrorMessage;
        }

        return result;
    }

    public async Task<AccountOperationResult> LoginWithPassword(string username, string password, CancellationToken token)
    {
        var localKeysToLink = GetLocalSecretKeys(_serverManager.CurrentServer);
        var charaIdent = await _dalamudUtilService.GetPlayerNameHashedAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(charaIdent))
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = "Unable to identify the current character."
            };
        }

        var payload = new PasswordLoginRequest
        {
            Username = username.Trim(),
            Password = password,
            CharaIdent = charaIdent
        };

        using var response = await _httpClient.PostAsJsonAsync(new Uri(GetApiBaseUri(), PasswordLoginRoute), payload, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = await ReadErrorAsync(response, token).ConfigureAwait(false)
            };
        }

        var account = await response.Content.ReadFromJsonAsync<AccountAuthResponse>(cancellationToken: token).ConfigureAwait(false);
        if (account == null || !account.Success)
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = account?.ErrorMessage ?? "Server returned an invalid account login response."
            };
        }

        var linkedCount = await LinkLocalSecretKeysAsync(localKeysToLink, token, account.Token).ConfigureAwait(false);
        if (linkedCount != localKeysToLink.Count)
        {
            return CreateLocalKeyLinkFailure(localKeysToLink.Count, linkedCount);
        }

        var accountKeys = await GetAccountKeys(token, account.Token).ConfigureAwait(false);
        if (accountKeys == null || !accountKeys.Success)
        {
            return CreateAccountKeyDownloadFailure();
        }

        var imported = await StoreAccountSecretKeysAsync(accountKeys.SecretKeys, account.Uid, assignCurrentCharacter: true).ConfigureAwait(false);
        MarkCurrentServerAccountLinked(account.UserAccountId);
        return new AccountOperationResult
        {
            Success = true,
            Uid = account.Uid,
            UserAccountId = account.UserAccountId,
            SecretKeyCount = imported.SecretKeyCount,
            NewSecretKeyCount = imported.NewSecretKeyCount,
            LinkedLocalSecretKeyCount = linkedCount
        };
    }

    public async Task<AccountOperationResult> AttachPasswordToCurrentAccount(string username, string password, CancellationToken token)
    {
        var localKeysToLink = GetLocalSecretKeys(_serverManager.CurrentServer);
        using var request = await CreateAuthorizedRequest(HttpMethod.Post, new Uri(GetApiBaseUri(), AccountPasswordRoute), token).ConfigureAwait(false);
        request.Content = JsonContent.Create(new AttachPasswordRequest
        {
            Username = username.Trim(),
            Password = password
        });

        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = await ReadErrorAsync(response, token).ConfigureAwait(false)
            };
        }

        var account = await response.Content.ReadFromJsonAsync<AccountAuthResponse>(cancellationToken: token).ConfigureAwait(false);
        if (account == null || !account.Success)
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = account?.ErrorMessage ?? "Server returned an invalid account setup response."
            };
        }

        var linkedCount = await LinkLocalSecretKeysAsync(localKeysToLink, token, account.Token).ConfigureAwait(false);
        if (linkedCount != localKeysToLink.Count)
        {
            return CreateLocalKeyLinkFailure(localKeysToLink.Count, linkedCount);
        }

        var accountKeys = await GetAccountKeys(token, account.Token).ConfigureAwait(false);
        if (accountKeys == null || !accountKeys.Success)
        {
            return CreateAccountKeyDownloadFailure();
        }

        var imported = await StoreAccountSecretKeysAsync(accountKeys.SecretKeys, account.Uid, assignCurrentCharacter: false).ConfigureAwait(false);
        MarkCurrentServerAccountLinked(account.UserAccountId);

        return new AccountOperationResult
        {
            Success = true,
            Uid = account.Uid,
            UserAccountId = account.UserAccountId,
            SecretKeyCount = imported.SecretKeyCount,
            NewSecretKeyCount = imported.NewSecretKeyCount,
            LinkedLocalSecretKeyCount = linkedCount
        };
    }

    public async Task<AccountOperationResult> CreateAccountUid(CancellationToken token)
    {
        using var request = await CreateAccountAuthorizedRequest(HttpMethod.Post, new Uri(GetApiBaseUri(), AccountUidRoute), token).ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = await ReadErrorAsync(response, token).ConfigureAwait(false)
            };
        }

        var account = await response.Content.ReadFromJsonAsync<AccountAuthResponse>(cancellationToken: token).ConfigureAwait(false);
        if (account == null || !account.Success)
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = account?.ErrorMessage ?? "Server returned an invalid account UID response."
            };
        }

        var imported = await StoreAccountSecretKeysAsync(account.SecretKeys, account.Uid, assignCurrentCharacter: true).ConfigureAwait(false);
        MarkCurrentServerAccountLinked(account.UserAccountId);
        return new AccountOperationResult
        {
            Success = true,
            Uid = account.Uid,
            UserAccountId = account.UserAccountId,
            SecretKeyCount = imported.SecretKeyCount,
            NewSecretKeyCount = imported.NewSecretKeyCount
        };
    }

    public async Task<PatreonStatusResult> GetPatreonStatus(CancellationToken token)
    {
        try
        {
            var uri = new Uri(GetApiBaseUri(), PatreonStatusRoute);
            using var request = await CreateAuthorizedRequest(HttpMethod.Get, uri, token).ConfigureAwait(false);
            using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new PatreonStatusResult
                {
                    Success = false,
                    ErrorMessage = $"Status check failed ({(int)response.StatusCode})."
                };
            }

            var payload = await response.Content.ReadFromJsonAsync<PatreonStatusResponse>(cancellationToken: token).ConfigureAwait(false) ?? new PatreonStatusResponse();
            return new PatreonStatusResult
            {
                Success = true,
                IsLinked = payload.isLinked,
                IsPayingPatron = payload.isPayingPatron,
                HasBenefits = payload.hasBenefits,
                IsCompetitionWinner = payload.isCompetitionWinner,
                IsTestOverride = payload.isTestOverride,
                IsCreatorForCampaign = payload.isCreatorForCampaign
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Patreon status");
            return new PatreonStatusResult
            {
                Success = false,
                ErrorMessage = "Unable to check Patreon status right now."
            };
        }
    }

    public async Task<PatreonLoginResult> LoginWithPatreon(CancellationToken token)
    {
        try
        {
            var startUri = new Uri(GetApiBaseUri(), PatreonStartRoute);
            using var startRequest = await CreateAuthorizedRequest(HttpMethod.Post, startUri, token).ConfigureAwait(false);
            using var startResponse = await _httpClient.SendAsync(startRequest, token).ConfigureAwait(false);

            if (!startResponse.IsSuccessStatusCode)
            {
                return new PatreonLoginResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to start Patreon login ({(int)startResponse.StatusCode})."
                };
            }

            var startPayload = await startResponse.Content.ReadFromJsonAsync<PatreonLinkStartResponse>(cancellationToken: token).ConfigureAwait(false);
            if (startPayload == null || string.IsNullOrWhiteSpace(startPayload.sessionId) || string.IsNullOrWhiteSpace(startPayload.authorizationUrl))
            {
                return new PatreonLoginResult
                {
                    Success = false,
                    ErrorMessage = "Server returned an invalid Patreon login response."
                };
            }

            Util.OpenLink(startPayload.authorizationUrl);

            var expiry = startPayload.expiresAtUtc > DateTimeOffset.UtcNow
                ? startPayload.expiresAtUtc
                : DateTimeOffset.UtcNow.AddMinutes(10);
            var pollUri = new Uri(GetApiBaseUri(), PatreonPollRoutePrefix + startPayload.sessionId);

            while (DateTimeOffset.UtcNow < expiry)
            {
                token.ThrowIfCancellationRequested();
                using var pollRequest = await CreateAuthorizedRequest(HttpMethod.Get, pollUri, token).ConfigureAwait(false);
                using var pollResponse = await _httpClient.SendAsync(pollRequest, token).ConfigureAwait(false);

                if (!pollResponse.IsSuccessStatusCode)
                {
                    return new PatreonLoginResult
                    {
                        Success = false,
                        ErrorMessage = $"Patreon login polling failed ({(int)pollResponse.StatusCode})."
                    };
                }

                var pollPayload = await pollResponse.Content.ReadFromJsonAsync<PatreonLinkPollResponse>(cancellationToken: token).ConfigureAwait(false) ?? new PatreonLinkPollResponse();
                if (pollPayload.status.Equals("pending", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    continue;
                }

                if (pollPayload.status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                {
                    return new PatreonLoginResult
                    {
                        Success = true,
                        IsLinked = pollPayload.isLinked,
                        IsPayingPatron = pollPayload.isPayingPatron,
                        HasBenefits = pollPayload.hasBenefits,
                        IsCompetitionWinner = pollPayload.isCompetitionWinner,
                        IsTestOverride = pollPayload.isTestOverride,
                        IsCreatorForCampaign = pollPayload.isCreatorForCampaign
                    };
                }

                return new PatreonLoginResult
                {
                    Success = false,
                    ErrorMessage = string.IsNullOrWhiteSpace(pollPayload.errorMessage)
                        ? "Patreon login failed. Please try again."
                        : pollPayload.errorMessage
                };
            }

            return new PatreonLoginResult
            {
                Success = false,
                ErrorMessage = "Timed out waiting for Patreon login to complete."
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patreon login failed");
            return new PatreonLoginResult
            {
                Success = false,
                ErrorMessage = "Unable to complete Patreon login right now."
            };
        }
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequest(HttpMethod method, Uri uri, CancellationToken token)
    {
        var jwt = await _tokenProvider.GetOrUpdateToken(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(jwt))
        {
            throw new InvalidOperationException("No authentication token available.");
        }

        return CreateBearerRequest(method, uri, jwt);
    }

    private async Task<HttpRequestMessage> CreateAccountAuthorizedRequest(HttpMethod method, Uri uri, CancellationToken token)
    {
        try
        {
            return await CreateAuthorizedRequest(method, uri, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to authorize account request with the current character key; trying saved account keys");
        }

        var jwt = await GetTokenForAnyLocalSecretKeyAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(jwt))
        {
            throw new InvalidOperationException("No saved account-linked secret key could authorize the request.");
        }

        return CreateBearerRequest(method, uri, jwt);
    }

    private async Task<string?> GetTokenForAnyLocalSecretKeyAsync(CancellationToken token)
    {
        var charaIdent = await _dalamudUtilService.GetPlayerNameHashedAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(charaIdent))
        {
            return null;
        }

        var tokenUri = SnowAuth.AuthV2FullPath(GetApiBaseUri());
        foreach (var secretKey in GetLocalSecretKeys(_serverManager.CurrentServer))
        {
            using var response = await _httpClient.PostAsync(tokenUri, new FormUrlEncodedContent([
                new("auth", secretKey.GetHash256()),
                new("charaIdent", charaIdent)
            ]), token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var payload = await response.Content.ReadFromJsonAsync<AuthReplyDto>(cancellationToken: token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(payload?.Token)
                && IsExpectedAccountToken(payload.Token, _serverManager.CurrentServer.UserAccountId))
            {
                return payload.Token;
            }
        }

        return null;
    }

    private static bool IsExpectedAccountToken(string jwt, Guid? expectedAccountId)
    {
        try
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
            var accountIdClaim = token.Claims.SingleOrDefault(c => string.Equals(c.Type, "user_account_id", StringComparison.Ordinal))?.Value;
            return Guid.TryParse(accountIdClaim, out var accountId)
                   && (!expectedAccountId.HasValue || accountId == expectedAccountId.Value);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static HttpRequestMessage CreateBearerRequest(HttpMethod method, Uri uri, string jwt)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return request;
    }

    private async Task<AccountKeysResponse?> GetAccountKeys(CancellationToken token, string? jwt = null)
    {
        using var request = string.IsNullOrWhiteSpace(jwt)
            ? await CreateAuthorizedRequest(HttpMethod.Get, new Uri(GetApiBaseUri(), AccountKeysRoute), token).ConfigureAwait(false)
            : CreateBearerRequest(HttpMethod.Get, new Uri(GetApiBaseUri(), AccountKeysRoute), jwt);
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch account keys after password setup: {status}", response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<AccountKeysResponse>(cancellationToken: token).ConfigureAwait(false);
    }

    private async Task<int> LinkLocalSecretKeysAsync(IReadOnlyList<string> secretKeys, CancellationToken token, string? jwt = null)
    {
        var linkedCount = 0;
        foreach (var secretKey in secretKeys)
        {
            using var request = string.IsNullOrWhiteSpace(jwt)
                ? await CreateAuthorizedRequest(HttpMethod.Post, new Uri(GetApiBaseUri(), AccountKeyLinkRoute), token).ConfigureAwait(false)
                : CreateBearerRequest(HttpMethod.Post, new Uri(GetApiBaseUri(), AccountKeyLinkRoute), jwt);
            request.Content = JsonContent.Create(new LinkAccountKeyRequest
            {
                SecretKey = secretKey
            });

            using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to link local account secret key: {status}", response.StatusCode);
                continue;
            }

            var payload = await response.Content.ReadFromJsonAsync<LinkAccountKeyResponse>(cancellationToken: token).ConfigureAwait(false);
            if (payload?.Success == true)
            {
                linkedCount++;
            }
        }

        return linkedCount;
    }

    private static AccountOperationResult CreateLocalKeyLinkFailure(int localKeyCount, int linkedKeyCount)
    {
        return new AccountOperationResult
        {
            Success = false,
            ErrorMessage = string.Format(
                CultureInfo.InvariantCulture,
                "Unable to link every local secret key to the account. Linked {0} of {1} key(s). Try again; keys already linked to another account must be removed from this service first.",
                linkedKeyCount,
                localKeyCount),
            LinkedLocalSecretKeyCount = linkedKeyCount
        };
    }

    private static AccountOperationResult CreateAccountKeyDownloadFailure()
    {
        return new AccountOperationResult
        {
            Success = false,
            ErrorMessage = "Unable to download the account secret keys. Try again before using account sign-in."
        };
    }

    private void MarkCurrentServerAccountLinked(Guid? userAccountId)
    {
        var currentServer = _serverManager.CurrentServer;
        if (currentServer.AccountLinked && currentServer.UserAccountId == userAccountId)
        {
            return;
        }

        currentServer.AccountLinked = true;
        currentServer.UserAccountId = userAccountId;
        _serverManager.Save();
    }

    private async Task StoreRegisteredSecretKeyAsync(RegisterReplyDto reply, bool assignCurrentCharacter)
    {
        if (string.IsNullOrWhiteSpace(reply.SecretKey))
        {
            return;
        }

        var key = NormalizeSecretKey(reply.SecretKey);
        if (!IsValidSecretKey(key))
        {
            return;
        }

        var server = _serverManager.CurrentServer;
        var keyIdx = FindSecretKeyIndex(server, key);
        if (keyIdx == null)
        {
            keyIdx = GetNextSecretKeyIndex(server);
            server.SecretKeys.Add(keyIdx.Value, new SecretKey
            {
                FriendlyName = string.Format(CultureInfo.InvariantCulture, "{0} (registered {1:yyyy-MM-dd})", reply.UID, DateTime.Now),
                Key = key
            });
        }

        if (assignCurrentCharacter)
        {
            await AssignCurrentCharacterToSecretKeyAsync(server, keyIdx.Value).ConfigureAwait(false);
        }

        _serverManager.Save();
    }

    private async Task<AccountSecretKeyImportResult> StoreAccountSecretKeysAsync(IEnumerable<AccountSecretKeyResponse> keys, string? preferredUid, bool assignCurrentCharacter)
    {
        var server = _serverManager.CurrentServer;
        var normalizedPreferredUid = string.IsNullOrWhiteSpace(preferredUid) ? string.Empty : preferredUid.Trim().ToUpperInvariant();
        int? assignmentKeyIdx = null;
        var assignmentRank = int.MinValue;
        var assignmentLastActivityUtc = DateTimeOffset.MinValue;
        var newKeyCount = 0;
        var totalKeyCount = 0;

        foreach (var accountKey in keys)
        {
            var secretKey = NormalizeSecretKey(accountKey.SecretKey);
            if (!IsValidSecretKey(secretKey))
            {
                continue;
            }

            totalKeyCount++;
            var keyIdx = FindSecretKeyIndex(server, secretKey);
            if (keyIdx == null)
            {
                keyIdx = GetNextSecretKeyIndex(server);
                server.SecretKeys.Add(keyIdx.Value, new SecretKey
                {
                    FriendlyName = BuildAccountSecretKeyName(accountKey),
                    Key = secretKey
                });
                newKeyCount++;
            }

            var candidateRank = GetAssignmentRank(accountKey, normalizedPreferredUid);
            var candidateLastActivityUtc = accountKey.LastUsedAtUtc ?? accountKey.CreatedAtUtc;
            if (candidateRank > assignmentRank
                || (candidateRank == assignmentRank && candidateLastActivityUtc > assignmentLastActivityUtc))
            {
                assignmentKeyIdx = keyIdx.Value;
                assignmentRank = candidateRank;
                assignmentLastActivityUtc = candidateLastActivityUtc;
            }
        }

        if (assignCurrentCharacter && assignmentKeyIdx.HasValue)
        {
            await AssignCurrentCharacterToSecretKeyAsync(server, assignmentKeyIdx.Value).ConfigureAwait(false);
        }

        if (newKeyCount > 0 || assignCurrentCharacter)
        {
            _serverManager.Save();
        }

        return new AccountSecretKeyImportResult(totalKeyCount, newKeyCount);
    }

    private static int GetAssignmentRank(AccountSecretKeyResponse key, string preferredUid)
    {
        var rank = string.Equals(key.Source, "generated", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (!string.IsNullOrWhiteSpace(preferredUid)
            && string.Equals(key.Uid, preferredUid, StringComparison.OrdinalIgnoreCase))
        {
            rank += 2;
        }

        return rank;
    }

    private async Task AssignCurrentCharacterToSecretKeyAsync(ServerStorage server, int secretKeyIdx)
    {
        var currentPlayerName = await _dalamudUtilService.GetPlayerNameAsync().ConfigureAwait(false);
        var currentPlayerWorldId = await _dalamudUtilService.GetHomeWorldIdAsync().ConfigureAwait(false);
        var assignedCharacter = server.Authentications.Find(a =>
            string.Equals(a.CharacterName, currentPlayerName, StringComparison.OrdinalIgnoreCase)
            && a.WorldId == currentPlayerWorldId);

        if (assignedCharacter == null)
        {
            server.Authentications.Add(new Authentication
            {
                CharacterName = currentPlayerName,
                WorldId = currentPlayerWorldId,
                SecretKeyIdx = secretKeyIdx
            });
        }
        else
        {
            assignedCharacter.SecretKeyIdx = secretKeyIdx;
        }
    }

    private static IReadOnlyList<string> GetLocalSecretKeys(ServerStorage server)
    {
        return server.SecretKeys.Values
            .Select(key => NormalizeSecretKey(key.Key))
            .Where(IsValidSecretKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static int? FindSecretKeyIndex(ServerStorage server, string secretKey)
    {
        foreach (var item in server.SecretKeys)
        {
            if (string.Equals(NormalizeSecretKey(item.Value.Key), secretKey, StringComparison.Ordinal))
            {
                return item.Key;
            }
        }

        return null;
    }

    private static int GetNextSecretKeyIndex(ServerStorage server)
    {
        return server.SecretKeys.Any() ? server.SecretKeys.Max(p => p.Key) + 1 : 0;
    }

    private static string BuildAccountSecretKeyName(AccountSecretKeyResponse key)
    {
        var uid = string.IsNullOrWhiteSpace(key.Uid) ? "Account" : key.Uid.Trim().ToUpperInvariant();
        var source = string.Equals(key.Source, "uploaded", StringComparison.OrdinalIgnoreCase) ? "linked" : "generated";
        return string.Format(CultureInfo.InvariantCulture, "{0} account {1} key ({2:yyyy-MM-dd})", uid, source, key.CreatedAtUtc.LocalDateTime);
    }

    private static string NormalizeSecretKey(string secretKey)
    {
        return secretKey.Trim().ToUpperInvariant();
    }

    private static bool IsValidSecretKey(string secretKey)
    {
        return secretKey.Length == 64 && secretKey.All(char.IsAsciiHexDigit);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        var responseText = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        responseText = responseText.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(responseText)
            ? $"Request failed ({(int)response.StatusCode})."
            : responseText;
    }

    private Uri GetApiBaseUri()
    {
        return new Uri(_serverManager.CurrentApiUrl
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase));
    }
    
    private sealed class RegisterResponse
    {
        public string link_url { get; set; } = "";
        public string poll_url { get; set; } = "";
    }
    
    private sealed class PollResponse
    {
        public string status { get; set; } = "";
        public string? uid { get; set; }
        
    }

    private sealed class PatreonLinkStartResponse
    {
        public string sessionId { get; set; } = string.Empty;
        public string authorizationUrl { get; set; } = string.Empty;
        public DateTimeOffset expiresAtUtc { get; set; }
    }

    private sealed class PatreonLinkPollResponse
    {
        public string status { get; set; } = string.Empty;
        public string errorMessage { get; set; } = string.Empty;
        public bool isLinked { get; set; }
        public bool isPayingPatron { get; set; }
        public bool hasBenefits { get; set; }
        public bool isCompetitionWinner { get; set; }
        public bool isTestOverride { get; set; }
        public bool isCreatorForCampaign { get; set; }
    }

    private sealed class PatreonStatusResponse
    {
        public bool isLinked { get; set; }
        public bool isPayingPatron { get; set; }
        public bool hasBenefits { get; set; }
        public bool isCompetitionWinner { get; set; }
        public bool isTestOverride { get; set; }
        public bool isCreatorForCampaign { get; set; }
    }

    private sealed record AccountSecretKeyImportResult(int SecretKeyCount, int NewSecretKeyCount);

    private sealed class PasswordLoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string CharaIdent { get; set; } = string.Empty;
    }

    private sealed class AttachPasswordRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    private sealed class LinkAccountKeyRequest
    {
        public string SecretKey { get; set; } = string.Empty;
    }

    private sealed class AccountAuthResponse
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string? WellKnown { get; set; }
        public string Uid { get; set; } = string.Empty;
        public Guid? UserAccountId { get; set; }
        public AccountSecretKeyResponse[] SecretKeys { get; set; } = [];
    }

    private sealed class AccountKeysResponse
    {
        public bool Success { get; set; }
        public Guid? UserAccountId { get; set; }
        public AccountSecretKeyResponse[] SecretKeys { get; set; } = [];
    }

    private sealed class LinkAccountKeyResponse
    {
        public bool Success { get; set; }
        public Guid? UserAccountId { get; set; }
        public string Uid { get; set; } = string.Empty;
        public bool Recoverable { get; set; }
    }

    private sealed class AccountSecretKeyResponse
    {
        public string Uid { get; set; } = string.Empty;
        public string SecretKey { get; set; } = string.Empty;
        public string HashedSecretKey { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset? LastUsedAtUtc { get; set; }
        public DateTimeOffset? RotatesAfterUtc { get; set; }
    }

}
