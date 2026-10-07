using Microsoft.Extensions.DependencyInjection;
using Snowcloak.PlayerData.Factories;
using Snowcloak.Infrastructure.Transfers;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.SignalR;

namespace Snowcloak.Initialization;

internal static class WebApiServiceRegistration
{
    public static IServiceCollection AddSnowcloakWebApi(this IServiceCollection collection)
    {
        collection.AddSingleton<ServerRegistry>();
        collection.AddSingleton<Snowcloak.FileRepair.FileRepairService>();
        // only resolve repairs when needed or it gets circular
        collection.AddSingleton(p => new Lazy<Snowcloak.FileRepair.FileRepairService>(
            () => p.GetRequiredService<Snowcloak.FileRepair.FileRepairService>()));
        collection.AddSingleton<Snowcloak.EnvironmentSnapshots.SnapshotService>();
        collection.AddSingleton<Snowcloak.EnvironmentSnapshots.SnapshotRestoreService>();
        collection.AddSingleton<NotesStore>();
        collection.AddSingleton<TagStore>();
        collection.AddSingleton<BlockListStore>();
        collection.AddSingleton<SecretKeyBackupService>();
        collection.AddSingleton<TokenProvider>();
        collection.AddSingleton<AccountRegistrationService>();
        collection.AddSingleton<HubFactory>();
        collection.AddSingleton<ApiController>();
        collection.AddSingleton<FileUploadManager>();
        collection.AddSingleton<FileTransferOrchestrator>();
        collection.AddSingleton<ImageTransferService>();
        collection.AddSingleton<DownloadStatusStore>();
        collection.AddSingleton<FileDownloadNegativeCache>();
        collection.AddSingleton<IFileDownloadTransport, DirectFileDownloadTransport>();
        collection.AddSingleton<FileDownloadManagerFactory>();

        return collection;
    }
}
