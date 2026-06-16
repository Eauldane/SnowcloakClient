using Dalamud.Utility;
using Snowcloak.API.Dto.Account;
using Snowcloak.API.Routes;
using Snowcloak.Utils;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Snowcloak.WebAPI;

public sealed partial class AccountRegistrationService
{
    public async Task<RegisterReplyDto> XIVAuth(CancellationToken token)
    {
        var secretKey = GenerateSecretKey();
        var hashedSecretKey = secretKey.GetHash256();
        var playerName = _dalamudUtilService.GetPlayerNameAsync().GetAwaiter().GetResult();
        var worldId = (ushort)_dalamudUtilService.GetHomeWorldIdAsync().GetAwaiter().GetResult();
        var worldName = _dalamudUtilService.WorldData[worldId];

        var sessionID = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var handshakeUri = new Uri("https://account.snowcloak-sync.com/register");
        var handshakePayload = new { session_id = sessionID, hashed_secret = hashedSecretKey, character_name = playerName, home_world = worldName };
        var handshakeResponse = await _httpClient.PostAsJsonAsync(handshakeUri, handshakePayload, token).ConfigureAwait(false);
        handshakeResponse.EnsureSuccessStatusCode();
        using var registerStream = await handshakeResponse.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var register = await JsonDocument.ParseAsync(registerStream, cancellationToken: token).ConfigureAwait(false);
        var linkUrl = register.RootElement.TryGetProperty("link_url", out var linkUrlProperty) ? linkUrlProperty.GetString() : null;
        var pollUrl = register.RootElement.TryGetProperty("poll_url", out var pollUrlProperty) ? pollUrlProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(linkUrl) || string.IsNullOrWhiteSpace(pollUrl))
        {
            return new RegisterReplyDto { Success = false, ErrorMessage = "Malformed registration response." };
        }

        Util.OpenLink(linkUrl);
        const int maxAttempts = 600 / 5;
        var pollUri = new Uri(pollUrl);
        for (var i = 0; i < maxAttempts; i++)
        {
            token.ThrowIfCancellationRequested();
            using var resp = await _httpClient.GetAsync(pollUri, token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Gone)
            {
                return new RegisterReplyDto
                {
                    Success = false, ErrorMessage = "Registration session expired. Please try again."
                };
            }

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                using var pollStream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var pollPayload = await JsonDocument.ParseAsync(pollStream, cancellationToken: token).ConfigureAwait(false);
                var status = pollPayload.RootElement.TryGetProperty("status", out var statusProperty) ? statusProperty.GetString() : null;
                if (status?.Equals("bound", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var uid = pollPayload.RootElement.TryGetProperty("uid", out var uidProperty) ? uidProperty.GetString() : null;
                    return new RegisterReplyDto
                    {
                        Success = true, ErrorMessage = string.Empty, UID = uid ?? string.Empty, SecretKey = secretKey
                    };
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }

        return new RegisterReplyDto
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

        var postUri = SnowAuth.AuthRegisterV2FullPath(new Uri(_serverManager.CurrentApiUrl
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)));

        using var content = new FormUrlEncodedContent([
            new("hashedSecretKey", hashedSecretKey)
        ]);
        using var result = await _httpClient.PostAsync(postUri, content, token).ConfigureAwait(false);
        if (!result.IsSuccessStatusCode)
        {
            return new RegisterReplyDto
            {
                Success = false,
                ErrorMessage = await ReadErrorAsync(result, token).ConfigureAwait(false)
            };
        }

        var response = await result.Content.ReadFromJsonAsync<RegisterReplyV2Dto>(token).ConfigureAwait(false) ?? new();

        return new RegisterReplyDto
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

}
