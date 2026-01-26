using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueRegistryListOwnedRequestDto(int Skip = 0, int Take = 50)
{
    public int Skip { get; set; } = Skip;
    public int Take { get; set; } = Take;
    public bool IncludeAds { get; set; } = true;
    public bool IncludeUnlisted { get; set; } = true;
}
