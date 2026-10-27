using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ElezenTools.Services;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Snowcloak.Core.Chat;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Configurations;
using Snowcloak.Utils;

namespace Snowcloak.Services.Chat;

public sealed class GameChatLogRenderer
{
    private readonly IChatGui _chatGui;
    private readonly ChatIdentityResolver _identityResolver;
    private readonly SnowcloakConfigService _configService;

    public GameChatLogRenderer(IChatGui chatGui, ChatIdentityResolver identityResolver, SnowcloakConfigService configService)
    {
        _chatGui = chatGui;
        _identityResolver = identityResolver;
        _configService = configService;
    }

    public void Render(ConversationKey key, string title, ChatEntry entry, bool isOutgoing)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var flattened = ChatMessageCodec.Flatten(entry.Segments, _identityResolver.ResolveName);
        var builder = new SeStringBuilder();
        var colour = _configService.Current.ChatReceivedMessageColour ?? SnowcloakConfig.DefaultChatReceivedMessageColour;
        var messageColours = new ElezenStrings.Colour(colour == 0 ? 0x010101u : colour);
        if (!isOutgoing)
        {
            builder.Append(ElezenStrings.BuildColourStartString(messageColours));
        }
        builder.AddText("[SnowChat] ")
            .Add(ChatUtils.CreateExtraChatTagPayload(key.ToString()));

        if (key.Kind is ConversationKind.Room or ConversationKind.Syncshell)
        {
            builder.AddText($"[{title}] ");
        }

        if (entry.Kind == ChatEntryKind.TurnChanged)
        {
            builder.AddText("Scene: " + entry.RawText);
            Print(builder, isOutgoing, messageColours);
            return;
        }

        if (entry.RpMode == RpChatMode.OutOfCharacter)
        {
            builder.AddText("((");
        }
        else if (entry.RpMode == RpChatMode.Action)
        {
            builder.AddText("* ");
        }

        var outgoingDirect = isOutgoing && key.Kind == ConversationKind.Direct;
        var display = outgoingDirect ? _identityResolver.Resolve(key.Id) ?? new SenderDisplay(title) : entry.Display;
        if (outgoingDirect)
        {
            builder.AddText(">> ");
        }

        if (ElezenStrings.TryBuildColours(display.ForegroundHex, display.GlowHex, out var colours))
        {
            builder.Append(ElezenStrings.BuildColouredString(display.Name, colours));
            if (!isOutgoing)
            {
                builder.Append(ElezenStrings.BuildColourStartString(messageColours));
            }
        }
        else
        {
            builder.AddText(display.Name);
        }

        builder.AddText(entry.RpMode switch
        {
            RpChatMode.InCharacter => " (IC): " + flattened,
            RpChatMode.OutOfCharacter => ": " + flattened + "))",
            RpChatMode.Action => " " + flattened,
            RpChatMode.Narration => " narrates: " + flattened,
            _ => ": " + flattened,
        });

        Print(builder, isOutgoing, messageColours);
    }

    private void Print(SeStringBuilder builder, bool isOutgoing, ElezenStrings.Colour messageColours)
    {
        if (!isOutgoing)
        {
            builder.Append(ElezenStrings.BuildColourEndString(messageColours));
        }
        _chatGui.Print(new XivChatEntry
        {
            Message = builder.BuiltString,
            Type = XivChatType.Echo,
        });
    }

}
