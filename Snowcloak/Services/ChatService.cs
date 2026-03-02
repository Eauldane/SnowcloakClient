using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ElezenTools.Services;
using ElezenTools.UI;
using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using Snowcloak.Interop;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

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
        _chatGui.CheckMessageHandled += HandleIncomingGameChatMessage;

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
        _chatGui.CheckMessageHandled -= HandleIncomingGameChatMessage;
        if (_gameChatHooks.IsValueCreated)
            _gameChatHooks.Value!.Dispose();
    }

    private void HandleUserChat(UserChatMsgMessage message)
    {
        if (_snowcloakConfig.Current.DisableChat)
            return;

        var chatMsg = message.ChatMsg;
        var senderDisplay = ResolveChatDisplayName(chatMsg.Sender);
        var senderColours = ResolveSenderDisplayColours(chatMsg.Sender, chatMsg.Sender.DisplayColour, chatMsg.Sender.DisplayGlowColour);
        PrintDirectChatMessage(senderDisplay, senderColours.Foreground, senderColours.Glow, chatMsg.PayloadContent);
    }

    public void PrintLocalUserChat(byte[] payloadContent)
    {
        if (_snowcloakConfig.Current.DisableChat)
            return;

        var senderDisplay = ResolveChatDisplayName(new UserData(_apiController.UID, _apiController.VanityId));
        PrintDirectChatMessage(senderDisplay, _apiController.DisplayColour, _apiController.DisplayGlowColour, payloadContent);
    }

    private void PrintDirectChatMessage(string senderDisplay, string? senderDisplayColour, string? senderGlowColour, byte[] payloadContent)
    {
        if (_snowcloakConfig.Current.DisableChat)
            return;

        var msg = new SeStringBuilder();
        msg.AddText("[SnowChat] ");
        msg.Append(BuildVanityColouredText(senderDisplay, senderDisplayColour, senderGlowColour));
        msg.AddText(": ");
        msg.Append(SeString.Parse(payloadContent));
        _chatGui.Print(new XivChatEntry{
            Message = msg.Build(),
            Name = string.Empty,
            Type = XivChatType.Yell
        });
    }

    private string ResolveChatDisplayName(UserData user)
    {
        var note = _serverConfigurationManager.GetNoteForUid(user.UID);
        if (string.IsNullOrWhiteSpace(note) && !string.IsNullOrWhiteSpace(user.Alias))
            note = _serverConfigurationManager.GetNoteForUid(user.Alias);
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
        var senderColours = ResolveSenderDisplayColours(chatMsg.Sender, chatMsg.Sender.DisplayColour, chatMsg.Sender.DisplayGlowColour);
        PrintGroupChatMessage(message.GroupInfo.GID, message.GroupInfo.Group.AliasOrGID, chatMsg.Sender, chatMsg.PayloadContent, senderColours.Foreground, senderColours.Glow);
    }

    public void PrintLocalGroupChat(GroupData group, byte[] payloadContent)
    {
        if (_snowcloakConfig.Current.DisableChat)
            return;

        var sender = new UserData(_apiController.UID, _apiController.VanityId, _apiController.DisplayColour, _apiController.DisplayGlowColour);
        PrintGroupChatMessage(group.GID, group.AliasOrGID, sender, payloadContent, _apiController.DisplayColour, _apiController.DisplayGlowColour);
    }

    private void PrintGroupChatMessage(string gid, string fallbackGroupName, UserData sender, byte[] payloadContent, string? senderDisplayColour, string? senderGlowColour)
    {
        var shellConfig = _serverConfigurationManager.GetShellConfigForGid(gid);
        if (!shellConfig.Enabled)
            return;

        ushort color = ResolveShellColor(shellConfig.Color);
        var extraChatTags = _snowcloakConfig.Current.ExtraChatTags;
        var logKind = ResolveShellLogKind(shellConfig.LogKind);

        var msg = new SeStringBuilder();
        if (extraChatTags)
        {
            msg.Add(ChatUtils.CreateExtraChatTagPayload(gid));
            msg.Add(RawPayload.LinkTerminator);
        }
        if (color != 0)
            msg.AddUiForeground(color);
        msg.AddText("[SnowChat] ");
        if (color != 0)
            msg.AddUiForegroundOff();
        var senderDisplay = ResolveChatDisplayName(sender);
        msg.Append(BuildVanityColouredText(senderDisplay, senderDisplayColour, senderGlowColour));
        if (color != 0)
            msg.AddUiForeground(color);
        var shellName = _serverConfigurationManager.GetNoteForGid(gid) ?? fallbackGroupName;
        msg.AddText($"@{shellName}: ");
        msg.Append(SeString.Parse(payloadContent));
        if (color != 0)
            msg.AddUiForegroundOff();

        _chatGui.Print(new XivChatEntry
        {
            Message = msg.Build(),
            Name = string.Empty,
            Type = logKind
        });
    }

    private static SeString BuildVanityColouredText(string text, string? foregroundHexColor, string? glowHexColor)
    {
        if (!TryBuildVanityColour(foregroundHexColor, glowHexColor, out var colors))
        {
            return new SeStringBuilder().AddText(text).Build();
        }

        return ElezenStrings.BuildColouredString(text, colors);
    }

    private static bool TryBuildVanityColour(string? foregroundHexColor, string? glowHexColor, out ElezenStrings.Colour colors)
    {
        colors = default;
        var hasForeground = TryParseBgrHex(foregroundHexColor, out var foregroundBgr);
        var hasGlow = TryParseBgrHex(glowHexColor, out var glowBgr);
        if (!hasForeground && !hasGlow)
        {
            return false;
        }

        colors = new ElezenStrings.Colour(Foreground: hasForeground ? foregroundBgr : 0u, Glow: hasGlow ? glowBgr : 0u);
        return true;
    }

    private static bool TryParseBgrHex(string? hexColor, out uint bgr)
    {
        bgr = 0u;
        if (string.IsNullOrWhiteSpace(hexColor))
        {
            return false;
        }

        var trimmed = hexColor.Trim().TrimStart('#');
        if (trimmed.Length != 6
            || !uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        var red = (parsed >> 16) & 0xFF;
        var green = (parsed >> 8) & 0xFF;
        var blue = parsed & 0xFF;
        bgr = (blue << 16) | (green << 8) | red;
        return true;
    }

    private (string? Foreground, string? Glow) ResolveSenderDisplayColours(UserData sender, string? preferredColour, string? preferredGlowColour)
    {
        if (string.Equals(sender.UID, _apiController.UID, StringComparison.Ordinal))
        {
            return (_apiController.DisplayColour, _apiController.DisplayGlowColour);
        }

        var pairData = _pairManager.GetPairByUID(sender.UID)?.UserData;
        if (pairData == null && !string.IsNullOrWhiteSpace(sender.Alias))
        {
            pairData = _pairManager.GetPairByUID(sender.Alias)?.UserData;
        }

        if (pairData != null
            && (!string.IsNullOrWhiteSpace(pairData.DisplayColour) || !string.IsNullOrWhiteSpace(pairData.DisplayGlowColour)))
        {
            return (pairData.DisplayColour, pairData.DisplayGlowColour);
        }

        if (!string.IsNullOrWhiteSpace(preferredColour) || !string.IsNullOrWhiteSpace(preferredGlowColour))
        {
            return (preferredColour, preferredGlowColour);
        }

        return (sender.DisplayColour, sender.DisplayGlowColour);
    }

    private void HandleIncomingGameChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (isHandled || !_snowcloakConfig.Current.ApplyVanityColoursToGameChat)
        {
            return;
        }

        if (!TryResolveVanityForGameChatSender(sender, message, out var foregroundHex, out var glowHex)
            || !TryBuildVanityColour(foregroundHex, glowHex, out var colours))
        {
            return;
        }

        var senderDisplayText = ResolvePreferredSenderDisplayName(sender, message);
        if (string.IsNullOrWhiteSpace(senderDisplayText))
        {
            return;
        }

        sender = ElezenStrings.BuildColouredString(senderDisplayText, colours);
    }

    private bool TryResolveVanityForGameChatSender(SeString sender, SeString message, out string? foregroundHex, out string? glowHex)
    {
        foregroundHex = null;
        glowHex = null;

        foreach (var senderIdent in EnumerateSenderIdents(sender, message))
        {
            var pairByIdent = _pairManager.GetOnlineUserPairs()
                .FirstOrDefault(pair => string.Equals(pair.Ident, senderIdent, StringComparison.Ordinal));
            if (pairByIdent != null)
            {
                foregroundHex = pairByIdent.UserData.DisplayColour;
                glowHex = pairByIdent.UserData.DisplayGlowColour;
                if (!string.IsNullOrWhiteSpace(foregroundHex) || !string.IsNullOrWhiteSpace(glowHex))
                {
                    return true;
                }
            }
        }

        var normalizedSenders = EnumerateSenderNameCandidates(sender, message);
        if (normalizedSenders.Count == 0)
        {
            return false;
        }

        var localName = NormalizeChatSenderName(_dalamudUtil.GetPlayerName());
        if (normalizedSenders.Any(normalizedSender => IsSenderNameMatch(normalizedSender, localName)))
        {
            foregroundHex = _apiController.DisplayColour;
            glowHex = _apiController.DisplayGlowColour;
            if (!string.IsNullOrWhiteSpace(foregroundHex) || !string.IsNullOrWhiteSpace(glowHex))
            {
                return true;
            }
        }

        foreach (var pair in _pairManager.GetOnlineUserPairs())
        {
            var pairName = NormalizeChatSenderName(pair.GetPlayerName());
            if (!normalizedSenders.Any(normalizedSender => IsSenderNameMatch(normalizedSender, pairName)))
            {
                continue;
            }

            foregroundHex = pair.UserData.DisplayColour;
            glowHex = pair.UserData.DisplayGlowColour;
            if (!string.IsNullOrWhiteSpace(foregroundHex) || !string.IsNullOrWhiteSpace(glowHex))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateSenderIdents(SeString sender, SeString message)
    {
        HashSet<string> yielded = new(StringComparer.Ordinal);
        foreach (var playerPayload in EnumeratePlayerPayloads(sender, message))
        {
            if (!TryResolveIdentFromPlayerPayload(playerPayload, out var ident))
            {
                continue;
            }

            if (yielded.Add(ident))
            {
                yield return ident;
            }
        }
    }

    private static bool TryResolveIdentFromPlayerPayload(PlayerPayload playerPayload, out string ident)
    {
        ident = string.Empty;
        var playerName = playerPayload.PlayerName?.Trim();
        if (string.IsNullOrWhiteSpace(playerName) || playerPayload.World.RowId == 0)
        {
            return false;
        }

        ident = (playerName + playerPayload.World.RowId).GetHash256();
        return !string.IsNullOrWhiteSpace(ident);
    }

    private static List<string> EnumerateSenderNameCandidates(SeString sender, SeString message)
    {
        HashSet<string> normalizedSenderNames = new(StringComparer.OrdinalIgnoreCase);

        AddNormalizedSender(normalizedSenderNames, sender.TextValue);
        foreach (var playerPayload in EnumeratePlayerPayloads(sender, message))
        {
            AddNormalizedSender(normalizedSenderNames, playerPayload.PlayerName);
            AddNormalizedSender(normalizedSenderNames, playerPayload.DisplayedName);
        }

        return normalizedSenderNames.ToList();
    }

    private static void AddNormalizedSender(HashSet<string> normalizedSenderNames, string? rawName)
    {
        var normalized = NormalizeChatSenderName(rawName);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            normalizedSenderNames.Add(normalized);
        }
    }

    private static string ResolvePreferredSenderDisplayName(SeString sender, SeString message)
    {
        if (!string.IsNullOrWhiteSpace(sender.TextValue))
        {
            return sender.TextValue;
        }

        var playerPayload = EnumeratePlayerPayloads(sender, message).FirstOrDefault();
        if (playerPayload == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(playerPayload.DisplayedName))
        {
            return playerPayload.DisplayedName;
        }

        return playerPayload.PlayerName ?? string.Empty;
    }

    private static IEnumerable<PlayerPayload> EnumeratePlayerPayloads(SeString sender, SeString message)
    {
        foreach (var senderPayload in sender.Payloads.OfType<PlayerPayload>())
        {
            yield return senderPayload;
        }

        foreach (var messagePayload in message.Payloads.OfType<PlayerPayload>())
        {
            yield return messagePayload;
        }
    }

    private static bool IsSenderNameMatch(string normalizedSender, string normalizedCandidate)
    {
        if (string.IsNullOrWhiteSpace(normalizedSender) || string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return false;
        }

        return normalizedSender.Equals(normalizedCandidate, StringComparison.OrdinalIgnoreCase)
               || normalizedSender.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase)
               || normalizedCandidate.Contains(normalizedSender, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeChatSenderName(string? rawSender)
    {
        if (string.IsNullOrWhiteSpace(rawSender))
        {
            return string.Empty;
        }

        var sender = rawSender.Trim();
        if (sender.StartsWith("From ", StringComparison.OrdinalIgnoreCase))
        {
            sender = sender[5..];
        }
        else if (sender.StartsWith("To ", StringComparison.OrdinalIgnoreCase))
        {
            sender = sender[3..];
        }

        var worldSeparator = sender.IndexOf('@');
        if (worldSeparator >= 0)
        {
            sender = sender[..worldSeparator];
        }

        sender = sender.Trim(' ', '<', '>', '[', ']', '(', ')');
        if (sender.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder normalized = new(sender.Length);
        foreach (var c in sender)
        {
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '\'' or '-' or '.')
            {
                normalized.Append(char.ToUpperInvariant(c));
            }
        }

        return normalized.ToString().Trim();
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

        if (TryResolveSyncshellByNumber(shellNumber, out _, out var shellDisplayName))
        {
            // BUG: This doesn't always update the chat window e.g. when renaming a group
            _gameChatHooks.Value.ChatChannelOverride = new()
            {
                ChannelName = $"SS [{shellNumber}]: {shellDisplayName}",
                ChatMessageHandler = chatBytes => SendChatShell(shellNumber, chatBytes)
            };
            return;
        }

        _chatGui.PrintError(string.Format(CultureInfo.InvariantCulture, "[Snowcloak] Syncshell number #{0} not found", shellNumber));
        
    }

    public void SendChatShell(int shellNumber, byte[] chatBytes)
    {
        if (_snowcloakConfig.Current.DisableChat)
            return;

        if (chatBytes.Length == 0)
            return;

        _ = Task.Run(() => SendChatShellAsync(shellNumber, chatBytes));
    }

    private bool TryResolveSyncshellByNumber(int shellNumber, out GroupData groupData, out string shellDisplayName)
    {
        foreach (var group in _pairManager.Groups)
        {
            var shellConfig = _serverConfigurationManager.GetShellConfigForGid(group.Key.GID);
            if (shellConfig.Enabled && shellConfig.ShellNumber == shellNumber)
            {
                groupData = group.Key;
                shellDisplayName = _serverConfigurationManager.GetNoteForGid(group.Key.GID) ?? group.Key.AliasOrGID;
                return true;
            }
        }

        groupData = default!;
        shellDisplayName = string.Empty;
        return false;
    }

    private async Task SendChatShellAsync(int shellNumber, byte[] chatBytes)
    {
        if (!TryResolveSyncshellByNumber(shellNumber, out var group, out _))
        {
            _chatGui.PrintError(string.Format(CultureInfo.InvariantCulture, "[Snowcloak] Syncshell number #{0} not found", shellNumber));
            return;
        }

        try
        {
            await _apiController.GroupChatJoin(new(group)).ConfigureAwait(false);

            // Should cache the name and home world instead of fetching it every time.
            var chatMsg = await Service.UseFramework(() => new ChatMessage
            {
                SenderName = _dalamudUtil.GetPlayerName(),
                SenderHomeWorldId = _dalamudUtil.GetHomeWorldId(),
                PayloadContent = chatBytes
            }).ConfigureAwait(false);

            await _apiController.GroupChatSendMsg(new(group), chatMsg).ConfigureAwait(false);

            var sender = new UserData(_apiController.UID, _apiController.VanityId, _apiController.DisplayColour, _apiController.DisplayGlowColour);
            var localEcho = new SignedChatMessage(chatMsg, sender)
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            Mediator.Publish(new GroupChatMsgMessage(new(group), localEcho));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send /ss{ShellNumber} message", shellNumber);
            _chatGui.PrintError($"[Snowcloak] Failed to send message to /ss{shellNumber}.");
        }
    }
}
