using Snowcloak.API.Data;

namespace Snowcloak.Configuration.Models;

[Serializable]
public class ServerStorage
{
    public List<Authentication> Authentications { get; set; } = [];
    public bool FullPause { get; set; } = false;
    public bool AccountLinked { get; set; } = false;
    public Guid? UserAccountId { get; set; }
    public Dictionary<int, SecretKey> SecretKeys { get; set; } = [];
    public string ServerName { get; set; } = string.Empty;
    public string ServerUri { get; set; } = string.Empty;
    public List<ChatChannelData> JoinedChannels { get; set; } = [];
}
