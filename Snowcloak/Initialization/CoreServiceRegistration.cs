using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.FileCache;
using Snowcloak.Game.Interop;
using Snowcloak.Game.Scheduling;
using Snowcloak.Infrastructure.Data;
using Snowcloak.Interop;
using Snowcloak.Services;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ModNullification;
using Snowcloak.Services.Performance;
using Snowcloak.Core.Chat;
using Snowcloak.Services.Chat;

namespace Snowcloak.Initialization;

internal static class CoreServiceRegistration
{
    public static IServiceCollection AddSnowcloakCore(this IServiceCollection collection)
    {
        collection.AddSingleton<SnowMediator>();
        collection.AddSingleton<PerformanceCollectorService>();
        collection.AddSingleton<IFrameTickProfiler, FrameSchedulerProfiler>();
        collection.AddSingleton<FrameScheduler>();
        collection.AddSingleton<IFrameScheduler>(sp => sp.GetRequiredService<FrameScheduler>());
        collection.AddSingleton(sp => new SqliteDatabase(
            sp.GetRequiredService<SnowcloakConfigService>().ConfigurationDirectory,
            sp.GetRequiredService<ILogger<SqliteDatabase>>()));
        collection.AddSingleton<SqliteStateDocumentStore>();
        collection.AddSingleton<StateDocumentStore>();
        collection.AddSingleton<DatabaseService>();
        collection.AddSingleton<UsageStatisticsService>();
        collection.AddSingleton<SemiTransientResourceStore>();
        collection.AddSingleton<FileCacheIndex>();
        collection.AddSingleton<FileCacheManager>();
        collection.AddSingleton<FileCompactor>();
        collection.AddSingleton<SnowPlugin>();
        collection.AddSingleton<ObjectTableCache>();
        collection.AddSingleton<GposeService>();
        collection.AddSingleton<PlayerInteractionService>();
        collection.AddSingleton<PlotPresenceTracker>();
        collection.AddSingleton<GameStateTracker>();
        collection.AddSingleton<DalamudUtilService>();
        collection.AddSingleton<VisibilityService>();
        collection.AddSingleton<EventAggregator>();
        collection.AddSingleton<NotificationService>();
        collection.AddSingleton<PluginWatcherService>();
        collection.AddSingleton<SyncTroubleshootingService>();
        collection.AddSingleton<PluginWarningNotificationService>();
        collection.AddSingleton<VfxSpawnManager>();
        collection.AddSingleton<BlockedCharacterHandler>();
        collection.AddSingleton<HumanCmpDefaultsProvider>();
        collection.AddSingleton<ModNullificationService>();
        collection.AddSingleton<GpuMemoryBudgetService>();
        collection.AddSingleton<SyncshellBudgetService>();
        collection.AddSingleton<CrowdPriorityController>();
        collection.AddSingleton<ApplicationAdmissionController>();
        collection.AddSingleton<TextureShrinkService>();
        collection.AddSingleton<SnowProfileManager>();
        collection.AddSingleton<UserSafetyStore>();
        collection.AddSingleton<RoleplayClientService>();
        collection.AddSingleton<RoleplayReminderService>();
        collection.AddSingleton<CharacterProfileBackupService>();
        collection.AddSingleton<ChatRoomRegistry>();
        collection.AddSingleton<ChatIdentityResolver>();
        collection.AddSingleton<IChatIdentityResolver>(sp => sp.GetRequiredService<ChatIdentityResolver>());
        collection.AddSingleton<ChatTransport>();
        collection.AddSingleton<IChatTransport>(sp => sp.GetRequiredService<ChatTransport>());
        collection.AddSingleton<ChatStore>();
        collection.AddSingleton<ChatClientService>();
        collection.AddSingleton<GameChatLogRenderer>();
        collection.AddSingleton<ChatNotifier>();
        collection.AddSingleton<ChatSoundPlayer>();

        return collection;
    }
}
