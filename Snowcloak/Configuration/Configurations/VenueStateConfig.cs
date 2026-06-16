using Snowcloak.Configuration.Models;

namespace Snowcloak.Configuration.Configurations;

public class VenueStateConfig : ISnowcloakConfiguration
{
    public List<VenueAutoJoinedSyncshell> AutoJoinedVenueSyncshells { get; set; } = [];
    public List<VenueReminderBookmark> VenueReminderBookmarks { get; set; } = [];
    public int Version { get; set; }
}
