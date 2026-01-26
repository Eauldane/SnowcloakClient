using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueAdvertisementDeleteRequestDto(Guid RegistryId, Guid AdvertisementId)
{
    public Guid RegistryId { get; set; } = RegistryId;
    public Guid AdvertisementId { get; set; } = AdvertisementId;
}
