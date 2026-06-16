using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Account;
using Snowcloak.Core.Accounts;
using System.Net.Http.Json;
using System.Text.Json;

namespace Snowcloak.WebAPI;

public sealed partial class AccountRegistrationService
{
    private static readonly Action<ILogger, Exception?> LogPatreonStatusFetchFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, nameof(LogPatreonStatusFetchFailed)),
            "Failed to fetch Patreon status");
    private static readonly Action<ILogger, Exception?> LogPatreonLoginFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(4, nameof(LogPatreonLoginFailed)),
            "Patreon login failed");

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

            var payload = await response.Content.ReadFromJsonAsync<PatreonStatusReplyDto>(cancellationToken: token).ConfigureAwait(false) ?? new PatreonStatusReplyDto();
            return new PatreonStatusResult
            {
                Success = true,
                Entitlements = AccountEntitlementMapper.FromPatreonStatus(payload)
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return CreatePatreonStatusFailure(ex);
        }
        catch (InvalidOperationException ex)
        {
            return CreatePatreonStatusFailure(ex);
        }
        catch (JsonException ex)
        {
            return CreatePatreonStatusFailure(ex);
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            return CreatePatreonStatusFailure(ex);
        }
    }

    private PatreonStatusResult CreatePatreonStatusFailure(Exception ex)
    {
        LogPatreonStatusFetchFailed(_logger, ex);
        return new PatreonStatusResult
        {
            Success = false,
            ErrorMessage = "Unable to check Patreon status right now."
        };
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

            var startPayload = await startResponse.Content.ReadFromJsonAsync<PatreonLinkStartReplyDto>(cancellationToken: token).ConfigureAwait(false);
            if (startPayload == null || string.IsNullOrWhiteSpace(startPayload.SessionId) || string.IsNullOrWhiteSpace(startPayload.AuthorizationUrl))
            {
                return new PatreonLoginResult
                {
                    Success = false,
                    ErrorMessage = "Server returned an invalid Patreon login response."
                };
            }

            Util.OpenLink(startPayload.AuthorizationUrl);

            var expiry = startPayload.ExpiresAtUtc > DateTimeOffset.UtcNow
                ? startPayload.ExpiresAtUtc
                : DateTimeOffset.UtcNow.AddMinutes(10);
            var pollUri = new Uri(GetApiBaseUri(), PatreonPollRoutePrefix + startPayload.SessionId);

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

                var pollPayload = await pollResponse.Content.ReadFromJsonAsync<PatreonLinkPollReplyDto>(cancellationToken: token).ConfigureAwait(false) ?? new PatreonLinkPollReplyDto();
                if (pollPayload.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    continue;
                }

                if (pollPayload.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                {
                    return new PatreonLoginResult
                    {
                        Success = true,
                        Entitlements = AccountEntitlementMapper.FromPatreonLinkPoll(pollPayload)
                    };
                }

                return new PatreonLoginResult
                {
                    Success = false,
                    ErrorMessage = string.IsNullOrWhiteSpace(pollPayload.ErrorMessage)
                        ? "Patreon login failed. Please try again."
                        : pollPayload.ErrorMessage
                };
            }

            return new PatreonLoginResult
            {
                Success = false,
                ErrorMessage = "Timed out waiting for Patreon login to complete."
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return CreatePatreonLoginFailure(ex);
        }
        catch (InvalidOperationException ex)
        {
            return CreatePatreonLoginFailure(ex);
        }
        catch (JsonException ex)
        {
            return CreatePatreonLoginFailure(ex);
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            return CreatePatreonLoginFailure(ex);
        }
    }

    private PatreonLoginResult CreatePatreonLoginFailure(Exception ex)
    {
        LogPatreonLoginFailed(_logger, ex);
        return new PatreonLoginResult
        {
            Success = false,
            ErrorMessage = "Unable to complete Patreon login right now."
        };
    }
}
