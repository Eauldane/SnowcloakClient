namespace Snowcloak.Services.Housing;

public readonly record struct HousingPlotLocation(uint WorldId, uint TerritoryId, uint DivisionId, uint WardId, uint PlotId, uint RoomId, bool IsApartment)
{
    public string FullId => $"{WorldId}:{TerritoryId}:{DivisionId}:{WardId}:{PlotId}:{RoomId}";

    public string DisplayName
    {
        get
        {
            if (IsApartment)
            {
                return $"Apartment (Ward {WardId}, Room {RoomId}, {FullId})";
            }

            return $"Ward {WardId} Plot {PlotId} ({FullId})";
        }
    }
}