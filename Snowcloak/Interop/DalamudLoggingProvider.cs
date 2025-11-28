using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.SnowcloakConfiguration;
using System.Collections.Concurrent;

namespace Snowcloak.Interop;

[ProviderAlias("Dalamud")]
public sealed class DalamudLoggingProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, DalamudLogger> _loggers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly IPluginLog _pluginLog;

    public DalamudLoggingProvider(SnowcloakConfigService snowcloakConfigService, IPluginLog pluginLog)
    {
        _snowcloakConfigService = snowcloakConfigService;
        _pluginLog = pluginLog;
    }

    public ILogger CreateLogger(string categoryName)
    {
        string catName = categoryName.Split(".", StringSplitOptions.RemoveEmptyEntries).Last();
        if (catName.Length > 15)
        {
            catName = string.Join("", catName.Take(6)) + "..." + string.Join("", catName.TakeLast(6));
        }
        else
        {
            catName = string.Join("", Enumerable.Range(0, 15 - catName.Length).Select(_ => " ")) + catName;
        }

        return _loggers.GetOrAdd(catName, name => new DalamudLogger(name, _snowcloakConfigService, _pluginLog));
    }

    public void Dispose()
    {
        _loggers.Clear();
        GC.SuppressFinalize(this);
    }
}