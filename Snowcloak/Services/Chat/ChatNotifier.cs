using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Core.Chat;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Services.Chat;

public sealed class ChatNotifier : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ChatPreferencesStore _chatPreferences;
    private readonly ChatClientService _chatService;
    private readonly SnowcloakConfigService _configService;
    private readonly GameChatLogRenderer _gameChatLogRenderer;
    private readonly INotificationManager _notifications;
    private readonly ChatIdentityResolver _identityResolver;
    private readonly ChatSoundPlayer _soundPlayer;

    public ChatNotifier(ILogger<ChatNotifier> logger, SnowMediator mediator, ChatClientService chatService,
        ChatPreferencesStore chatPreferences, SnowcloakConfigService configService,
        GameChatLogRenderer gameChatLogRenderer, ChatIdentityResolver identityResolver, ChatSoundPlayer soundPlayer,
        INotificationManager notifications)
        : base(logger, mediator)
    {
        _chatService = chatService;
        _chatPreferences = chatPreferences;
        _configService = configService;
        _gameChatLogRenderer = gameChatLogRenderer;
        _identityResolver = identityResolver;
        _soundPlayer = soundPlayer;
        _notifications = notifications;
    }

    public int TotalUnread => _chatService.Store.Snapshot.TotalUnread;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Mediator.Subscribe<ChatIncomingAppendedMessage>(this, HandleIncoming);
        Mediator.Subscribe<ChatOutgoingStampedMessage>(this, HandleOutgoing);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeAll();
        return Task.CompletedTask;
    }

    private void HandleIncoming(ChatIncomingAppendedMessage incoming)
    {
        var config = _configService.Current;
        if (!config.ChatEnabled)
        {
            return;
        }

        var conversation = _chatService.Store.Snapshot.Conversations.FirstOrDefault(candidate => candidate.Key == incoming.Key);
        if (conversation == null)
        {
            return;
        }

        if (config.ChatShowInGameLog)
        {
            _gameChatLogRenderer.Render(incoming.Key, conversation.Title, incoming.Entry);
        }

        if (conversation.Muted)
        {
            return;
        }

        var raw = incoming.Entry.RawText;
        var mentionsSelf = raw.Contains($"[mention:{_identityResolver.SelfUid}]", StringComparison.OrdinalIgnoreCase);
        var direct = incoming.Key.Kind == ConversationKind.Direct;

        if (config.ChatSoundsEnabled)
        {
            var sound = _chatPreferences.ResolveSound(incoming.Key, config.DefaultChatSound);
            if (sound != ChatSoundOption.None)
            {
                _soundPlayer.Play(sound);
            }
        }

        if ((direct && config.ChatToastDirectMessages) || (mentionsSelf && config.ChatToastMentions))
        {
            _notifications.AddNotification(new Notification
            {
                Title = conversation.Title,
                Content = ChatMessageCodec.Flatten(ChatMessageCodec.Decode(raw), _identityResolver.ResolveName),
                Type = Dalamud.Interface.ImGuiNotification.NotificationType.Info,
                InitialDuration = TimeSpan.FromSeconds(5),
            });
        }
    }

    private void HandleOutgoing(ChatOutgoingStampedMessage outgoing)
    {
        var config = _configService.Current;
        if (!config.ChatEnabled || !config.ChatShowInGameLog)
        {
            return;
        }

        var conversation = _chatService.Store.Snapshot.Conversations.FirstOrDefault(candidate => candidate.Key == outgoing.Key);
        if (conversation != null)
        {
            _gameChatLogRenderer.Render(outgoing.Key, conversation.Title, outgoing.Entry);
        }
    }
}
