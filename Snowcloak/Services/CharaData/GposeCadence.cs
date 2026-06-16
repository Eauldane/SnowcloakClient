namespace Snowcloak.Services.CharaData;

internal static class GposeCadence
{
    public static readonly TimeSpan PoseActiveTick = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan PoseIdleTick = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan PoseActiveWindow = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan WorldTickGpose = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan WorldTickOverworld = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan WispLerpDuration = TimeSpan.FromSeconds(1);
}
