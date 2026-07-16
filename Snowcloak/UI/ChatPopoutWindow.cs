using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.Logging;
using Snowcloak.Core.Chat;
using Snowcloak.Services;
using Snowcloak.Services.Chat;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class ChatPopoutWindow : WindowMediatorSubscriberBase
{
    private readonly ChatConversationView _view;

    public ChatPopoutWindow(ILogger<ChatPopoutWindow> logger, SnowMediator mediator, ChatClientService chatService,
        ImGuiChatRenderer renderer, PerformanceCollectorService performanceCollectorService, ConversationKey key)
        : base(logger, mediator, BuildTitle(chatService ?? throw new ArgumentNullException(nameof(chatService)), key), performanceCollectorService)
    {
        Key = key;
        _view = new ChatConversationView(logger, chatService, renderer, mediator);
        SetScaledSizeConstraints(new Vector2(420, 300), new Vector2(1200, 1600));
        Size = new Vector2(560, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = true;
    }

    public ConversationKey Key { get; }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    protected override void DrawInternal()
    {
        _view.Draw(Key, showHeader: false);
    }

    private static string BuildTitle(ChatClientService service, ConversationKey key)
    {
        var title = service.Store.Snapshot.Conversations.FirstOrDefault(conversation => conversation.Key == key)?.Title ?? key.Id;
        return $"{title}###SnowcloakChatPopout_{key}";
    }
}
