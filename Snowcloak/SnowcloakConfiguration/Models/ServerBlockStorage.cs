namespace Snowcloak.Configuration.Models;

[Serializable]
public class ServerBlockStorage
{
    public List<string> Whitelist { get; set; } = new();
    public List<string> Blacklist { get; set; } = new();
}