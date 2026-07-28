using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Services;

public sealed class ServerNewsChatService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly IChatGui _chatGui;
    private string? _lastDisplayedNews;

    public ServerNewsChatService(
        ILogger<ServerNewsChatService> logger,
        SnowMediator mediator,
        IChatGui chatGui)
        : base(logger, mediator)
    {
        _chatGui = chatGui;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mediator.Subscribe<ConnectedMessage>(this, message => PrintNews(message.Connection.News));
        Mediator.Subscribe<ServerNewsMessage>(this, message => PrintNews(message.News));
        Mediator.Subscribe<DalamudLogoutMessage>(this, _ => _lastDisplayedNews = null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeAll();
        return Task.CompletedTask;
    }

    private void PrintNews(string? news)
    {
        if (string.IsNullOrWhiteSpace(news))
            return;

        var normalised = news.Trim();
        if (string.Equals(_lastDisplayedNews, normalised, StringComparison.Ordinal))
            return;

        _lastDisplayedNews = normalised;
        _chatGui.Print(new XivChatEntry
        {
            Message = "[Snowcloak News] " + normalised,
            Type = XivChatType.SystemMessage,
        });
    }
}
