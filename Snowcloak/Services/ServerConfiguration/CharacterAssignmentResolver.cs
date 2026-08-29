using Snowcloak.Configuration.Models;
using Snowcloak.Core.PlayerData;
using Snowcloak.API.Protocol;

namespace Snowcloak.Services.ServerConfiguration;

internal static class CharacterAssignmentResolver
{
    public static Authentication? Resolve(IEnumerable<Authentication> assignments, CharacterIdentity character, out bool ambiguous)
    {
        var candidates = assignments.ToList();
        var matches = character.ContentId == 0
            ? []
            : candidates.Where(a => a.ContentId == character.ContentId).ToList();

        if (matches.Count == 0)
            matches = candidates.Where(a => a.ContentId == 0 && MatchesLegacy(a, character)).ToList();

        ambiguous = matches.Count > 1;
        return matches.Count == 1 ? matches[0] : null;
    }

    public static bool MatchesLegacy(Authentication assignment, CharacterIdentity character)
        => character.HomeWorldId != 0 && !string.IsNullOrWhiteSpace(character.Name)
            && string.Equals(assignment.CharacterName, character.Name, StringComparison.Ordinal)
            && assignment.WorldId == character.HomeWorldId;

    public static bool UpdateIdentity(Authentication assignment, CharacterIdentity character)
    {
        if (!character.IsValid || (assignment.ContentId != 0 && assignment.ContentId != character.ContentId))
            return false;

        var changed = assignment.ContentId != character.ContentId
            || !string.Equals(assignment.CharacterName, character.Name, StringComparison.Ordinal)
            || assignment.WorldId != character.HomeWorldId;
        if (changed && !assignment.IdentityBindingId.HasValue)
        {
            var aliases = assignment.PendingLegacyIdents.ToHashSet(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(assignment.CharacterName) && assignment.WorldId != 0)
                aliases.Add(CharacterIdentityProtocol.LegacyIdent(assignment.CharacterName, assignment.WorldId));
            aliases.Add(CharacterIdentityProtocol.LegacyIdent(character.Name, character.HomeWorldId));
            if (aliases.Count > CharacterIdentityProtocol.MaximumLegacyAliases)
                throw new InvalidOperationException("Complete the pending server identity migration before changing this assignment again.");
            assignment.PendingLegacyIdents = aliases.Order(StringComparer.Ordinal).ToList();
        }
        assignment.ContentId = character.ContentId;
        // Keep the old-build lookup fields useful after a rename or homeworld transfer.
        assignment.CharacterName = character.Name;
        assignment.WorldId = character.HomeWorldId;
        return changed;
    }
}
