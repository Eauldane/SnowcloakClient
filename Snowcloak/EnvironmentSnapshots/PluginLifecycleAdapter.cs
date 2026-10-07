using System.Reflection;
using Dalamud.Plugin;
using Snowcloak.Core.EnvironmentSnapshots;

namespace Snowcloak.EnvironmentSnapshots;

internal sealed class PluginLifecycleAdapter(IDalamudPluginInterface pi)
{
    private static readonly Guid SupportedModule = new("9a6f3e4f-c2f0-46f4-b211-7013dde20a8b");
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private Task? _pending;

    public void Validate(IEnumerable<string> names)
    {
        if (_pending is { IsCompleted: false }) throw new InvalidOperationException("A plugin lifecycle operation is still running. Sync suspended.");
        if (typeof(IDalamudPluginInterface).Module.ModuleVersionId != SupportedModule)
            throw new NotSupportedException("Automatic restore lifecycle is unsupported for this Dalamud build.");
        foreach (var name in names) Inspect(name);
    }

    public Task ChangeAsync(IReadOnlyDictionary<string, bool> original, bool reload, CancellationToken ct)
    {
        Validate(original.Keys);
        return RestorePluginSequence.ChangeAsync(original, reload, Loaded, ChangeOneAsync, ct);
    }

    private bool Loaded(string name) => pi.InstalledPlugins.Single(p => p.InternalName == name).IsLoaded;
    private (object Local, MethodInfo Unload, MethodInfo Load) Inspect(string name)
    {
        if (name is not ("Penumbra" or "Glamourer" or "CustomizePlus")) throw new NotSupportedException("Unknown restore plugin.");
        var exposed = pi.InstalledPlugins.Single(p => p.InternalName == name);
        var wrapper = exposed.GetType();
        if (wrapper.FullName != "Dalamud.Plugin.ExposedPlugin") throw new NotSupportedException("Unsupported plugin lifecycle wrapper.");
        var field = wrapper.GetField("<plugin>P", Members) ?? throw new NotSupportedException("Plugin lifecycle reference is unavailable.");
        if (field.FieldType.FullName != "Dalamud.Plugin.Internal.Types.LocalPlugin") throw new NotSupportedException("Unsupported plugin lifecycle target.");
        var local = field.GetValue(exposed) ?? throw new NotSupportedException("Plugin lifecycle target is unavailable.");
        var type = field.FieldType;
        var assembly = type.Assembly;
        var mode = assembly.GetType("Dalamud.Plugin.Internal.Types.PluginLoaderDisposalMode", true)!;
        var reason = assembly.GetType("Dalamud.Plugin.PluginLoadReason", true)!;
        var unload = type.GetMethod("UnloadAsync", Members, [mode]);
        var load = type.GetMethod("LoadAsync", Members, [reason, typeof(bool), typeof(CancellationToken)]);
        if (unload == null || load == null || unload.ReturnType != typeof(Task) || load.ReturnType != typeof(Task)
            || !Enum.IsDefined(mode, "WaitBeforeDispose") || !Enum.IsDefined(reason, "Reload"))
            throw new NotSupportedException("Unsupported plugin lifecycle signatures.");
        return (local, unload, load);
    }
    private async Task ChangeOneAsync(string name, bool loaded, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (local, unload, load) = Inspect(name);
        var method = loaded ? load : unload;
        object?[] args = loaded ? [Enum.Parse(load.GetParameters()[0].ParameterType, "Reload"), false, CancellationToken.None]
            : [Enum.Parse(unload.GetParameters()[0].ParameterType, "WaitBeforeDispose")];
        _pending = Task.Run(async () =>
        {
            try { await ((Task)method.Invoke(local, args)!).ConfigureAwait(false); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
        });
        try { await _pending.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false); }
        finally { if (_pending.IsCompleted) _pending = null; }
        ct.ThrowIfCancellationRequested();
    }
}
