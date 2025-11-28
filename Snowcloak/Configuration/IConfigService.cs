using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public interface IConfigService<out T> : IDisposable where T : ISnowcloakConfiguration
{
    T Current { get; }
    string ConfigurationName { get; }
    string ConfigurationPath { get; }
    public event EventHandler? ConfigSave;
    void UpdateLastWriteTime();
}
