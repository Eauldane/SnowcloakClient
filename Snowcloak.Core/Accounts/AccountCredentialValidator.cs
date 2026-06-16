namespace Snowcloak.Core.Accounts;

/// <summary>
/// Pure validation for the Snowcloak account username/password credential forms shared by
/// the onboarding (IntroUI) and Settings account flows. Returns a user-facing message describing
/// the first problem found, or <c>null</c> when the credentials are acceptable.
/// </summary>
public static class AccountCredentialValidator
{
    public static string? Validate(string? username, string? password, string? passwordConfirm, bool requireConfirmation)
    {
        username ??= string.Empty;
        password ??= string.Empty;
        passwordConfirm ??= string.Empty;

        if (username.Trim().Length == 0)
            return "Enter a username.";

        if (username.Trim().Length is < 3 or > 64)
            return "Username must contain between 3 and 64 characters.";

        if (username.Any(char.IsWhiteSpace))
            return "Username cannot contain spaces.";

        if (string.IsNullOrEmpty(password))
            return "Enter a password.";

        if (!requireConfirmation)
            return null;

        if (password.Length < 12)
            return "New account passwords must be at least 12 characters long.";

        if (string.IsNullOrEmpty(passwordConfirm))
            return "Re-enter your password to confirm it.";

        return string.Equals(password, passwordConfirm, StringComparison.Ordinal)
            ? null
            : "The password confirmation does not match.";
    }
}
