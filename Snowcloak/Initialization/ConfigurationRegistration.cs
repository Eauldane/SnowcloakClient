using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;

namespace Snowcloak.Initialization;

internal static class ConfigurationRegistration
{
    public static IServiceCollection AddSnowcloakConfiguration(this IServiceCollection collection, string configDirectory)
    {
        collection.AddSingleton(s => new ConfigStore(
            s.GetRequiredService<ILogger<ConfigStore>>(),
            configDirectory));

        collection.AddSingleton<SnowcloakConfigService>();
        collection.AddSingleton<ServerConfigService>();
        collection.AddSingleton<NotesConfigService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<NotesConfigService>());
        collection.AddSingleton<ServerTagConfigService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<ServerTagConfigService>());
        collection.AddSingleton<SyncshellConfigService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<SyncshellConfigService>());
        collection.AddSingleton<TransientConfigService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<TransientConfigService>());
        collection.AddSingleton<XivDataStorageService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<XivDataStorageService>());
        collection.AddSingleton<PairAppearanceCacheService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<PairAppearanceCacheService>());
        collection.AddSingleton<PlayerPerformanceConfigService>();
        collection.AddSingleton<ServerBlockConfigService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<ServerBlockConfigService>());
        collection.AddSingleton<CharaDataConfigService>();
        collection.AddSingleton<CharaDataStateConfigService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<CharaDataStateConfigService>());
        collection.AddSingleton<PairingFilterConfigService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<PairingFilterConfigService>());
        collection.AddSingleton<VenueStateConfigService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<VenueStateConfigService>());
        collection.AddSingleton<RemoteConfigCacheService>();
        collection.AddSingleton<IStateDocument>(sp => sp.GetRequiredService<RemoteConfigCacheService>());
        collection.AddSingleton<StateDocumentWarmup>();

        return collection;
    }
}
