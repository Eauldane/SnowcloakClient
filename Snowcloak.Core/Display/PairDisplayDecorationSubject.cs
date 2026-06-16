namespace Snowcloak.Core.Display;

public readonly record struct PairDisplayDecorationSubject(
    bool IsSelf,
    bool IsKnownPair,
    bool IsPairingCandidate,
    bool IsApplicationBlocked,
    bool IsAutoPaused,
    bool CanUseVanityColour,
    PairDisplayColour? VanityColour);
