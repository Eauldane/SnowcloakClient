namespace Snowcloak.Configuration.Models;

[Serializable]
public sealed class VenueAutoJoinedSyncshell
{
    public string GroupGid { get; set; } = string.Empty;
    public string? GroupAlias { get; set; }
    public string? GroupHexString { get; set; }
    public uint WorldId { get; set; }
    public uint TerritoryId { get; set; }
    public uint DivisionId { get; set; }
    public uint WardId { get; set; }
    public uint PlotId { get; set; }
    public uint RoomId { get; set; }
    public bool IsApartment { get; set; }
    public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LeaveAfterUtc { get; set; }
    public bool LeaveWarningShown { get; set; }
}
