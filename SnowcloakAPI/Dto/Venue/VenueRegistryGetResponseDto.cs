using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueRegistryGetResponseDto(bool Success, VenueRegistryEntryDto? Registry)
{
    public bool Success { get; set; } = Success;
    public VenueRegistryEntryDto? Registry { get; set; } = Registry;
    public string? ErrorMessage { get; set; }
}
