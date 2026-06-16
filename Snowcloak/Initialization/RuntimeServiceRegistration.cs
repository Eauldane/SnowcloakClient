using Microsoft.Extensions.DependencyInjection;
using Snowcloak.FileCache;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.PlayerData.Services;
using Snowcloak.Services;

namespace Snowcloak.Initialization;

internal static class RuntimeServiceRegistration
{
    public static IServiceCollection AddSnowcloakRuntimePlan(this IServiceCollection collection)
    {
        collection.AddSingleton(new RuntimeServicePlan
        {
            // Needed even on the intro screen (before setup is valid).
            BaseServices =
            [
                typeof(UiService),
                typeof(CommandManagerService),
            ],
            // Only spun up once setup and server config are valid.
            ConfiguredServices =
            [
                typeof(CacheCreationService),
                typeof(TransientResourceManager),
                typeof(ChatService),
                typeof(PairDisplayDecorationService),
            ],
        });

        return collection;
    }
}
