namespace Snowcloak.Core.Display;

public readonly record struct PairDisplayDecorationOptions(
    bool InRestrictedContent,
    bool UsePairColours,
    bool UsePairingHighlights,
    PairDisplayColour PairColour,
    PairDisplayColour BlockedColour,
    PairDisplayColour PairRequestColour);
