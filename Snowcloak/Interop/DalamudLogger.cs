using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.SnowcloakConfiguration;
using System.Text;

namespace Snowcloak.Interop;

internal sealed class DalamudLogger : ILogger
{
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly string _name;
    private readonly IPluginLog _pluginLog;

    public DalamudLogger(string name, SnowcloakConfigService snowcloakConfigService, IPluginLog pluginLog)
    {
        _name = name;
        _snowcloakConfigService = snowcloakConfigService;
        _pluginLog = pluginLog;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => default!;

    public bool IsEnabled(LogLevel logLevel)
    {
        return (int)_snowcloakConfigService.Current.LogLevel <= (int)logLevel;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        if ((int)logLevel <= (int)LogLevel.Information)
            _pluginLog.Information($"[{_name}]{{{(int)logLevel}}} {state}");
        else
        {
            StringBuilder sb = new();
            sb.Append($"[{_name}]{{{(int)logLevel}}} {state}: {exception?.Message}");
            if (!string.IsNullOrWhiteSpace(exception?.StackTrace))
                sb.AppendLine(exception?.StackTrace);
            var innerException = exception?.InnerException;
            while (innerException != null)
            {
                sb.AppendLine($"InnerException {innerException}: {innerException.Message}");
                sb.AppendLine(innerException.StackTrace);
                innerException = innerException.InnerException;
            }
            if (logLevel == LogLevel.Warning)
                _pluginLog.Warning(sb.ToString());
            else if (logLevel == LogLevel.Error)
                _pluginLog.Error(sb.ToString());
            else
                _pluginLog.Fatal(sb.ToString());
        }
    }
}