using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueAdvertisementUpsertResponseDto(bool Success, bool WasUpdate)
{
    public bool Success { get; set; } = Success;
    public bool WasUpdate { get; set; } = WasUpdate;
    public VenueAdvertisementDto? Advertisement { get; set; }
    public string? ErrorMessage { get; set; }
}
