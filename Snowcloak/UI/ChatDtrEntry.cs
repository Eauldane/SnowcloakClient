using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services.Chat;
using Snowcloak.Services.Mediator;
using System.Globalization;

namespace Snowcloak.UI;

public sealed class ChatDtrEntry : DtrEntryBase
{
    private readonly ChatNotifier _notifier;
    private readonly SnowcloakConfigService _configService;
    private readonly SnowMediator _mediator;
    private int _lastUnread = -1;

    public ChatDtrEntry(ILogger<ChatDtrEntry> logger, IDtrBar dtrBar, ChatNotifier notifier,
        SnowcloakConfigService configService, SnowMediator mediator) : base(logger, dtrBar, "Snowcloak Chat")
    {
        _notifier = notifier;
        _configService = configService;
        _mediator = mediator;
    }

    protected override void ConfigureEntry(IDtrBarEntry entry)
    {
        entry.OnClick = _ => _mediator.Publish(new UiToggleMessage(typeof(ChatWindow)));
    }

    protected override void ResetCachedState()
    {
        _lastUnread = -1;
    }

    protected override void UpdateEntry()
    {
        if (!_configService.Current.ChatEnabled || !_configService.Current.ChatEnableDtrEntry)
        {
            HideEntry();
            return;
        }

        ShowEntry();
        var unread = _notifier.TotalUnread;
        if (_lastUnread == unread)
        {
            return;
        }

        _lastUnread = unread;
        Entry.Text = unread > 0 ? $"Chat ({unread.ToString(CultureInfo.InvariantCulture)})" : "Chat";
        Entry.Tooltip = unread > 0 ? "Open Snowcloak chat" : "No unread Snowcloak messages";
    }
}
