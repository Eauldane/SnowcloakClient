using System.Globalization;

namespace Snowcloak.Core.Pairing;

public static class AvailabilityFilter
{
    public static List<AvailabilityRow> Apply(IReadOnlyList<AvailabilityRow> rows,
        bool onlyWithProfiles, string? searchQuery, string? tagQuery)
    {
        var search = ProfileTagText.NormalizeForLookup(searchQuery);
        var tag = ProfileTagText.NormalizeForLookup(tagQuery);

        var result = new List<AvailabilityRow>(rows.Count);
        foreach (var row in rows)
        {
            if (onlyWithProfiles && !HasMeaningfulProfile(row))
                continue;
            if (search.Length > 0 && !MatchesSearch(row, search))
                continue;
            if (tag.Length > 0 && !MatchesRequiredTag(row, tag))
                continue;
            result.Add(row);
        }

        return result;
    }

    public static bool HasMeaningfulProfile(AvailabilityRow row)
    {
        var profile = row.Profile;
        return profile != null
               && (!string.IsNullOrWhiteSpace(profile.CharacterName)
                   || !string.IsNullOrWhiteSpace(profile.Title)
                   || !string.IsNullOrWhiteSpace(profile.Pronouns)
                   || !string.IsNullOrWhiteSpace(profile.Tagline)
                   || !string.IsNullOrWhiteSpace(profile.RpStatus)
                   || !string.IsNullOrWhiteSpace(profile.Approachability)
                   || profile.Hooks.Count > 0
                   || row.VisibleTags.Count > 0);
    }

    /// <param name="normalizedQuery">A query already run through <see cref="ProfileTagText.NormalizeForLookup"/>.</param>
    public static bool MatchesSearch(AvailabilityRow row, string normalizedQuery)
    {
        return EnumerateSearchFields(row)
            .Select(ProfileTagText.NormalizeForLookup)
            .Any(field => field.Contains(normalizedQuery, StringComparison.Ordinal));
    }

    /// <param name="normalizedQuery">A query already run through <see cref="ProfileTagText.NormalizeForLookup"/>.</param>
    public static bool MatchesRequiredTag(AvailabilityRow row, string normalizedQuery)
    {
        return row.VisibleTags.Any(tag =>
            ProfileTagText.NormalizeForLookup(tag.Value).Contains(normalizedQuery, StringComparison.Ordinal)
            || ProfileTagText.NormalizeForLookup(ProfileTagText.GetTypeLabel(tag.Type)).Contains(normalizedQuery, StringComparison.Ordinal)
            || ProfileTagText.NormalizeForLookup($"{ProfileTagText.GetTypeLabel(tag.Type)}:{tag.Value}").Contains(normalizedQuery, StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateSearchFields(AvailabilityRow row)
    {
        yield return row.CharacterName;
        yield return row.DisplayName;
        yield return row.Status;
        yield return row.HomeWorldName;
        yield return row.ClassName;
        yield return row.GenderText;
        yield return row.RaceName;
        yield return row.TribeName;
        if (row.Level > 0)
            yield return row.Level.ToString(CultureInfo.InvariantCulture);

        var profile = row.Profile;
        if (profile == null)
            yield break;

        yield return profile.CharacterName;
        yield return profile.Title;
        yield return profile.Pronouns;
        yield return profile.Tagline;
        yield return profile.RpStatus;
        yield return profile.Approachability;
        foreach (var hook in profile.Hooks)
        {
            yield return hook.Title;
            yield return hook.Description;
        }

        foreach (var tag in row.VisibleTags)
        {
            yield return tag.Value;
            yield return ProfileTagText.GetTypeLabel(tag.Type);
        }
    }
}
