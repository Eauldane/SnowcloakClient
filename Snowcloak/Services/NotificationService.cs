using System.Globalization;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Configurations;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.Mediator;
using DalamudNotificationType = Dalamud.Interface.ImGuiNotification.NotificationType;
using NotificationType = Snowcloak.Configuration.Models.NotificationType;

namespace Snowcloak.Services;

public partial class NotificationService : DisposableMediatorSubscriberBase, IHostedService
{
    private static readonly Dictionary<NotificationType, NotificationRoute> NotificationRoutes =
        new()
        {
            [NotificationType.Info] = new(static config => config.InfoNotification,
                static (service, message) => service.PrintInfoChat(message), DalamudNotificationType.Info),
            [NotificationType.Warning] = new(static config => config.WarningNotification,
                static (service, message) => service.PrintWarnChat(message), DalamudNotificationType.Warning),
            [NotificationType.Error] = new(static config => config.ErrorNotification,
                static (service, message) => service.PrintErrorChat(message), DalamudNotificationType.Error),
        };

    private readonly DalamudUtilService _dalamudUtilService;
    private readonly INotificationManager _notificationManager;
    private readonly IChatGui _chatGui;
    private readonly SnowcloakConfigService _configurationService;

    public NotificationService(ILogger<NotificationService> logger, SnowMediator mediator,
        DalamudUtilService dalamudUtilService,
        INotificationManager notificationManager,
        IChatGui chatGui, SnowcloakConfigService configurationService) : base(logger, mediator)
    {
        _dalamudUtilService = dalamudUtilService;
        _notificationManager = notificationManager;
        _chatGui = chatGui;
        _configurationService = configurationService;
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Mediator.Subscribe<NotificationMessage>(this, ShowNotification);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeAll();
        return Task.CompletedTask;
    }

    private void PrintErrorChat(string? message)
    {
        var content = "[Snowcloak] Error: {0}";
        SeStringBuilder se = new SeStringBuilder().AddText(string.Format(CultureInfo.InvariantCulture, content, message));
        _chatGui.PrintError(se.BuiltString);
    }

    private void PrintInfoChat(string? message)
    {
        var prefix = "[Snowcloak] Info: ";
        SeStringBuilder se = new SeStringBuilder().AddText(prefix).AddItalics(message ?? string.Empty);
        _chatGui.Print(se.BuiltString);
    }

    private void PrintWarnChat(string? message)
    {
        var prefix = "[Snowcloak] ";
        var warningText = "Warning: {0}";
        SeStringBuilder se = new SeStringBuilder().AddText(prefix).AddUiForeground(string.Format(CultureInfo.InvariantCulture, warningText, message ?? string.Empty), 31).AddUiForegroundOff();
        _chatGui.Print(se.BuiltString);
    }

    private void ShowChat(NotificationMessage msg)
    {
        if (NotificationRoutes.TryGetValue(msg.Type, out var route))
            route.PrintChat(this, msg.Message);
    }

    private void ShowNotification(NotificationMessage msg)
    {
        LogNotification(Logger, msg);

        if (!_dalamudUtilService.IsLoggedIn) return;

        if (NotificationRoutes.TryGetValue(msg.Type, out var route))
            ShowNotificationLocationBased(msg, route, route.ResolveLocation(_configurationService.Current));
    }

    private void ShowNotificationLocationBased(NotificationMessage msg, NotificationRoute route, NotificationLocation location)
    {
        switch (location)
        {
            case NotificationLocation.Toast:
                ShowToast(msg, route);
                break;

            case NotificationLocation.Chat:
                ShowChat(msg);
                break;

            case NotificationLocation.Both:
                ShowToast(msg, route);
                ShowChat(msg);
                break;

            case NotificationLocation.Nowhere:
                break;
        }
    }

    private void ShowToast(NotificationMessage msg, NotificationRoute route)
    {
        _notificationManager.AddNotification(new Notification()
        {
            Content = msg.Message ?? string.Empty,
            Title = msg.Title,
            Type = route.ToastType,
            Minimized = false,
            InitialDuration = msg.TimeShownOnScreen ?? TimeSpan.FromSeconds(3)
        });
    }

    private sealed record NotificationRoute(
        Func<SnowcloakConfig, NotificationLocation> ResolveLocation,
        Action<NotificationService, string?> PrintChat,
        DalamudNotificationType ToastType);

    [LoggerMessage(EventId = 0, Level = LogLevel.Information, Message = "{Notification}")]
    private static partial void LogNotification(ILogger logger, NotificationMessage notification);
}
