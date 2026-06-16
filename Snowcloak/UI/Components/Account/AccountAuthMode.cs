namespace Snowcloak.UI.Components.Account;

/// <summary>
/// The two modes a password account form can be in. Shared by the onboarding and Settings
/// account flows so the same component drives both surfaces.
/// </summary>
public enum AccountAuthMode
{
    SignIn,
    CreateAccount
}
