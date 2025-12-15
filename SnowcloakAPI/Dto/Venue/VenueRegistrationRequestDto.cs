using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueRegistrationRequestDto(
    VenueLocationDto Location,
    string SyncshellGid,
    string VenueName)
{
    public VenueLocationDto Location { get; set; } = Location;
    public string SyncshellGid { get; set; } = SyncshellGid;
    public string VenueName { get; set; } = VenueName;
    public string? VenueDescription { get; set; }
    public string? VenueWebsite { get; set; }
    public string? VenueHost { get; set; }
    public bool IsFreeCompanyPlot { get; set; }
    public string? FreeCompanyTag { get; set; }
    public string? OwnerName { get; set; }
    public string? Alias { get; set; }
    public string? HexString { get; set; }
}
