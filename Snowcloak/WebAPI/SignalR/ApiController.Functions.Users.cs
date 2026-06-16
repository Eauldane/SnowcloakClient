using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.Mediator;
using System.Globalization;
using System.Text;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public async Task PushCharacterData(CharacterData data, List<UserData> visibleCharacters)
    {
        if (!IsConnected) return;

        try
        {
            Logger.LogDebug("Pushing Character data {hash} to {visible}", data.DataHash, string.Join(", ", visibleCharacters.Select(v => v.AliasOrUID)));
            await PushCharacterDataInternal(data, [.. visibleCharacters]).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Upload operation was cancelled");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during upload of files");
        }
    }

    public async Task UserAddPair(UserDto user)
    {
        if (!IsConnected) return;
        await _snowHub!.SendAsync(nameof(UserAddPair), user).ConfigureAwait(false);
    }

    public async Task UserChatSendMsg(UserDto user, ChatMessage message)
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(UserChatSendMsg), user, message).ConfigureAwait(false);
    }

    public async Task<List<SignedChatMessage>> UserChatGetHistory(UserDto user)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<List<SignedChatMessage>>(nameof(UserChatGetHistory), user).ConfigureAwait(false);
    }

    public async Task UserDelete()
    {
        CheckConnection();
        await _snowHub!.SendAsync(nameof(UserDelete)).ConfigureAwait(false);
        await CreateConnections().ConfigureAwait(false);
    }
    
    public async Task<List<OnlineUserIdentDto>> UserGetPairsInRange(List<string> idents)
    {
        if (!IsConnected) return [];
        return await _snowHub!.InvokeAsync<List<OnlineUserIdentDto>>(nameof(UserGetPairsInRange), idents)
            .ConfigureAwait(false);
    }
    
    public async Task<List<OnlineUserIdentDto>> UserGetOnlinePairs()
    {
        return await _snowHub!.InvokeAsync<List<OnlineUserIdentDto>>(nameof(UserGetOnlinePairs)).ConfigureAwait(false);
    }

    public async Task<List<UserPairDto>> UserGetPairedClients()
    {
        return await _snowHub!.InvokeAsync<List<UserPairDto>>(nameof(UserGetPairedClients)).ConfigureAwait(false);
    }

    public async Task<CharacterProfileDto> CharacterProfileGet(CharacterProfileRequestDto dto)
    {
        if (!IsConnected)
        {
            return new CharacterProfileDto
            {
                Ident = dto.Ident,
                Visibility = dto.Visibility ?? ProfileVisibility.Public,
                DisabledReason = "Not connected.",
            };
        }

        return await _snowHub!.InvokeAsync<CharacterProfileDto>(nameof(CharacterProfileGet), dto).ConfigureAwait(false);
    }

    public async Task<CharacterProfileDto> CharacterProfileGetOwn(ProfileVisibility visibility)
    {
        if (!IsConnected)
        {
            return new CharacterProfileDto
            {
                Visibility = visibility,
                IsOwnProfile = true,
                DisabledReason = "Not connected.",
            };
        }

        return await _snowHub!.InvokeAsync<CharacterProfileDto>(nameof(CharacterProfileGetOwn), visibility).ConfigureAwait(false);
    }

    public async Task<CharacterProfileDto> CharacterProfileSet(CharacterProfileUpdateDto dto)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<CharacterProfileDto>(nameof(CharacterProfileSet), dto).ConfigureAwait(false);
    }

    public async Task CharacterProfileDelete()
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(CharacterProfileDelete)).ConfigureAwait(false);
    }

    public async Task CharacterProfileReport(CharacterProfileReportDto dto)
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(CharacterProfileReport), dto).ConfigureAwait(false);
    }
    
    
    public async Task UserPushData(UserCharaDataMessageDto dto)
    {
        try
        {
            await _snowHub!.InvokeAsync(nameof(UserPushData), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to Push character data");
        }
    }

    public async Task UserRequestData(UserData user)
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserRequestData), user).ConfigureAwait(false);
    }

    public async Task UserSendApplicationReceipt(PairApplicationReceiptRequestDto dto)
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserSendApplicationReceipt), dto).ConfigureAwait(false);
    }

    private async Task SendApplicationReceipt(PairApplicationCompletedMessage message)
    {
        if (!IsConnected)
        {
            return;
        }

        var receipt = new PairApplicationReceiptRequestDto(new UserData(message.UID), message.CharacterData.DataHash.Value, PairApplicationReceiptStatus.Applied);
        await UserSendApplicationReceipt(receipt).ConfigureAwait(false);
    }

    public async Task UserRemovePair(UserDto userDto)
    {
        if (!IsConnected) return;
        await _snowHub!.SendAsync(nameof(UserRemovePair), userDto).ConfigureAwait(false);
    }

    public async Task UserSetPairPermissions(UserPermissionsDto userPermissions)
    {
        await _snowHub!.SendAsync(nameof(UserSetPairPermissions), userPermissions).ConfigureAwait(false);
    }

    public async Task<bool> UserSetVanityId(UserVanityIdDto dto)
    {
        if (!IsConnected) return false;
        var updated = await _snowHub!.InvokeAsync<bool>(nameof(UserSetVanityId), dto).ConfigureAwait(false);
        var connectionDto = _connectionContext.Dto;
        if (!updated || connectionDto == null)
        {
            return updated;
        }

        var currentUser = connectionDto.User;
        var updatedUser = currentUser with
        {
            Alias = dto.VanityId,
            HexString = dto.HexString ?? currentUser.HexString,
            GlowHexString = dto.GlowHexString ?? currentUser.GlowHexString
        };

        var aliasChanged = !string.Equals(currentUser.Alias, updatedUser.Alias, StringComparison.Ordinal);
        var colorChanged = !string.Equals(currentUser.HexString, updatedUser.HexString, StringComparison.Ordinal);
        var glowChanged = !string.Equals(currentUser.GlowHexString, updatedUser.GlowHexString, StringComparison.Ordinal);
        if (!aliasChanged && !colorChanged && !glowChanged)
        {
            return updated;
        }

        _connectionContext = _connectionContext.WithUser(updatedUser);
        Mediator.Publish(new ClearProfileDataMessage(updatedUser));
        if (colorChanged || glowChanged)
        {
            Mediator.Publish(new NameplateRedrawMessage());
        }

        return updated;
    }

    public async Task UserSetPairingOptIn(PairingOptInDto dto)
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserSetPairingOptIn), dto).ConfigureAwait(false);
    }

    public async Task<bool> UserGetPairingOptIn()
    {
        if (!IsConnected) return false;
        return await _snowHub!.InvokeAsync<bool>(nameof(UserGetPairingOptIn)).ConfigureAwait(false);
    }
    
    public async Task UserQueryPairingAvailability(PairingAvailabilityQueryDto query)
    {
        Logger.LogDebug("Querying pairing availability for {count} nearby idents: {idents}",
            query.NearbyIdents?.Count ?? 0,
            string.Join(", ", query.NearbyIdents ?? Array.Empty<string>()));

        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserQueryPairingAvailability), query).ConfigureAwait(false);
    }

    public async Task<bool> UserSubscribePairingAvailability(PairingAvailabilitySubscriptionDto request)
    {
        Logger.LogDebug("Updating pairing availability subscription for world {world} territory {territory}: +{added}/-{removed}",
            request.WorldId,
            request.TerritoryId,
            request.AddedNearbyIdents?.Count ?? 0,
            request.RemovedNearbyIdents?.Count ?? 0);

        if (!IsConnected) return false;
        return await _snowHub!.InvokeAsync<bool>(nameof(UserSubscribePairingAvailability), request).ConfigureAwait(false);
    }

    public async Task UserUnsubscribePairingAvailability()
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserUnsubscribePairingAvailability)).ConfigureAwait(false);
    }

    public async Task UserSendPairRequest(PairingRequestTargetDto dto)
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserSendPairRequest), dto).ConfigureAwait(false);
    }

    public async Task UserRespondToPairRequest(PairingRequestDecisionDto dto)
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserRespondToPairRequest), dto).ConfigureAwait(false);
    }

    private async Task PushCharacterDataInternal(CharacterData character, List<UserData> visibleCharacters)
    {
        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation("Pushing character data for {Hash} to {Characters}", character.DataHash.Value, string.Join(", ", visibleCharacters.Select(c => c.AliasOrUID)));
        }

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            StringBuilder sb = new();
            foreach (var kvp in character.FileReplacements.ToList())
            {
                sb.Append("FileReplacements for ").Append(kvp.Key).Append(": ").Append(kvp.Value.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();
            }
            foreach (var item in character.GlamourerData)
            {
                sb.Append("GlamourerData for ").Append(item.Key).Append(": ").Append(!string.IsNullOrEmpty(item.Value)).AppendLine();
            }
            Logger.LogDebug("Chara data contained: {NewLine} {Data}", Environment.NewLine, sb.ToString());
        }

        await UserPushData(new(visibleCharacters, character)).ConfigureAwait(false);
        Mediator.Publish(new LocalCharacterDataPushedMessage(visibleCharacters, character.DataHash.Value));
    }
}
