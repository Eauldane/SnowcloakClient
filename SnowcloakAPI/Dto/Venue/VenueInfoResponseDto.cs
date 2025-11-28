using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueInfoResponseDto(bool HasVenue, VenueSyncshellDto? Venue)
{
    public bool HasVenue { get; set; } = HasVenue;
    public VenueSyncshellDto? Venue { get; set; } = Venue;
}