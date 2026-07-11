using Snowcloak.API.Dto.Account;
using Snowcloak.API.Routes;
using Snowcloak.Utils;
using System.Net.Http.Json;

namespace Snowcloak.WebAPI;

public sealed partial class AccountRegistrationService
{
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
