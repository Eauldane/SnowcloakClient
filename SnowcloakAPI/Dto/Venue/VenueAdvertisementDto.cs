using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueAdvertisementDto(Guid Id)
{
    public Guid Id { get; set; } = Id;
    public string? Text { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string? BannerFileHash { get; set; }
    public string? BannerBase64 { get; set; }
    public int? BannerWidth { get; set; }
    public int? BannerHeight { get; set; }
    public bool IsActive { get; set; } = true;
}
