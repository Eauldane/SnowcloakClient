using Snowcloak.Configuration.Models;

namespace Snowcloak.Configuration.Configurations;

public class CharaDataStateConfig : ISnowcloakConfiguration
{
    public Dictionary<string, CharaDataFavorite> FavoriteCodes { get; set; } = [];
    public int Version { get; set; }
}
