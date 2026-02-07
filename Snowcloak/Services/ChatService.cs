using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ElezenTools.Services;
using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using Snowcloak.Interop;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Globalization;

namespace Snowcloak.Services;

public class ChatService : DisposableMediatorSubscriberBase
{
    public const int DefaultColor = 710;
    public const int CommandMaxNumber = 50;

    private readonly ILogger<ChatService> _logger;
    private readonly IChatGui _chatGui;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SnowcloakConfigService _snowcloakConfig;
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigurationManager;

    private readonly Lazy<GameChatHooks> _gameChatHooks;

    public ChatService(ILogger<ChatService> logger, DalamudUtilService dalamudUtil, SnowMediator mediator, ApiController apiController,
        PairManager pairManager, ILoggerFactory loggerFactory, IGameInteropProvider gameInteropProvider, IChatGui chatGui,
        SnowcloakConfigService snowcloakConfig, ServerConfigurationManager serverConfigurationManager) : base(logger, mediator)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _chatGui = chatGui;
        _snowcloakConfig = snowcloakConfig;
        _apiController = apiController;
        _pairManager = pairManager;
        _serverConfigurationManager = serverConfigurationManager;

        Mediator.Subscribe<UserChatMsgMessage>(this, HandleUserChat);
        Mediator.Subscribe<GroupChatMsgMessage>(this, HandleGroupChat);

        _gameChatHooks = new(() => new GameChatHooks(loggerFactory.CreateLogger<GameChatHooks>(), gameInteropProvider, SendChatShell));

        // Initialize chat hooks in advance
        _ = Task.Run(() =>
        {
            try
            {
                _ = _gameChatHooks.Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize chat hooks");
            }
        });
    }
    
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (_gameChatHooks.IsValueCreated)
            _gameChatHooks.Value!.Dispose();
    }

    private void HandleUserChat(UserChatMsgMessage message)
    {
        var chatMsg = message.ChatMsg;
        var senderDisplay = ResolveChatDisplayName(chatMsg.Sender);
        var prefix = new SeStringBuilder();
        prefix.AddText("[SnowChat] ");
        _chatGui.Print(new XivChatEntry{
            MessageBytes = [..prefix.Build().Encode(), ..message.ChatMsg.PayloadContent],
            Name = senderDisplay,
            Type = XivChatType.Yell
        });
    }

    private string ResolveChatDisplayName(UserData user)
    {
        var note = _serverConfigurationManager.GetNoteForUid(user.UID);
        if (!string.IsNullOrWhiteSpace(note))
            return note;
        if (!string.IsNullOrWhiteSpace(user.Alias))
            return user.Alias;
        return user.UID;
    }

    private ushort ResolveShellColor(int shellColor)
    {
        if (shellColor != 0)
            return (ushort)shellColor;
        var globalColor = _snowcloakConfig.Current.ChatColor;
        if (globalColor != 0)
            return (ushort)globalColor;
        return (ushort)DefaultColor;
    }

    private XivChatType ResolveShellLogKind(int shellLogKind)
    {
        if (shellLogKind != 0)
            return (XivChatType)shellLogKind;
        return (XivChatType)_snowcloakConfig.Current.ChatLogKind;
    }

    private void HandleGroupChat(GroupChatMsgMessage message)
    {
        if (_snowcloakConfig.Current.DisableChat)
            return;

        var chatMsg = message.ChatMsg;
        var shellConfig = _serverConfigurationManager.GetShellConfigForGid(message.GroupInfo.GID);
        var shellNumber = shellConfig.ShellNumber;

        if (!shellConfig.Enabled)
            return;

        ushort color = ResolveShellColor(shellConfig.Color);
        var extraChatTags = _snowcloakConfig.Current.ExtraChatTags;
        var logKind = ResolveShellLogKind(shellConfig.LogKind);

        var msg = new SeStringBuilder();
        if (extraChatTags)
        {
            msg.Add(ChatUtils.CreateExtraChatTagPayload(message.GroupInfo.GID));
            msg.Add(RawPayload.LinkTerminator);
        }
        if (color != 0)
            msg.AddUiForeground((ushort)color);
        msg.AddText("[SnowChat] ");
        if (message.ChatMsg.Sender.UID.Equals(_apiController.UID, StringComparison.Ordinal))
        {
            // Don't link to your own character
            msg.AddText(chatMsg.SenderName);
        }
        else
        {
            msg.Add(new PlayerPayload(chatMsg.Sender.AliasOrUID, chatMsg.SenderHomeWorldId));
        }
        var shellName = _serverConfigurationManager.GetNoteForGid(message.GroupInfo.GID) ?? message.GroupInfo.Group.AliasOrGID;
        msg.AddText($"@{shellName}: ");
        msg.Append(SeString.Parse(message.ChatMsg.PayloadContent));
        if (color != 0)
            msg.AddUiForegroundOff();

        _chatGui.Print(new XivChatEntry{
            Message = msg.Build(),
            Name = chatMsg.SenderName,
            Type = logKind
        });
    }

    // Print an example message to the configured global chat channel
    public void PrintChannelExample(string message, string gid = "")
    {
        int chatType = _snowcloakConfig.Current.ChatLogKind;

        foreach (var group in _pairManager.Groups)
        {
            if (group.Key.GID.Equals(gid, StringComparison.Ordinal))
            {
                int shellChatType = _serverConfigurationManager.GetShellConfigForGid(gid).LogKind;
                if (shellChatType != 0)
                    chatType = shellChatType;
            }
        }

        _chatGui.Print(new XivChatEntry{
            Message = message,
            Name = "",
            Type = (XivChatType)chatType
        });
    }

    // Called to update the active chat shell name if its renamed
    public void MaybeUpdateShellName(int shellNumber)
    {
        if (_snowcloakConfig.Current.DisableChat)
            return;

        foreach (var group in _pairManager.Groups)
        {
            var shellConfig = _serverConfigurationManager.GetShellConfigForGid(group.Key.GID);
            if (shellConfig.Enabled && shellConfig.ShellNumber == shellNumber)
            {
                if (_gameChatHooks.IsValueCreated && _gameChatHooks.Value.ChatChannelOverride != null)
                {
                    // Very dumb and won't handle re-numbering -- need to identify the active chat channel more reliably later
                    if (_gameChatHooks.Value.ChatChannelOverride.ChannelName.StartsWith($"SS [{shellNumber}]", StringComparison.Ordinal))
                        SwitchChatShell(shellNumber);
                }
            }
        }
    }

    public void SwitchChatShell(int shellNumber)
    {
        if (_snowcloakConfig.Current.DisableChat)
            return;

        foreach (var group in _pairManager.Groups)
        {
            var shellConfig = _serverConfigurationManager.GetShellConfigForGid(group.Key.GID);
            if (shellConfig.Enabled && shellConfig.ShellNumber == shellNumber)
            {
                var name = _serverConfigurationManager.GetNoteForGid(group.Key.GID) ?? group.Key.AliasOrGID;
                // BUG: This doesn't always update the chat window e.g. when renaming a group
                _gameChatHooks.Value.ChatChannelOverride = new()
                {
                    ChannelName = $"SS [{shellNumber}]: {name}",
                    ChatMessageHandler = chatBytes => SendChatShell(shellNumber, chatBytes)
                };
                return;
            }
        }

        _chatGui.PrintError(string.Format(CultureInfo.InvariantCulture, "[Snowcloak] Syncshell number #{0} not found", shellNumber));
        
    }

    public void SendChatShell(int shellNumber, byte[] chatBytes)
    {
        if (_snowcloakConfig.Current.DisableChat)
            return;

        foreach (var group in _pairManager.Groups)
        {
            var shellConfig = _serverConfigurationManager.GetShellConfigForGid(group.Key.GID);
            if (shellConfig.Enabled && shellConfig.ShellNumber == shellNumber)
            {
                _ = Task.Run(async () => {
                    // Should cache the name and home world instead of fetching it every time
                    var chatMsg = await Service.UseFramework(() => {
                        return new ChatMessage()
                        {
                            SenderName = _dalamudUtil.GetPlayerName(),
                            SenderHomeWorldId = _dalamudUtil.GetHomeWorldId(),
                            PayloadContent = chatBytes
                        };
                    }).ConfigureAwait(false);
                    await _apiController.GroupChatSendMsg(new(group.Key), chatMsg).ConfigureAwait(false);
                }).ConfigureAwait(false);
                return;
            }
        }

        _chatGui.PrintError(string.Format(CultureInfo.InvariantCulture, "[Snowcloak] Syncshell number #{0} not found", shellNumber));
    }
}
