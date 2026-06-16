namespace Snowcloak.Configuration.Configurations;

public class CharaDataConfig : ISnowcloakConfiguration
{
    public bool OpenMareHubOnGposeStart { get; set; }
    public string LastSavedCharaDataLocation { get; set; } = string.Empty;
    public bool DownloadMcdDataOnConnection { get; set; } = true;
    public int Version { get; set; }
    public bool NearbyOwnServerOnly { get; set; }
    public bool NearbyIgnoreHousingLimitations { get; set; }
    public bool NearbyDrawWisps { get; set; } = true;
    public int NearbyDistanceFilter { get; set; } = 100;
    public bool NearbyShowOwnData { get; set; }
    public bool ShowHelpTexts { get; set; } = true;
    public bool NearbyShowAlways { get; set; }
}
