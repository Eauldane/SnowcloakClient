using System.Text;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.Core.Pairing;

public static class ProfileTagText
{
    public static string NormalizeForLookup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        StringBuilder normalized = new(value.Length);
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
                continue;

            normalized.Append(char.ToUpperInvariant(character));
        }

        return normalized.ToString();
    }

    public static string GetTypeLabel(ProfileTagType type)
    {
        return type switch
        {
            ProfileTagType.ChatStyle => "Chat Style",
            ProfileTagType.WritingStyle => "Writing Style",
            ProfileTagType.LikedCharacter => "Liked Characters",
            ProfileTagType.OwnCharacter => "Own Character",
            ProfileTagType.Timezone => "Timezone",
            ProfileTagType.Kink => "Kink",
            _ => "Other",
        };
    }
}
