using Snowcloak.API.Dto.Account;

namespace Snowcloak.Core.Accounts;

public static class AccountSecretKeySelectionPolicy
{
    public static int GetAssignmentRank(AccountSecretKeyDto key, string? preferredUid)
    {
        ArgumentNullException.ThrowIfNull(key);

        var rank = string.Equals(key.Source, "generated", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (!string.IsNullOrWhiteSpace(preferredUid)
            && string.Equals(key.Uid, preferredUid.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            rank += 2;
        }

        return rank;
    }

    public static DateTimeOffset GetActivityTime(AccountSecretKeyDto key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.LastUsedAtUtc ?? key.CreatedAtUtc;
    }

    public static bool IsBetterAssignmentCandidate(AccountSecretKeyDto candidate, AccountSecretKeyDto? current,
        string? preferredUid)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (current == null)
        {
            return true;
        }

        var candidateRank = GetAssignmentRank(candidate, preferredUid);
        var currentRank = GetAssignmentRank(current, preferredUid);
        if (candidateRank != currentRank)
        {
            return candidateRank > currentRank;
        }

        return GetActivityTime(candidate) > GetActivityTime(current);
    }
}
