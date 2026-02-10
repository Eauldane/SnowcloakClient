using Snowcloak.API.Data;
using Snowcloak.API.Dto.User;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
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

    public async Task<UserProfileDto> UserGetProfile(UserProfileRequestDto dto)
    {
        if (!IsConnected)
        {
            var fallbackUser = dto.User ?? new UserData(dto.Ident ?? string.Empty);
            return new UserProfileDto(fallbackUser, false, null, null, null, dto.Visibility);
        }
        return await _snowHub!.InvokeAsync<UserProfileDto>(nameof(UserGetProfile), dto).ConfigureAwait(false);
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

    public async Task UserRemovePair(UserDto userDto)
    {
        if (!IsConnected) return;
        await _snowHub!.SendAsync(nameof(UserRemovePair), userDto).ConfigureAwait(false);
    }

    public async Task UserReportProfile(UserProfileReportDto userDto)
    {
        if (!IsConnected) return;
        await _snowHub!.SendAsync(nameof(UserReportProfile), userDto).ConfigureAwait(false);
    }

    public async Task UserSetPairPermissions(UserPermissionsDto userPermissions)
    {
        await _snowHub!.SendAsync(nameof(UserSetPairPermissions), userPermissions).ConfigureAwait(false);
    }

    public async Task UserSetProfile(UserProfileDto userDescription)
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserSetProfile), userDescription).ConfigureAwait(false);
    }
    
    public async Task<bool> UserSetVanityId(UserVanityIdDto vanityId)
    {
        if (!IsConnected) return false;
        return await _snowHub!.InvokeAsync<bool>(nameof(UserSetVanityId), vanityId).ConfigureAwait(false);
    }

    public async Task UserSetTextureCompressionPreference(TextureCompressionPreferenceDto preference)
    {
        if (!IsConnected) return;
        try
        {
            await _snowHub!.InvokeAsync(nameof(UserSetTextureCompressionPreference), preference).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update texture compression preference");
        }
    }

    public async Task UserSetTextureCompressionMapping(TextureCompressionMappingBatchDto mapping)
    {
        if (!IsConnected) return;
        try
        {
            await _snowHub!.InvokeAsync(nameof(UserSetTextureCompressionMapping), mapping).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update texture compression mapping");
        }
    }
    
    public async Task UserSetPairingOptIn(PairingOptInDto optInDto)
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserSetPairingOptIn), optInDto).ConfigureAwait(false);
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

    public async Task<bool> UserSubscribePairingAvailability(PairingAvailabilitySubscriptionDto subscription)
    {
        Logger.LogDebug("Updating pairing availability subscription for world {world} territory {territory}: +{added}/-{removed}",
            subscription.WorldId,
            subscription.TerritoryId,
            subscription.AddedNearbyIdents?.Count ?? 0,
            subscription.RemovedNearbyIdents?.Count ?? 0);

        if (!IsConnected) return false;
        return await _snowHub!.InvokeAsync<bool>(nameof(UserSubscribePairingAvailability), subscription).ConfigureAwait(false);
    }

    public async Task UserUnsubscribePairingAvailability()
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserUnsubscribePairingAvailability)).ConfigureAwait(false);
    }

    public async Task UserSendPairRequest(PairingRequestTargetDto targetDto)
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserSendPairRequest), targetDto).ConfigureAwait(false);
    }

    public async Task UserRespondToPairRequest(PairingRequestDecisionDto decisionDto)
    {
        if (!IsConnected) return;
        await _snowHub!.InvokeAsync(nameof(UserRespondToPairRequest), decisionDto).ConfigureAwait(false);
    }

    private async Task PushCharacterDataInternal(CharacterData character, List<UserData> visibleCharacters)
    {
        Logger.LogInformation("Pushing character data for {hash} to {charas}", character.DataHash.Value, string.Join(", ", visibleCharacters.Select(c => c.AliasOrUID)));
        StringBuilder sb = new();
        foreach (var kvp in character.FileReplacements.ToList())
        {
            sb.AppendLine($"FileReplacements for {kvp.Key}: {kvp.Value.Count}");
        }
        foreach (var item in character.GlamourerData)
        {
            sb.AppendLine($"GlamourerData for {item.Key}: {!string.IsNullOrEmpty(item.Value)}");
        }
        Logger.LogDebug("Chara data contained: {nl} {data}", Environment.NewLine, sb.ToString());

        await UserPushData(new(visibleCharacters, character)).ConfigureAwait(false);
    }
}
