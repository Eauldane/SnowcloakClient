namespace Snowcloak.Core.Authentication;

public static class AuthenticationFailureClassifier
{
    public static AuthenticationFailureKind Classify(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return AuthenticationFailureKind.Unknown;
        }

        if (Contains(reason, "already logged in") && Contains(reason, "reconnect in"))
        {
            return AuthenticationFailureKind.TransientDuplicateSession;
        }

        if (Contains(reason, "secret key is invalid"))
        {
            return AuthenticationFailureKind.InvalidSecretKey;
        }

        if (Contains(reason, "temporarily banned"))
        {
            return AuthenticationFailureKind.TemporaryBan;
        }

        if (Contains(reason, "permanently banned"))
        {
            return AuthenticationFailureKind.PermanentBan;
        }

        if (Contains(reason, "character is banned"))
        {
            return AuthenticationFailureKind.CharacterBan;
        }

        if (Contains(reason, "client version is outdated"))
        {
            return AuthenticationFailureKind.ClientOutdated;
        }

        return AuthenticationFailureKind.Unknown;
    }

    public static bool IsTransient(AuthenticationFailureKind kind)
    {
        return kind is AuthenticationFailureKind.TransientDuplicateSession;
    }

    private static bool Contains(string value, string expected)
    {
        return value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }
}
