using Dalamud.Utility;
using Snowcloak.API.Dto.Account;
using Snowcloak.API.Routes;
using Snowcloak.Utils;
using System.Net.Http.Json;

namespace Snowcloak.WebAPI;

public sealed partial class AccountRegistrationService
{
    public async Task<RegisterReplyDto> XIVAuth(CancellationToken token)
    {
        var secretKey = GenerateSecretKey();
        var hashedSecretKey = secretKey.GetHash256();
        var playerName = await _dalamudUtilService.GetPlayerNameAsync().ConfigureAwait(false);
        var worldId = (ushort)await _dalamudUtilService.GetHomeWorldIdAsync().ConfigureAwait(false);
        var worldName = _dalamudUtilService.WorldData[worldId];

        var startUri = new Uri(GetApiBaseUri(), XivAuthRegisterStartRoute);
        var startPayload = new XivAuthRegisterStartRequestDto
        {
            HashedSecretKey = hashedSecretKey,
            CharacterName = playerName,
            HomeWorld = worldName
        };

        using var startResponse = await _httpClient.PostAsJsonAsync(startUri, startPayload, token).ConfigureAwait(false);
        if (!startResponse.IsSuccessStatusCode)
        {
            return new RegisterReplyDto { Success = false, ErrorMessage = await ReadErrorAsync(startResponse, token).ConfigureAwait(false) };
        }

        var start = await startResponse.Content.ReadFromJsonAsync<XivAuthRegisterStartReplyDto>(token).ConfigureAwait(false);
        if (start == null || string.IsNullOrWhiteSpace(start.SessionId) || string.IsNullOrWhiteSpace(start.AuthorizationUrl))
        {
            return new RegisterReplyDto { Success = false, ErrorMessage = "Malformed registration response." };
        }

        Util.OpenLink(start.AuthorizationUrl);
        var expiry = start.ExpiresAtUtc > DateTimeOffset.UtcNow ? start.ExpiresAtUtc : DateTimeOffset.UtcNow.AddMinutes(10);
        var pollUri = new Uri(GetApiBaseUri(), XivAuthRegisterPollRoutePrefix + start.SessionId);

        while (DateTimeOffset.UtcNow < expiry)
        {
            token.ThrowIfCancellationRequested();
            using var pollResponse = await _httpClient.GetAsync(pollUri, token).ConfigureAwait(false);
            if (!pollResponse.IsSuccessStatusCode)
            {
                return new RegisterReplyDto { Success = false, ErrorMessage = $"Registration polling failed ({(int)pollResponse.StatusCode})." };
            }

            var poll = await pollResponse.Content.ReadFromJsonAsync<XivAuthRegisterPollReplyDto>(token).ConfigureAwait(false) ?? new XivAuthRegisterPollReplyDto();
            if (poll.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                continue;
            }

            if (poll.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                return new RegisterReplyDto { Success = true, ErrorMessage = string.Empty, UID = poll.Uid, SecretKey = secretKey };
            }

            return new RegisterReplyDto
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(poll.ErrorMessage) ? "XIVAuth registration failed. Please try again." : poll.ErrorMessage
            };
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
