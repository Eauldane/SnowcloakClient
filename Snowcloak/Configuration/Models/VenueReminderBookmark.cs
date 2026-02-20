namespace Snowcloak.Configuration.Models;

public enum VenueReminderBookmarkScope
{
    Event,
    Venue
}

[Serializable]
public sealed class VenueReminderBookmark
{
    public Guid BookmarkId { get; set; } = Guid.NewGuid();
    public VenueReminderBookmarkScope Scope { get; set; } = VenueReminderBookmarkScope.Event;
    public Guid VenueId { get; set; }
    public string VenueName { get; set; } = string.Empty;
    public Guid? AdvertisementId { get; set; }
    public string EventSummary { get; set; } = string.Empty;
    public DateTime? StartsAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
