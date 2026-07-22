using Snowcloak.API.Dto.Account;
using Snowcloak.Configuration.Models;
using Snowcloak.Core.Accounts;
using System.Globalization;
using System.Net.Http.Json;

namespace Snowcloak.WebAPI;

public sealed partial class AccountRegistrationService
{
    public async Task<AccountOperationResult> LinkLocalSecretKey(string secretKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(secretKey);

        var normalizedSecretKey = NormalizeSecretKey(secretKey);
        if (!IsValidSecretKey(normalizedSecretKey))
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = "The selected secret key is not a valid 64-character hexadecimal key."
            };
        }

        using var request = await CreateAccountAuthorizedRequest(HttpMethod.Post, new Uri(GetApiBaseUri(), AccountKeyLinkRoute), token).ConfigureAwait(false);
        request.Content = JsonContent.Create(new LinkAccountKeyRequestDto
        {
            SecretKey = normalizedSecretKey
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

        var payload = await response.Content.ReadFromJsonAsync<LinkAccountKeyReplyDto>(cancellationToken: token).ConfigureAwait(false);
        if (payload?.Success != true)
        {
            return new AccountOperationResult
            {
                Success = false,
                ErrorMessage = payload?.ErrorMessage ?? "Server returned an invalid secret key link response."
            };
        }

        MarkCurrentServerAccountLinked(payload.UserAccountId);
        return new AccountOperationResult
        {
            Success = true,
            Uid = payload.Uid,
            UserAccountId = payload.UserAccountId,
            SecretKeyCount = 1
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

    private async Task<AccountSecretKeyImportResult> StoreAccountSecretKeysAsync(IEnumerable<AccountSecretKeyDto> keys, string? preferredUid, bool assignCurrentCharacter)
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

            var candidateRank = AccountSecretKeySelectionPolicy.GetAssignmentRank(accountKey, normalizedPreferredUid);
            var candidateLastActivityUtc = AccountSecretKeySelectionPolicy.GetActivityTime(accountKey);
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

    private static string[] GetLocalSecretKeys(ServerStorage server)
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
        return server.SecretKeys.Count != 0 ? server.SecretKeys.Max(p => p.Key) + 1 : 0;
    }

    private static string BuildAccountSecretKeyName(AccountSecretKeyDto key)
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

    private sealed record AccountSecretKeyImportResult(int SecretKeyCount, int NewSecretKeyCount);
}
