using Snowcloak.Configuration.Models;
using Snowcloak.WebAPI;

namespace Snowcloak.Configuration.Configurations;

[Serializable]
public class ServerConfig : ISnowcloakConfiguration
{
    public int CurrentServer { get; set; } = 0;

    public List<ServerStorage> ServerStorage { get; set; } = new()
    {
        { new ServerStorage() { ServerName = ApiController.SnowcloakServer, ServerUri = ApiController.SnowcloakServiceUri } },
    };

    public int Version { get; set; } = 1;
}