using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueAdvertisementUpsertRequestDto(Guid RegistryId, Guid? AdvertisementId)
{
    public Guid RegistryId { get; set; } = RegistryId;
    public Guid? AdvertisementId { get; set; } = AdvertisementId;
    public string? Text { get; set; }
    public string? BannerFileHash { get; set; }
    public int? BannerWidth { get; set; }
    public int? BannerHeight { get; set; }
    public bool IsActive { get; set; } = true;
}
