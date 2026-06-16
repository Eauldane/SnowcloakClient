using System.Globalization;

namespace Snowcloak.Core.Pairing;

public static class PairRequestAutoRejectPolicy
{
    public static PairRequestFilterResult Evaluate(
        PairRequestFilterSettings settings,
        PairRequestFilterCandidate candidate,
        bool deferIfUnavailable)
    {
        if (!settings.PairingEnabled || !settings.HasAnyFilter)
            return PairRequestFilterResult.Accept;

        if (!candidate.IsAvailable)
            return Unavailable("requester unavailable for filtering", deferIfUnavailable);

        if (settings.FriendsOnly && !candidate.IsFriend)
            return PairRequestFilterResult.Reject("Auto rejected: This user is only accepting pair requests from friends.");

        var minimumLevel = Math.Max(0, settings.MinimumLevel);
        if (minimumLevel > 0)
        {
            if (!candidate.Level.HasValue)
                return Unavailable("requester level unavailable", deferIfUnavailable);

            if (candidate.Level.Value < minimumLevel)
                return PairRequestFilterResult.Reject($"Auto rejected: This user isn't interested in pairing with users below level {minimumLevel}.");
        }

        if (settings.RejectedHomeworlds.Count > 0)
        {
            if (!candidate.HomeWorldId.HasValue)
                return Unavailable("requester homeworld unavailable", deferIfUnavailable);

            if (settings.RejectedHomeworlds.Contains(candidate.HomeWorldId.Value))
            {
                var homeworldName = string.IsNullOrWhiteSpace(candidate.HomeWorldName)
                    ? candidate.HomeWorldId.Value.ToString(CultureInfo.InvariantCulture)
                    : candidate.HomeWorldName;
                return PairRequestFilterResult.Reject($"Auto rejected: This user isn't interested in pairing with users from {homeworldName}.");
            }
        }

        if (settings.RejectedAppearances.Count == 0)
            return PairRequestFilterResult.Accept;

        if (!candidate.Appearance.HasValue)
            return Unavailable("appearance unavailable", deferIfUnavailable);

        var appearance = candidate.Appearance.Value;
        if (appearance.Gender.HasValue && appearance.Race.HasValue && appearance.Clan.HasValue)
        {
            var key = new PairRequestAppearanceFilter(appearance.Race.Value, appearance.Clan.Value, appearance.Gender.Value);
            if (settings.RejectedAppearances.Contains(key))
                return PairRequestFilterResult.Reject("Auto rejected: This user isn't interested in your vanilla gender/clan combination.");
        }

        return PairRequestFilterResult.Accept;
    }

    private static PairRequestFilterResult Unavailable(string reason, bool deferIfUnavailable)
        => deferIfUnavailable
            ? PairRequestFilterResult.Defer
            : PairRequestFilterResult.Reject($"Auto rejected: {reason}");
}
