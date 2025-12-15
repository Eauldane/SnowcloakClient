using MessagePack;
using Snowcloak.API.Dto.Group;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueSyncshellDto(string VenueName, string LocationDisplay, GroupPasswordDto JoinInfo)
{
    public string VenueName { get; set; } = VenueName;
    public string LocationDisplay { get; set; } = LocationDisplay;
    public string? VenueDescription { get; set; }
    public string? VenueWebsite { get; set; }
    public string? VenueHost { get; set; }
    public string? HexString { get; set; }
    public GroupPasswordDto JoinInfo { get; set; } = JoinInfo;
}