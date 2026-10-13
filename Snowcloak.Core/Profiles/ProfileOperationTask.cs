namespace Snowcloak.Core.Profiles;

public static class ProfileOperationTask
{
    public static async Task ObserveAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception) { }
    }
}
