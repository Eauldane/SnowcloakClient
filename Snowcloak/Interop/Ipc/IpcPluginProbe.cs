using Dalamud.Plugin;

namespace Snowcloak.Interop.Ipc;

internal static class IpcPluginProbe
{
    public static IpcPluginState Find(IDalamudPluginInterface pluginInterface, string internalName)
    {
        try
        {
            var plugin = pluginInterface.InstalledPlugins
                .Where(p => string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => !p.IsLoaded)
                .ThenByDescending(p => p.Version)
                .FirstOrDefault();

            return plugin == null
                ? IpcPluginState.Missing
                : new IpcPluginState(plugin.Version, plugin.IsLoaded);
        }
        catch
        {
            return IpcPluginState.Missing;
        }
    }
}

internal readonly record struct IpcPluginState(Version? Version, bool IsLoaded)
{
    public static IpcPluginState Missing => new(null, false);
    public bool IsInstalled => Version != null;
}
