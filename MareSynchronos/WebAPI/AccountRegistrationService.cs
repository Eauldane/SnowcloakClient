using MareSynchronos.API.Dto.Account;
using MareSynchronos.API.Routes;
using MareSynchronos.Services;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.Utils;
using MareSynchronos.WebAPI.SignalR;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using Dalamud.Utility;
using System.Net;

namespace MareSynchronos.WebAPI;

public sealed class AccountRegistrationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AccountRegistrationService> _logger;
    private readonly ServerConfigurationManager _serverManager;
    private readonly DalamudUtilService _dalamudUtilService;

    private string GenerateSecretKey()
    {
        return Convert.ToHexString(SHA256.HashData(RandomNumberGenerator.GetBytes(64)));
    }

    public AccountRegistrationService(ILogger<AccountRegistrationService> logger, DalamudUtilService dalamudUtilService, ServerConfigurationManager serverManager)
    {
        _logger = logger;
        _serverManager = serverManager;
        _dalamudUtilService = dalamudUtilService;
        _httpClient = new(
            new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            }
        );
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MareSynchronos", ver!.Major + "." + ver!.Minor + "." + ver!.Build));
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
        const int maxAttempts = 600 / 15; // Try once every 15 seconds for 10 minutes
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
            await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
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

        Uri postUri = MareAuth.AuthRegisterV2FullPath(new Uri(_serverManager.CurrentApiUrl
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

}

