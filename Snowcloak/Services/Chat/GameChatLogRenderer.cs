using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ElezenTools.Services;
using ElezenTools.UI;
using Snowcloak.Core.Chat;
using Snowcloak.Utils;

namespace Snowcloak.Services.Chat;

public sealed class GameChatLogRenderer
{
    private readonly IChatGui _chatGui;
    private readonly ChatIdentityResolver _identityResolver;

    public GameChatLogRenderer(IChatGui chatGui, ChatIdentityResolver identityResolver)
    {
        _chatGui = chatGui;
        _identityResolver = identityResolver;
    }

    public void Render(ConversationKey key, string title, ChatEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var flattened = ChatMessageCodec.Flatten(entry.Segments, _identityResolver.ResolveName);
        var builder = new SeStringBuilder()
            .AddText("[SnowChat] ")
            .Add(ChatUtils.CreateExtraChatTagPayload(key.ToString()))
            .AddText($"[{title}] ");

        if (ElezenStrings.TryBuildColours(entry.Display.ForegroundHex, entry.Display.GlowHex, out var colours))
        {
            builder.Append(ElezenStrings.BuildColouredString(entry.Display.Name, colours));
        }
        else
        {
            builder.AddText(entry.Display.Name);
        }

        builder.AddText(entry.IsEmote
            ? " " + flattened[4..]
            : ": " + flattened);

        _chatGui.Print(new XivChatEntry
        {
            Message = builder.BuiltString,
            Type = XivChatType.Echo,
        });
    }
}
