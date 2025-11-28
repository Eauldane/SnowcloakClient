namespace Snowcloak.Configuration.Models;

[Serializable]
public class ServerShellStorage
{
    public Dictionary<string, ShellConfig> GidShellConfig { get; set; } = new(StringComparer.Ordinal);
}