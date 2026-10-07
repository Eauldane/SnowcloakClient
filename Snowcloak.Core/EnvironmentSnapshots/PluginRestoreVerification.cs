using System.Text.Json.Nodes;

namespace Snowcloak.Core.EnvironmentSnapshots;

public static class PluginRestoreVerification
{
    public static bool CompareAfterReload(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length < 2 || parts[0] is not ("Penumbra" or "Glamourer" or "CustomizePlus")) return true;
        if (relative.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return false;
        return relative is not ("Penumbra/mod_filesystem/selected_nodes.json"
            or "CustomizePlus/profile_filesystem/selected_nodes.json"
            or "CustomizePlus/template_filesystem/selected_nodes.json"
            or "Glamourer/design_filesystem/selected_nodes.json"
            or "Penumbra/config/ephemeral.json"
            or "Glamourer/ephemeral_config.json"
            or "Glamourer/unlocks_items.json"
            or "Glamourer/unlocks_customize.json");
    }

    public static bool EquivalentJson(string relative, string expected, string actual)
    {
        var saved = JsonNode.Parse(expected);
        var current = JsonNode.Parse(actual);
        if (relative is "Penumbra/config/penumbra.json" or "Penumbra.json")
        {
            // Penumbra updates its save timestamp without changing restored settings.
            if (saved is JsonObject savedConfig) savedConfig.Remove("Timestamp");
            if (current is JsonObject currentConfig) currentConfig.Remove("Timestamp");
        }
        return JsonNode.DeepEquals(saved, current);
    }
}
