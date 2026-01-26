using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Venue;
using System.Text.Json;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    private static readonly JsonSerializerOptions VenueLogJsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<VenueInfoResponseDto> VenueGetInfoForPlot(VenueInfoRequestDto request)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<VenueInfoResponseDto>(nameof(VenueGetInfoForPlot), request).ConfigureAwait(false);
    }
    
    public async Task<VenueRegistrationResponseDto> VenueRegister(VenueRegistrationRequestDto request)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<VenueRegistrationResponseDto>(nameof(VenueRegister), request).ConfigureAwait(false);
    }

    public async Task<VenueRegistryGetResponseDto> VenueRegistryGet(VenueRegistryGetRequestDto request)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<VenueRegistryGetResponseDto>(nameof(VenueRegistryGet), request).ConfigureAwait(false);
    }

    public async Task<VenueRegistryListResponseDto> VenueRegistryList(VenueRegistryListRequestDto request)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<VenueRegistryListResponseDto>(nameof(VenueRegistryList), request).ConfigureAwait(false);
    }

    public async Task<VenueRegistryListResponseDto> VenueRegistryListOwned(VenueRegistryListOwnedRequestDto request)
    {
        CheckConnection();
        var response = await _snowHub!.InvokeAsync<VenueRegistryListResponseDto>(nameof(VenueRegistryListOwned), request).ConfigureAwait(false);
        Logger.LogInformation("VenueRegistryListOwned response: {Response}", JsonSerializer.Serialize(response, VenueLogJsonOptions));
        return response;
    }

    public async Task<VenueRegistryUpsertResponseDto> VenueRegistryUpsert(VenueRegistryUpsertRequestDto request)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<VenueRegistryUpsertResponseDto>(nameof(VenueRegistryUpsert), request).ConfigureAwait(false);
    }

    public async Task<VenueAdvertisementUpsertResponseDto> VenueAdvertisementUpsert(VenueAdvertisementUpsertRequestDto request)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<VenueAdvertisementUpsertResponseDto>(nameof(VenueAdvertisementUpsert), request).ConfigureAwait(false);
    }

    public async Task<VenueAdvertisementDeleteResponseDto> VenueAdvertisementDelete(VenueAdvertisementDeleteRequestDto request)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<VenueAdvertisementDeleteResponseDto>(nameof(VenueAdvertisementDelete), request).ConfigureAwait(false);
    }
}
