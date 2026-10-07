namespace Snowcloak.Core.EnvironmentSnapshots;

public static class RestorePluginSequence
{
    public static async Task ChangeAsync(IReadOnlyDictionary<string, bool> original, bool reload,
        Func<string, bool> loaded, Func<string, bool, CancellationToken, Task> change, CancellationToken ct)
    {
        string[] order = reload ? ["CustomizePlus", "Penumbra", "Glamourer"] : ["Glamourer", "CustomizePlus", "Penumbra"];
        foreach (var plugin in order)
        {
            ct.ThrowIfCancellationRequested();
            if (!original.TryGetValue(plugin, out var wasLoaded)) continue;
            var desired = reload && wasLoaded;
            if (loaded(plugin) != desired) await change(plugin, desired, ct).ConfigureAwait(false);
            if (loaded(plugin) != desired) throw new IOException("Plugin lifecycle state was not reached: " + plugin);
        }
    }
}
