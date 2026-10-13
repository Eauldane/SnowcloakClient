using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.Core.Profiles;

public static class ProfileReaderLayoutPolicy
{
    public static string[] ImageHashes(CharacterProfileDocumentDto document) => ProfileBuilderPolicy.AssetHashes(document.Builder)
        .Concat(new[] { document.HeaderImageHash, document.ProfilePictureHash }.OfType<string>().Where(hash => hash.Length > 0))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static float SurfaceHeight(float available, float scale, bool hasBoundaries)
    {
        var footer = hasBoundaries ? 110 * scale : 0;
        return Math.Max(1, available - footer);
    }
}
