using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueRegistryListOwnedResponseDto(List<VenueRegistryEntryDto> Registries)
{
    public List<VenueRegistryEntryDto> Registries { get; set; } = Registries;
}
