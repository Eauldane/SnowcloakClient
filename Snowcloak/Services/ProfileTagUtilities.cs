using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Core.Profiles;

namespace Snowcloak.Services;

public static class ProfileTagUtilities
{
    public const int MaxTagCount = ProfileTagPolicy.MaxTagCount;
    public const int MaxTagLength = ProfileTagPolicy.MaxTagLength;

    public static List<UserProfileTagDto> NormalizeForStorage(IEnumerable<UserProfileTagDto>? tags)
        => ProfileTagPolicy.NormalizeForStorage(tags);

    public static string GetTypeLabel(ProfileTagType type) => ProfileTagPolicy.GetTypeLabel(type);

    public static IReadOnlyList<string> GetDefaultSuggestions(ProfileTagType type)
        => ProfileTagPolicy.GetDefaultSuggestions(type);

    public static List<string> GetDefaultSuggestions(
        ProfileTagType type,
        string? searchText,
        IEnumerable<UserProfileTagDto>? existingTags,
        int? maxResults = null)
        => ProfileTagPolicy.GetDefaultSuggestions(type, searchText, existingTags, maxResults);

    public static List<UserProfileTagDto> GetVisibleTagsForViewer(
        IEnumerable<UserProfileTagDto>? profileTags,
        IEnumerable<UserProfileTagDto>? viewerTags)
        => ProfileTagPolicy.GetVisibleTagsForViewer(profileTags, viewerTags);

    public static HashSet<string> BuildKinkLookup(IEnumerable<UserProfileTagDto>? tags)
        => ProfileTagPolicy.BuildKinkLookup(tags);

    public static string NormalizeForLookup(string? value) => ProfileTagPolicy.NormalizeForLookup(value);
}
