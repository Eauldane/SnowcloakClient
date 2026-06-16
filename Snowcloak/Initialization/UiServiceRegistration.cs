using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Scrutor;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;
using Snowcloak.UI.Components;
using Snowcloak.UI.Components.BbCode;
using Snowcloak.UI.Components.Popup;
using Snowcloak.UI.Handlers;
using IntroUi = Snowcloak.UI.IntroUi;

namespace Snowcloak.Initialization;

internal static class UiServiceRegistration
{
    public static IServiceCollection AddSnowcloakUi(this IServiceCollection collection)
    {
        collection.AddSingleton(new WindowSystem("Snowcloak"));
        collection.AddSingleton<FileDialogManager>();
        collection.AddSingleton<TextureService>();
        collection.AddSingleton<TransferOverlayUiState>();
        collection.AddSingleton<UiFontService>();
        collection.AddSingleton<BbCodeRenderer>();
        collection.AddSingleton<TagHandler>();
        collection.AddSingleton<UidDisplayHandler>();

        // Scoped UI services
        collection.AddScoped<UiFactory>();
        collection.AddScoped<UiService>();
        collection.AddScoped<CommandManagerService>();
        collection.AddScoped<BbCodeRenderService>();
        collection.AddScoped<AdvancedSettingsPanel>();
        collection.AddScoped<ChatSettingsPanel>();
        collection.AddScoped<GeneralSettingsPanel>();
        collection.AddScoped<InterfaceSettingsPanel>();
        collection.AddScoped<NotificationSettingsPanel>();
        collection.AddScoped<PerformanceSettingsPanel>();
        collection.AddScoped<PluginAvailabilityPanel>();
        collection.AddScoped<ServiceSelectionPanel>();
        collection.AddScoped<StorageSettingsPanel>();
        collection.AddScoped<TransferSettingsPanel>();
        collection.AddScoped<ChatService>();
        collection.AddScoped<PairDisplayDecorationService>();


        collection.Scan(scan => scan
            .FromAssemblyOf<UiService>()
            .AddClasses(classes => classes.AssignableTo<IStaticWindow>(), publicOnly: false)
            .As<WindowMediatorSubscriberBase>()
            .WithScopedLifetime());

        // Popup handlers
        collection.AddScoped<IPopupHandler, ReportPopupHandler>();
        collection.AddScoped<IPopupHandler, BanUserPopupHandler>();
        collection.AddScoped<IPopupHandler, BbCodeLinkPopupHandler>();

        return collection;
    }
}
