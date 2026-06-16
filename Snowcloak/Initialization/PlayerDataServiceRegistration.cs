using Microsoft.Extensions.DependencyInjection;
using Snowcloak.FileCache;
using Snowcloak.PlayerData.Factories;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.PlayerData.Services;
using Snowcloak.Services;
using Snowcloak.UI;
using Snowcloak.UI.Components;

namespace Snowcloak.Initialization;

internal static class PlayerDataServiceRegistration
{
    public static IServiceCollection AddSnowcloakPlayerData(this IServiceCollection collection)
    {
        collection.AddSingleton<GameObjectHandlerMonitor>();
        collection.AddSingleton<GameObjectHandlerFactory>();
        collection.AddSingleton<PairHandlerFactory>();
        collection.AddSingleton<PairAnalyzerFactory>();
        collection.AddSingleton<PairFactory>();
        collection.AddSingleton<PairContextMenuBuilder>();
        collection.AddSingleton<PairManager>();
        collection.AddSingleton<PairRequestService>();
        collection.AddSingleton<XivDataAnalyzer>();
        collection.AddSingleton<CharacterAnalyzer>();
        collection.AddSingleton<PlayerPerformanceService>();
        collection.AddSingleton<DtrEntry>();
        collection.AddSingleton<PairingAvailabilityDtrEntry>();

        // created once the player is present
        collection.AddScoped<CacheMonitor>();
        collection.AddScoped<CacheCreationService>();
        collection.AddScoped<TransientRecordingService>();
        collection.AddScoped<TransientResourceManager>();
        collection.AddScoped<SnapshotBuilder>();

        return collection;
    }
}
