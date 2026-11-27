using System.Collections;
using System.Reflection;
using Dalamud.Interface;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace Snowcloak.Services;

internal static class DalamudPluginUpdate
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static async Task RunAsync(IDalamudPluginInterface plugin, IFramework framework,
        IChatGui chat, ILogger logger, CancellationToken token)
    {
        try
        {
            if (plugin.IsDev)
            {
                await framework.RunOnFrameworkThread(() => plugin.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins, plugin.InternalName)).ConfigureAwait(false);
                return;
            }

            var manager = Service("Dalamud.Plugin.Internal.PluginManager");
            var reload = (Task)manager.GetType().GetMethod("ReloadAllReposAsync", Members, Type.EmptyTypes)!.Invoke(manager, null)!;
            await reload.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await framework.RunOnFrameworkThread(() =>
            {
                token.ThrowIfCancellationRequested();
                plugin.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins, plugin.InternalName);
                var ui = Service("Dalamud.Interface.Internal.DalamudInterface");
                var installer = ui.GetType().GetField("pluginWindow", Members)!.GetValue(ui)!;
                if (!TryLaunch(manager, installer, plugin.WorkingPluginId, plugin.InternalName))
                    chat.Print("[Snowcloak] Snowcloak has a pending update, but Dalamud hasn't found it yet. Try opening the plugin installer to force it to check.");
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't start Snowcloak's update! Opening the plugin installer to force the issue...");
            if (!token.IsCancellationRequested)
                await framework.RunOnFrameworkThread(() => plugin.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins, plugin.InternalName)).ConfigureAwait(false);
        }
    }

    internal static bool TryLaunch(object manager, object installer, Guid workingPluginId, string internalName)
    {
        if ((bool)installer.GetType().GetProperty("AnyOperationInProgress", Members)!.GetValue(installer)!) return false;

        var updates = (IEnumerable)manager.GetType().GetProperty("UpdatablePlugins", Members)!.GetValue(manager)!;
        foreach (var update in updates.Cast<object>())
        {
            var installed = update.GetType().GetProperty("InstalledPlugin")!.GetValue(update)!;
            var manifest = installed.GetType().GetProperty("Manifest")!.GetValue(installed)!;
            if ((Guid)installed.GetType().GetProperty("EffectiveWorkingPluginId")!.GetValue(installed)! != workingPluginId
                || (string)manifest.GetType().GetProperty("InternalName")!.GetValue(manifest)! != internalName
                || (bool)installed.GetType().GetProperty("IsDev")!.GetValue(installed)!) continue;

            var method = installer.GetType().GetMethod("UpdateSinglePlugin", Members, [update.GetType()])!;
            _ = (Task)method.Invoke(installer, [update])!;
            return true;
        }
        return false;
    }

    private static object Service(string name)
    {
        var assembly = typeof(IDalamudPluginInterface).Assembly;
        var type = assembly.GetType(name, throwOnError: true)!;
        return assembly.GetType("Dalamud.Service`1", throwOnError: true)!.MakeGenericType(type)
            .GetMethod("Get", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)!.Invoke(null, null)!;
    }
}
