using Snowcloak.API.Dto.Account;
using System.Net.Http.Json;

namespace Snowcloak.WebAPI;

public sealed partial class AccountRegistrationService
{
    public async Task<AccountOperationResult> LoginWithPassword(string username, string password, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

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

        var payload = new PasswordLoginRequestDto
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

        var account = await response.Content.ReadFromJsonAsync<AccountAuthReplyDto>(cancellationToken: token).ConfigureAwait(false);
        if (account == null || !account.Success)
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = account?.ErrorMessage ?? "Server returned an invalid account login response."
            };
        }

        var linkedCount = await LinkLocalSecretKeysAsync(localKeysToLink, token, account.Token).ConfigureAwait(false);
        if (linkedCount != localKeysToLink.Length)
        {
            return CreateLocalKeyLinkFailure(localKeysToLink.Length, linkedCount);
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
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        var localKeysToLink = GetLocalSecretKeys(_serverManager.CurrentServer);
        using var request = await CreateAuthorizedRequest(HttpMethod.Post, new Uri(GetApiBaseUri(), AccountPasswordRoute), token).ConfigureAwait(false);
        request.Content = JsonContent.Create(new AttachPasswordRequestDto
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

        var account = await response.Content.ReadFromJsonAsync<AccountAuthReplyDto>(cancellationToken: token).ConfigureAwait(false);
        if (account == null || !account.Success)
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = account?.ErrorMessage ?? "Server returned an invalid account setup response."
            };
        }

        var linkedCount = await LinkLocalSecretKeysAsync(localKeysToLink, token, account.Token).ConfigureAwait(false);
        if (linkedCount != localKeysToLink.Length)
        {
            return CreateLocalKeyLinkFailure(localKeysToLink.Length, linkedCount);
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

        var account = await response.Content.ReadFromJsonAsync<AccountUidReplyDto>(cancellationToken: token).ConfigureAwait(false);
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
}
