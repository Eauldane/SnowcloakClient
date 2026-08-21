using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Roleplay;

namespace Snowcloak.Core.Roleplay;

public enum BoundaryCompatibilityKind
{
    Aligned,
    AskFirst,
    Conflict,
}

public sealed record BoundaryCompatibilityItem(
    string Key,
    RpBoundaryRating Mine,
    RpBoundaryRating Theirs,
    BoundaryCompatibilityKind Kind);

public sealed record BoundaryCompatibilityResult(
    IReadOnlyList<BoundaryCompatibilityItem> Items,
    IReadOnlyList<string> MineOnlyKeys,
    IReadOnlyList<string> TheirsOnlyKeys,
    bool ContentRatingMismatch)
{
    public IEnumerable<BoundaryCompatibilityItem> Conflicts() =>
        Items.Where(item => item.Kind == BoundaryCompatibilityKind.Conflict);

    public IEnumerable<BoundaryCompatibilityItem> AskFirst() =>
        Items.Where(item => item.Kind == BoundaryCompatibilityKind.AskFirst);

    public IEnumerable<BoundaryCompatibilityItem> Aligned() =>
        Items.Where(item => item.Kind == BoundaryCompatibilityKind.Aligned);
}

public static class BoundaryCompatibility
{
    public static BoundaryCompatibilityResult Evaluate(
        RpBoundariesDto? mine,
        RpBoundariesDto? theirs,
        ProfileContentRating myContentRating,
        ProfileContentRating theirContentRating)
    {
        var myEntries = ToMap(mine);
        var theirEntries = ToMap(theirs);
        var shared = myEntries.Keys.Intersect(theirEntries.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(key => BuildItem(key, myEntries[key], theirEntries[key]))
            .ToArray();
        var mineOnly = myEntries.Keys.Except(theirEntries.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var theirsOnly = theirEntries.Keys.Except(myEntries.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new BoundaryCompatibilityResult(
            shared,
            mineOnly,
            theirsOnly,
            theirContentRating > myContentRating);
    }

    private static Dictionary<string, RpBoundaryRating> ToMap(RpBoundariesDto? boundaries)
        => (boundaries?.Entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && Enum.IsDefined(entry.Rating))
            .GroupBy(entry => entry.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Rating, StringComparer.OrdinalIgnoreCase);

    private static BoundaryCompatibilityItem BuildItem(string key, RpBoundaryRating mine, RpBoundaryRating theirs)
    {
        var kind = mine == RpBoundaryRating.HardNo || theirs == RpBoundaryRating.HardNo
            ? mine == theirs ? BoundaryCompatibilityKind.Aligned : BoundaryCompatibilityKind.Conflict
            : mine == RpBoundaryRating.AskFirst || theirs == RpBoundaryRating.AskFirst
                ? BoundaryCompatibilityKind.AskFirst
                : BoundaryCompatibilityKind.Aligned;
        return new BoundaryCompatibilityItem(key, mine, theirs, kind);
    }
}
