using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using System.Text;

namespace Snowcloak.Services;

public static class ProfileTagUtilities
{
    public const int MaxTagCount = 64;
    public const int MaxTagLength = 64;

    private static readonly IReadOnlyDictionary<ProfileTagType, IReadOnlyList<string>> DefaultTagSuggestions
        = new Dictionary<ProfileTagType, IReadOnlyList<string>>
        {
            [ProfileTagType.ChatStyle] =
            [
                "Just Chatting",
                "RP",
                "ERP",
                "Casual RP",
                "Story-Driven RP",
                "Slice of Life",
                "Action RP",
                "Slow Burn",
                "OOC Friendly",
                "IC Approaches Preferred",
                "OOC Approaches Preferred"
            ],
            [ProfileTagType.WritingStyle] =
            [
                "One-liners",
                "Semi-paragraph",
                "Paragraph",
                "Long paragraph",
                "Multi-paragraph",
                "Third person",
                "First person",
                "Emote-heavy"
            ],
            [ProfileTagType.LikedCharacter] =
            [
                "Midlander",
                "Highlander",
                "Wildwood",
                "Duskwight",
                "Plainsfolk",
                "Dunesfolk",
                "Seeker of the Sun",
                "Keeper of the Moon",
                "Sea Wolf",
                "Hellsguard",
                "Raen",
                "Xaela",
                "Rava",
                "Veena",
                "Helions",
                "The Lost",
                "Elezen",
                "Viera",
                "Miqo'te",
                "Au Ra",
                "Hrothgar",
                "Lalafell",
                "Roegadyn",
                "Hyur",
                "Male",
                "Female"
            ],
            [ProfileTagType.OwnCharacter] =
            [
                "Midlander",
                "Highlander",
                "Wildwood",
                "Duskwight",
                "Plainsfolk",
                "Dunesfolk",
                "Seeker of the Sun",
                "Keeper of the Moon",
                "Sea Wolf",
                "Hellsguard",
                "Raen",
                "Xaela",
                "Rava",
                "Veena",
                "Helions",
                "The Lost",
                "Elezen",
                "Viera",
                "Miqo'te",
                "Au Ra",
                "Hrothgar",
                "Lalafell",
                "Roegadyn",
                "Hyur",
                "Male",
                "Female"
            ],
            [ProfileTagType.Timezone] =
            [
                "Weekday Evenings",
                "Weekends",
                "Late Night"
            ],
            [ProfileTagType.Kink] =
            [
                "Aftercare",
                "Anal",
                "Aphrodisiacs",
                "BDSM",
                "Bad Ends",
                "Choking",
                "Clothed Sex",
                "Consent Play",
                "Costumes",
                "Creampies",
                "Cuddling",
                "Exhibitionism",
                "Exotic Cocks",
                "Foreplay",
                "Handholding",
                "Impregnation",
                "Mommy/Daddy Kink",
                "Monsters",
                "Oral Sex",
                "Size Differences",
                "Uniforms",
                "Voyeurism",
                "Vanilla",
                "Voyeurism"
            ],
            [ProfileTagType.Other] =
            [
                "SFW",
                "NSFW",
                "Text Only",
                "Long-Term",
                "One-Shots",
                "Lore-Friendly"
            ],
        };

    public static List<UserProfileTagDto> NormalizeForStorage(IEnumerable<UserProfileTagDto>? tags)
    {
        if (tags == null)
        {
            return [];
        }

        List<UserProfileTagDto> normalizedTags = [];
        HashSet<string> seenTagKeys = new(StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            if (!Enum.IsDefined(tag.Type))
            {
                continue;
            }

            var trimmedValue = tag.Value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedValue))
            {
                continue;
            }

            if (trimmedValue.Length > MaxTagLength)
            {
                trimmedValue = trimmedValue[..MaxTagLength];
            }

            var key = $"{(int)tag.Type}:{NormalizeForLookup(trimmedValue)}";
            if (!seenTagKeys.Add(key))
            {
                continue;
            }

            normalizedTags.Add(new UserProfileTagDto(tag.Type, trimmedValue));
            if (normalizedTags.Count >= MaxTagCount)
            {
                break;
            }
        }

        normalizedTags.Sort((left, right) =>
        {
            var typeCompare = GetDisplaySortOrder(left.Type).CompareTo(GetDisplaySortOrder(right.Type));
            if (typeCompare != 0)
            {
                return typeCompare;
            }

            var valueCompare = string.Compare(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);
            if (valueCompare != 0)
            {
                return valueCompare;
            }

            return string.Compare(left.Value, right.Value, StringComparison.Ordinal);
        });

        return normalizedTags;
    }

    public static IReadOnlyList<string> GetDefaultSuggestions(ProfileTagType type)
    {
        if (DefaultTagSuggestions.TryGetValue(type, out var suggestions))
        {
            return suggestions;
        }

        return [];
    }

    public static List<string> GetDefaultSuggestions(ProfileTagType type, string? searchText, IEnumerable<UserProfileTagDto>? existingTags, int? maxResults = null)
    {
        var defaults = GetDefaultSuggestions(type);
        if (defaults.Count == 0)
        {
            return [];
        }

        var query = NormalizeForLookup(searchText);
        var existingLookup = BuildTypeLookup(existingTags, type);
        var hasResultLimit = maxResults.HasValue && maxResults.Value > 0;

        List<string> filteredSuggestions = [];
        foreach (var suggestion in defaults)
        {
            var normalizedSuggestion = NormalizeForLookup(suggestion);
            if (existingLookup.Contains(normalizedSuggestion))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(query) && !normalizedSuggestion.Contains(query, StringComparison.Ordinal))
            {
                continue;
            }

            filteredSuggestions.Add(suggestion);
            if (hasResultLimit && filteredSuggestions.Count >= maxResults!.Value)
            {
                break;
            }
        }

        return filteredSuggestions;
    }

    public static List<UserProfileTagDto> GetVisibleTagsForViewer(IEnumerable<UserProfileTagDto>? profileTags, IEnumerable<UserProfileTagDto>? viewerTags)
    {
        var normalizedProfileTags = NormalizeForStorage(profileTags);
        if (normalizedProfileTags.Count == 0)
        {
            return normalizedProfileTags;
        }

        var viewerKinks = BuildKinkLookup(viewerTags);
        return normalizedProfileTags
            .Where(tag => tag.Type != ProfileTagType.Kink
                || viewerKinks.Contains(NormalizeForLookup(tag.Value)))
            .ToList();
    }

    public static HashSet<string> BuildKinkLookup(IEnumerable<UserProfileTagDto>? tags)
    {
        return BuildTypeLookup(tags, ProfileTagType.Kink);
    }

    public static string NormalizeForLookup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder normalized = new(value.Length);
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            normalized.Append(char.ToUpperInvariant(character));
        }

        return normalized.ToString();
    }

    private static HashSet<string> BuildTypeLookup(IEnumerable<UserProfileTagDto>? tags, ProfileTagType type)
    {
        HashSet<string> lookup = new(StringComparer.Ordinal);
        if (tags == null)
        {
            return lookup;
        }

        foreach (var tag in NormalizeForStorage(tags))
        {
            if (tag.Type != type)
            {
                continue;
            }

            lookup.Add(NormalizeForLookup(tag.Value));
        }

        return lookup;
    }

    private static int GetDisplaySortOrder(ProfileTagType type)
    {
        return type switch
        {
            ProfileTagType.ChatStyle => 0,
            ProfileTagType.WritingStyle => 1,
            ProfileTagType.LikedCharacter => 2,
            ProfileTagType.OwnCharacter => 3,
            ProfileTagType.Timezone => 4,
            ProfileTagType.Kink => 5,
            ProfileTagType.Other => 6,
            _ => int.MaxValue,
        };
    }
}
