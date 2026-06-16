using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Snowcloak.Initialization;

internal static class DalamudServiceRegistration
{
    public static IServiceCollection AddDalamudServices(this IServiceCollection collection,
        IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IDataManager gameData,
        IFramework framework, IObjectTable objectTable, IPlayerState playerState, IClientState clientState,
        ICondition condition, IChatGui chatGui, IGameGui gameGui, IDtrBar dtrBar, IToastGui toastGui,
        IPluginLog pluginLog, ITargetManager targetManager, INotificationManager notificationManager,
        ITextureProvider textureProvider, IContextMenu contextMenu, IGameInteropProvider gameInteropProvider,
        INamePlateGui namePlateGui, IGameConfig gameConfig, IPartyList partyList)
    {
        collection.AddSingleton(pluginInterface);
        collection.AddSingleton(pluginInterface.UiBuilder);
        collection.AddSingleton(commandManager);
        collection.AddSingleton(gameData);
        collection.AddSingleton(framework);
        collection.AddSingleton(objectTable);
        collection.AddSingleton(clientState);
        collection.AddSingleton(playerState);
        collection.AddSingleton(condition);
        collection.AddSingleton(chatGui);
        collection.AddSingleton(gameGui);
        collection.AddSingleton(dtrBar);
        collection.AddSingleton(toastGui);
        collection.AddSingleton(pluginLog);
        collection.AddSingleton(targetManager);
        collection.AddSingleton(notificationManager);
        collection.AddSingleton(textureProvider);
        collection.AddSingleton(contextMenu);
        collection.AddSingleton(gameInteropProvider);
        collection.AddSingleton(namePlateGui);
        collection.AddSingleton(gameConfig);
        collection.AddSingleton(partyList);

        return collection;
    }
}
