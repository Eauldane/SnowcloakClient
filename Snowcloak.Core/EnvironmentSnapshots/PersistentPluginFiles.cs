namespace Snowcloak.Core.EnvironmentSnapshots;

public static class PersistentPluginFiles
{
    public static bool Include(string relative)
    {
        var segments = relative.Split('/');
        if (segments.Any(s => s.StartsWith("temporary", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("remote", StringComparison.OrdinalIgnoreCase)
            || s.Equals("cache", StringComparison.OrdinalIgnoreCase)
            || s.Equals("logs", StringComparison.OrdinalIgnoreCase)
            || s.Equals("backups", StringComparison.OrdinalIgnoreCase))) return false;
        var name = segments[^1];
        if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || name is "ephemeral.json" or "ephemeral_config.json") return false;
        if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return false;
        var rotated = name.LastIndexOf(".log.", StringComparison.OrdinalIgnoreCase);
        return rotated < 0 || !name[(rotated + 5)..].All(char.IsAsciiDigit);
    }
}
