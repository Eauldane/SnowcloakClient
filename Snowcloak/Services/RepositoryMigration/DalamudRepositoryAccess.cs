using System.Collections;
using System.Reflection;
using Dalamud.Plugin;

namespace Snowcloak.Services.RepositoryMigration;

// Dalamud does not expose repository management through its public plugin API.
// Keep the version-dependent access here, including the host's persistence methods.
internal sealed class DalamudRepositoryAccess(object manager, object configuration, object autoUpdates)
{
    internal const string Destination = "https://xiv.eauldane.dev/repo.json";
    private static readonly string[] LegacyUrls =
    [
        "https://git.snowcloak-sync.com/repo.json",
        "https://raw.githubusercontent.com/Eauldane/ElfShelf/main/repo.json",
    ];

    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Lock _pluginListLock = (Lock)(manager.GetType().GetField("pluginListLock", InstanceMembers)?.GetValue(manager)
        ?? throw new NotSupportedException("Dalamud's plugin-list lock is unavailable."));
    private IList Subscriptions => (IList)Read(configuration, "ThirdRepoList");
    internal bool Ready => (bool)Read(manager, "PluginsReady") && (bool)Read(manager, "ReposReady")
        && (bool)Read(autoUpdates, "IsAutoUpdateComplete");

    internal static DalamudRepositoryAccess Open()
    {
        var assembly = typeof(IDalamudPluginInterface).Assembly;
        object Service(string name)
        {
            var type = assembly.GetType(name, throwOnError: true)!;
            var service = assembly.GetType("Dalamud.Service`1", throwOnError: true)!.MakeGenericType(type);
            return service.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)!.Invoke(null, null)!;
        }

        return new(Service("Dalamud.Plugin.Internal.PluginManager"),
            Service("Dalamud.Configuration.Internal.DalamudConfiguration"),
            Service("Dalamud.Plugin.Internal.AutoUpdate.AutoUpdateManager"));
    }

    internal static bool IsLegacy(string url) => LegacyUrls.Contains(url, StringComparer.Ordinal);
    private object[] OldSubscriptions() => Subscriptions.Cast<object>().Where(s => IsLegacy((string)Read(s, "Url"))).ToArray();
    private object? NewSubscription() => Subscriptions.Cast<object>().FirstOrDefault(s => (string)Read(s, "Url") == Destination);

    internal PluginSource[] AffectedPlugins() => ((IEnumerable)Read(manager, "InstalledPlugins")).Cast<object>()
        .Where(p => !(bool)Read(p, "IsDev"))
        .Select(p => new PluginSource(p, Read(p, "Manifest"), (FileInfo)Read(p, "DllFile")))
        .Where(p => IsLegacy(p.Source)).ToArray();

    // Handling Ledger and BigMac as well
    internal string Describe() => $"{OldSubscriptions().Length} legacy subscription(s), {AffectedPlugins().Length} installed plugin(s) to transfer";
    internal bool Needed => OldSubscriptions().Length > 0 || AffectedPlugins().Length > 0
        || Subscriptions.Cast<object>().Count(s => (string)Read(s, "Url") == Destination) > 1;

    internal int Migrate(CancellationToken cancellationToken)
    {
        using var scope = _pluginListLock.EnterScope();
        cancellationToken.ThrowIfCancellationRequested();
        if (!Ready) throw new InvalidOperationException("Dalamud is still loading or updating plugins; retry when it finishes.");
        var affected = AffectedPlugins();
        if (affected.Any(p => Read(p.Plugin, "State").ToString() is "Loading" or "Unloading"))
            throw new InvalidOperationException("An affected plugin is changing state; retry when it finishes.");
        
        var legacy = OldSubscriptions();
        var enabled = legacy.Any(s => (bool)Read(s, "IsEnabled")) || legacy.Length == 0 && affected.Length > 0;
        if (NewSubscription() == null)
        {
            var entryType = Subscriptions.GetType().GetGenericArguments().Single();
            var entry = Activator.CreateInstance(entryType)!;
            Write(entry, "Url", Destination);
            Write(entry, "IsEnabled", enabled);
            Subscriptions.Add(entry);
            try { Invoke(configuration, "ForceSave"); }
            catch
            {
                Subscriptions.Remove(entry);
                Invoke(configuration, "QueueSave");
                throw;
            }
        }
        else if (enabled || Subscriptions.Cast<object>().Any(s => (string)Read(s, "Url") == Destination && (bool)Read(s, "IsEnabled")))
        {
            Write(NewSubscription()!, "IsEnabled", true);
            Invoke(configuration, "ForceSave");
        }

        foreach (var plugin in affected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var oldSource = plugin.Source;
            Write(plugin.Manifest, "InstalledFromUrl", Destination);
            try { Invoke(plugin.Manifest, "Save", plugin.ManifestFile, "Snowcloak repository migration"); }
            catch
            {
                Write(plugin.Manifest, "InstalledFromUrl", oldSource);
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var oldSubscriptions = OldSubscriptions();
        var duplicates = Subscriptions.Cast<object>().Where(s => (string)Read(s, "Url") == Destination).Skip(1).ToArray();
        var removed = oldSubscriptions.Concat(duplicates).ToArray();
        foreach (var entry in removed) Subscriptions.Remove(entry);
        try { Invoke(configuration, "ForceSave"); }
        catch
        {
            foreach (var entry in removed) Subscriptions.Add(entry);
            Invoke(configuration, "QueueSave");
            throw;
        }
        return affected.Length;
    }

    internal Task Reload() => (Task)Invoke(manager, "SetPluginReposFromConfigAsync", true)!;
    internal bool DestinationAvailable => ((IEnumerable)Read(manager, "Repos")).Cast<object>()
        .Any(r => (string)Read(r, "PluginMasterUrl") == Destination && Read(r, "State").ToString() == "Success");

    private static object Read(object target, string name) => target.GetType().GetProperty(name, InstanceMembers)?.GetValue(target)
        ?? throw new NotSupportedException($"Dalamud {target.GetType().Name}.{name} is unavailable.");
    private static void Write(object target, string name, object value) =>
        (target.GetType().GetProperty(name, InstanceMembers)
         ?? throw new NotSupportedException($"Dalamud {target.GetType().Name}.{name} is unavailable.")).SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) =>
        (target.GetType().GetMethod(name, InstanceMembers, args.Select(a => a.GetType()).ToArray())
         ?? throw new NotSupportedException($"Dalamud {target.GetType().Name}.{name} is unavailable.")).Invoke(target, args);

    internal sealed record PluginSource(object Plugin, object Manifest, FileInfo DllFile)
    {
        internal string Name => (string)Read(Manifest, "InternalName");
        internal string Source => (string)Read(Manifest, "InstalledFromUrl");
        internal FileInfo ManifestFile => new(Path.ChangeExtension(DllFile.FullName, ".json"));
    }
}
