using Microsoft.Extensions.DependencyInjection;
using Snowcloak.Configuration;
using Snowcloak.FileCache;
using Snowcloak.Game.Scheduling;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Venue;
using Snowcloak.UI;

namespace Snowcloak.Initialization;

internal static class HostedServiceRegistration
{
    public static IServiceCollection AddSnowcloakHostedServices(this IServiceCollection collection)
    {
        collection.AddHostedService(p => p.GetRequiredService<PluginWatcherService>());
        collection.AddHostedService(p => p.GetRequiredService<StateDocumentWarmup>());
        collection.AddHostedService(p => p.GetRequiredService<StateDocumentStore>());
        collection.AddHostedService(p => p.GetRequiredService<ConfigStore>());
        collection.AddHostedService(p => p.GetRequiredService<SnowMediator>());
        collection.AddHostedService(p => p.GetRequiredService<NotificationService>());
        collection.AddHostedService(p => p.GetRequiredService<FileCacheManager>());
        collection.AddHostedService(p => p.GetRequiredService<FrameScheduler>());
        collection.AddHostedService(p => p.GetRequiredService<GameStateTracker>());
        collection.AddHostedService(p => p.GetRequiredService<DalamudUtilService>());
        collection.AddHostedService(p => p.GetRequiredService<SyncTroubleshootingService>());
        collection.AddHostedService(p => p.GetRequiredService<PerformanceCollectorService>());
        collection.AddHostedService(p => p.GetRequiredService<DtrEntry>());
        collection.AddHostedService(p => p.GetRequiredService<PairingAvailabilityDtrEntry>());
        collection.AddHostedService(p => p.GetRequiredService<EventAggregator>());
        collection.AddHostedService(p => p.GetRequiredService<SnowPlugin>());
        collection.AddHostedService(p => p.GetRequiredService<IpcProvider>());
        collection.AddHostedService(p => p.GetRequiredService<VenueSyncshellService>());
        collection.AddHostedService(p => p.GetRequiredService<VenueRegistrationService>());
        collection.AddHostedService(p => p.GetRequiredService<VenueReminderService>());

        return collection;
    }
}
