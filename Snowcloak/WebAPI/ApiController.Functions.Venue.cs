using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Dto.Venue;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public async Task<VenueInfoResponseDto> VenueGetInfoForPlot(VenueInfoRequestDto request)
    {
        CheckConnection();
        return await _snowHub!.InvokeAsync<VenueInfoResponseDto>(nameof(VenueGetInfoForPlot), request).ConfigureAwait(false);
    }
}