using System.Numerics;
using Snowcloak.API.Data;
using ElezenTools.UI;
using Snowcloak.API.Data.Extensions;
using Snowcloak.Configuration.Configurations;
using Snowcloak.Core.Display;
using Snowcloak.PlayerData.Pairs;

namespace Snowcloak.Services;

internal static class PairDisplayDecorationMapper
{
    public static PairDisplayDecorationOptions CreateOptions(
        SnowcloakConfig config,
        bool inRestrictedContent,
        bool usePairColours,
        bool usePairingHighlights)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new PairDisplayDecorationOptions(
            inRestrictedContent,
            usePairColours,
            usePairingHighlights,
            FromElezenColour(config.NameColors),
            FromElezenColour(config.BlockedNameColors),
            FromElezenColour(config.PairRequestNameColors));
    }

    public static PairDisplayDecorationSubject CreatePairSubject(Pair pair, bool allowPairVanity)
    {
        ArgumentNullException.ThrowIfNull(pair);

        PairDisplayColour? vanityColour = null;
        if (allowPairVanity && TryGetPairVanityColour(pair, out var pairVanityColour))
            vanityColour = pairVanityColour;

        return new PairDisplayDecorationSubject(
            IsSelf: false,
            IsKnownPair: true,
            IsPairingCandidate: false,
            pair.IsApplicationBlocked,
            pair.IsAutoPaused,
            vanityColour.HasValue,
            vanityColour);
    }

    public static PairDisplayDecorationSubject CreatePairingCandidateSubject()
    {
        return new PairDisplayDecorationSubject(
            IsSelf: false,
            IsKnownPair: false,
            IsPairingCandidate: true,
            IsApplicationBlocked: false,
            IsAutoPaused: false,
            CanUseVanityColour: false,
            VanityColour: null);
    }

    public static PairDisplayDecorationSubject CreateSelfSubject(string? displayColour, string? glowColour)
    {
        var hasVanity = TryReadColour(displayColour, glowColour, out var vanityColour);
        return new PairDisplayDecorationSubject(
            IsSelf: true,
            IsKnownPair: false,
            IsPairingCandidate: false,
            IsApplicationBlocked: false,
            IsAutoPaused: false,
            hasVanity,
            hasVanity ? vanityColour : null);
    }

    public static PairDisplayDecorationSubject CreateUserDataSubject(UserData userData)
    {
        ArgumentNullException.ThrowIfNull(userData);

        var hasVanity = TryReadColour(userData.DisplayColour, userData.DisplayGlowColour, out var vanityColour);
        return new PairDisplayDecorationSubject(
            IsSelf: false,
            IsKnownPair: true,
            IsPairingCandidate: false,
            IsApplicationBlocked: false,
            IsAutoPaused: false,
            hasVanity,
            hasVanity ? vanityColour : null);
    }

    public static ElezenStrings.Colour ToElezenColour(PairDisplayColour colour)
    {
        return new ElezenStrings.Colour(colour.Foreground, colour.Glow);
    }

    public static (Vector4? Foreground, Vector4? Glow) ToVectorColours(PairDisplayDecoration? decoration)
    {
        if (decoration == null)
            return (null, null);

        var colour = ToElezenColour(decoration.Value.Colour);
        return (ToVectorColour(colour.Foreground), ToVectorColour(colour.Glow));
    }

    private static bool TryGetPairVanityColour(Pair pair, out PairDisplayColour colour)
    {
        colour = default;
        if (!IsPairedForVanity(pair) || pair.IsPaused)
            return false;

        return TryReadColour(pair.UserData.DisplayColour, pair.UserData.DisplayGlowColour, out colour);
    }

    private static bool TryReadColour(string? displayColour, string? glowColour, out PairDisplayColour colour)
    {
        colour = default;
        if (!ElezenStrings.TryBuildColours(displayColour, glowColour, out var elezenColour))
            return false;

        colour = FromElezenColour(elezenColour);
        return true;
    }

    private static PairDisplayColour FromElezenColour(ElezenStrings.Colour colour)
    {
        return new PairDisplayColour(colour.Foreground, colour.Glow);
    }

    private static Vector4 ToVectorColour(uint colour)
    {
        return new Vector4(
            (byte)colour / 255.0f,
            (byte)(colour >> 8) / 255.0f,
            (byte)(colour >> 16) / 255.0f,
            1.0f);
    }

    private static bool IsPairedForVanity(Pair pair)
    {
        if (pair.UserPair != null)
        {
            return pair.UserPair.OtherPermissions.IsPaired()
                   && pair.UserPair.OwnPermissions.IsPaired();
        }

        return !pair.GroupPair.IsEmpty;
    }
}
