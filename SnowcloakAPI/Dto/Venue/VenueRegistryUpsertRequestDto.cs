using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueRegistryUpsertRequestDto(string SyncshellGid, string VenueName)
{
    public string SyncshellGid { get; set; } = SyncshellGid;
    public string VenueName { get; set; } = VenueName;
    public string? VenueDescription { get; set; }
    public string? VenueWebsite { get; set; }
    public string? VenueHost { get; set; }
    public bool IsListed { get; set; } = true;
}
