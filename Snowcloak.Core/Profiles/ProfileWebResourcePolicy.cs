namespace Snowcloak.Core.Profiles;

public static class ProfileWebResourcePolicy
{
    public const string Origin = "https://profile.snowcloak.invalid";
    public const string Page = Origin + "/index.html";
    public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'none'; frame-src 'none'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";

    public static string? Resource(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != Origin ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var path = uri.AbsolutePath;
        if (path is "/index.html" or "/builder.css" or "/builder.js") return path[1..];
        return ImageHash(address) == null ? null : path[1..];
    }

    public static string? ImageHash(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != Origin || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var path = uri.AbsolutePath;
        return path.Length == 76 && path.StartsWith("/assets/", StringComparison.Ordinal) && path.EndsWith(".png", StringComparison.Ordinal)
            && path.AsSpan(8, 64).ToArray().All(char.IsAsciiHexDigit) ? path.Substring(8, 64) : null;
    }
}
