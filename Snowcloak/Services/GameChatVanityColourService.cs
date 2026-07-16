using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ElezenTools.Services;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Text;

namespace Snowcloak.Services;

public sealed class GameChatVanityColourService : IDisposable
{
    private readonly ApiController _apiController;
    private readonly IChatGui _chatGui;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly PairManager _pairManager;
    private readonly SnowcloakConfigService _snowcloakConfig;

    public GameChatVanityColourService(IChatGui chatGui, DalamudUtilService dalamudUtil,
        PairManager pairManager, ApiController apiController, SnowcloakConfigService snowcloakConfig)
    {
        _chatGui = chatGui;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _apiController = apiController;
        _snowcloakConfig = snowcloakConfig;
        _chatGui.ChatMessage += HandleIncomingGameChatMessage;
    }

    public void Dispose()
    {
        _chatGui.ChatMessage -= HandleIncomingGameChatMessage;
    }

    private void HandleIncomingGameChatMessage(IHandleableChatMessage chatMessage)
    {
        if (chatMessage.IsHandled || !_snowcloakConfig.Current.ApplyVanityColoursToGameChat)
        {
            return;
        }

        if (chatMessage is not IMutableChatMessage mutableChatMessage)
        {
            return;
        }

        if (!TryResolveVanityForGameChatSender(chatMessage.Sender, chatMessage.Message, out var foregroundHex, out var glowHex)
            || !ElezenStrings.TryBuildColours(foregroundHex, glowHex, out var colours))
        {
            return;
        }

        var colouredSender = BuildVanityColouredSender(chatMessage.Sender, colours);
        if (colouredSender != null)
        {
            mutableChatMessage.Sender = colouredSender;
        }
    }

    private bool TryResolveVanityForGameChatSender(SeString sender, SeString message, out string? foregroundHex, out string? glowHex)
    {
        foregroundHex = null;
        glowHex = null;

        foreach (var senderIdent in EnumerateSenderIdents(sender, message))
        {
            var pairByIdent = _pairManager.GetOnlineUserPairs()
                .FirstOrDefault(pair => string.Equals(pair.Ident, senderIdent, StringComparison.Ordinal));
            if (pairByIdent == null)
            {
                continue;
            }

            foregroundHex = pairByIdent.UserData.DisplayColour;
            glowHex = pairByIdent.UserData.DisplayGlowColour;
            if (!string.IsNullOrWhiteSpace(foregroundHex) || !string.IsNullOrWhiteSpace(glowHex))
            {
                return true;
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
            if (TryResolveIdentFromPlayerPayload(playerPayload, out var ident) && yielded.Add(ident))
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

    private static SeString? BuildVanityColouredSender(SeString sender, ElezenStrings.Colour colours)
    {
        if (sender.Payloads.Count == 0)
        {
            return string.IsNullOrWhiteSpace(sender.TextValue)
                ? null
                : ElezenStrings.BuildColouredString(sender.TextValue, colours);
        }

        var builder = new SeStringBuilder();
        builder.Append(ElezenStrings.BuildColourStartString(colours));
        builder.Append(sender);
        builder.Append(ElezenStrings.BuildColourEndString(colours));
        return builder.Build();
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

        var worldSeparator = sender.IndexOf('@', StringComparison.Ordinal);
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
        foreach (var character in sender)
        {
            if (char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || character is '\'' or '-' or '.')
            {
                normalized.Append(char.ToUpperInvariant(character));
            }
        }

        return normalized.ToString().Trim();
    }
}
