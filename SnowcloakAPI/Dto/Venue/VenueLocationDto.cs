using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueLocationDto(uint WorldId, uint TerritoryId, uint DivisionId, uint WardId, uint PlotId, uint RoomId, bool IsApartment)
{
    public uint WorldId { get; set; } = WorldId;
    public uint TerritoryId { get; set; } = TerritoryId;
    public uint DivisionId { get; set; } = DivisionId;
    public uint WardId { get; set; } = WardId;
    public uint PlotId { get; set; } = PlotId;
    public uint RoomId { get; set; } = RoomId;
    public bool IsApartment { get; set; } = IsApartment;
}