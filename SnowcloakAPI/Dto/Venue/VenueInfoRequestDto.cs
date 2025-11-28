using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueInfoRequestDto(VenueLocationDto Location)
{
    public VenueLocationDto Location { get; set; } = Location;
}