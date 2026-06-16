namespace Snowcloak.Core.Venue;


public static class VenueReminderSchedule
{

    public static DateTime ResolveReminderStartUtc(DateTime startsAt, TimeSpan reminderWindow)
    {
        return AsUtc(startsAt) - reminderWindow;
    }


    public static DateTime ResolveReminderEndUtc(DateTime startsAt, DateTime? endsAt, TimeSpan defaultRunningWindow)
    {
        var startUtc = AsUtc(startsAt);
        if (endsAt.HasValue)
        {
            var endUtc = AsUtc(endsAt.Value);
            // A malformed/inverted end time must never collapse the window below the start.
            return endUtc < startUtc ? startUtc : endUtc;
        }

        return startUtc + defaultRunningWindow;
    }

    public static bool IsWithinReminderWindow(DateTime now, DateTime startsAt, DateTime? endsAt,
        TimeSpan reminderWindow, TimeSpan defaultRunningWindow)
    {
        var nowUtc = AsUtc(now);
        var reminderStart = ResolveReminderStartUtc(startsAt, reminderWindow);
        var reminderEnd = ResolveReminderEndUtc(startsAt, endsAt, defaultRunningWindow);
        return nowUtc >= reminderStart && nowUtc <= reminderEnd;
    }

    public static bool IsRunning(DateTime now, DateTime startsAt)
    {
        return AsUtc(now) >= AsUtc(startsAt);
    }

    private static DateTime AsUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }
}
