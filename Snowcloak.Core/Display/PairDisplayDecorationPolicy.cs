namespace Snowcloak.Core.Display;

public static class PairDisplayDecorationPolicy
{
    public static PairDisplayDecoration? Resolve(
        PairDisplayDecorationOptions options,
        PairDisplayDecorationSubject subject)
    {
        if (options.InRestrictedContent)
            return null;

        if (subject is { IsSelf: true, VanityColour: { } selfVanity })
            return new PairDisplayDecoration(PairDisplayDecorationKind.SelfVanity, selfVanity);

        if (subject.IsKnownPair)
        {
            if (subject is { CanUseVanityColour: true, VanityColour: { } pairVanity })
            {
                return subject.IsAutoPaused
                    ? new PairDisplayDecoration(PairDisplayDecorationKind.Blocked, options.BlockedColour)
                    : new PairDisplayDecoration(PairDisplayDecorationKind.PairVanity, pairVanity);
            }

            if (options.UsePairColours)
            {
                return subject.IsApplicationBlocked || subject.IsAutoPaused
                    ? new PairDisplayDecoration(PairDisplayDecorationKind.Blocked, options.BlockedColour)
                    : new PairDisplayDecoration(PairDisplayDecorationKind.Pair, options.PairColour);
            }
        }

        if (subject.IsPairingCandidate && options.UsePairingHighlights)
            return new PairDisplayDecoration(PairDisplayDecorationKind.PairRequest, options.PairRequestColour);

        return null;
    }
}
