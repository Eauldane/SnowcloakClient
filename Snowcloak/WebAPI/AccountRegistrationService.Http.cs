using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto;
using Snowcloak.API.Dto.Account;
using Snowcloak.API.Routes;
using Snowcloak.Utils;
using Snowcloak.WebAPI.SignalR;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Snowcloak.WebAPI;

public sealed partial class AccountRegistrationService
{
    private static readonly Action<ILogger, Exception?> LogAccountKeyTokenFallback =
        LoggerMessage.Define(LogLevel.Debug, new EventId(1, nameof(LogAccountKeyTokenFallback)),
            "Unable to authorise account request with the current character key; trying saved account keys");
    private static readonly Action<ILogger, HttpStatusCode, Exception?> LogAccountKeysFetchFailed =
        LoggerMessage.Define<HttpStatusCode>(LogLevel.Warning, new EventId(2, nameof(LogAccountKeysFetchFailed)),
            "Failed to fetch account keys after password setup: {Status}");

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
        catch (InvalidOperationException ex)
        {
            LogAccountKeyTokenFallback(_logger, ex);
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
            using var content = new FormUrlEncodedContent([
                new("auth", secretKey.GetHash256()),
                new("charaIdent", charaIdent)
            ]);
            using var response = await _httpClient.PostAsync(tokenUri, content, token).ConfigureAwait(false);
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

    private async Task<AccountKeysReplyDto?> GetAccountKeys(CancellationToken token, string? jwt = null)
    {
        using var request = string.IsNullOrWhiteSpace(jwt)
            ? await CreateAuthorizedRequest(HttpMethod.Get, new Uri(GetApiBaseUri(), AccountKeysRoute), token).ConfigureAwait(false)
            : CreateBearerRequest(HttpMethod.Get, new Uri(GetApiBaseUri(), AccountKeysRoute), jwt);
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LogAccountKeysFetchFailed(_logger, response.StatusCode, null);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<AccountKeysReplyDto>(cancellationToken: token).ConfigureAwait(false);
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
}
