using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public static class ConfigurationExtensions
{
    public static bool HasValidSetup(this SnowcloakConfig configuration)
    {
        return configuration.AcceptedAgreement && configuration.InitialScanComplete
                    && !string.IsNullOrEmpty(configuration.CacheFolder)
                    && Directory.Exists(configuration.CacheFolder);
    }
}