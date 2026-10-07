using System.Text.Json.Nodes;

namespace Snowcloak.Core.EnvironmentSnapshots;

public static class PenumbraRestoreSettings
{
    public static bool IsSettings(string plugin, string path) => string.Equals(plugin, "Penumbra", StringComparison.Ordinal)
        && (string.Equals(path, "@config.json", StringComparison.Ordinal) || string.Equals(path, "config/penumbra.json", StringComparison.OrdinalIgnoreCase));

    public static bool NeedsRoot(string json, string root)
    {
        var config = Read(json);
        return !string.Equals(config["ModDirectory"]?.GetValue<string>(), root, StringComparison.OrdinalIgnoreCase);
    }

    public static JsonObject WithRoot(string json, string root)
    {
        var config = Read(json);
        config["ModDirectory"] = root;
        return config;
    }

    public static string ExistingPath(string pluginConfigs)
    {
        foreach (var relative in new[] { "Penumbra/config/penumbra.json", "Penumbra.json" })
        {
            var path = SnapshotSafety.Destination(pluginConfigs, relative);
            if (File.Exists(path)) return path;
        }
        throw new InvalidDataException("Penumbra settings are missing! Make sure you've configured Penumbra with a path.");
    }

    private static JsonObject Read(string json)
    {
        var config = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Penumbra settings are not a recognized object.");
        if (!config.ContainsKey("ModDirectory")) throw new InvalidDataException("The Penumbra path is invalid! Set it somewhere that exists.");
        return config;
    }
}
