using Dalamud.Utility;
using Snowcloak.API.Dto.Account;
using Snowcloak.API.Routes;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using Snowcloak.WebAPI.SignalR;
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

    private const string PatreonStartRoute = "/auth/patreon/link/start";
    private const string PatreonPollRoutePrefix = "/auth/patreon/link/poll/";
    private const string PatreonStatusRoute = "/auth/patreon/status";

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

        var result = await _httpClient.PostAsync(postUri, new FormUrlEncodedContent([
            new("hashedSecretKey", hashedSecretKey)
        ]), token).ConfigureAwait(false);
        result.EnsureSuccessStatusCode();

        var response = await result.Content.ReadFromJsonAsync<RegisterReplyV2Dto>(token).ConfigureAwait(false) ?? new();

        return new RegisterReplyDto()
        {
            Success = response.Success,
            ErrorMessage = response.ErrorMessage,
            UID = response.UID,
            SecretKey = secretKey
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

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return request;
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

}
