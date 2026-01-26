using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueRegistryListResponseDto(List<VenueRegistryEntryDto> Registries)
{
    public List<VenueRegistryEntryDto> Registries { get; set; } = Registries;
}
