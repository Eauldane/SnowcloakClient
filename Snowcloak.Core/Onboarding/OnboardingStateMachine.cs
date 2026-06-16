namespace Snowcloak.Core.Onboarding;

/// <summary>
/// The first-run journey steps. The active step is derived from <see cref="OnboardingInputs"/>
/// rather than from a long chain of nested config/server predicates in render code.
/// </summary>
public enum OnboardingStep
{
    /// <summary>Welcome / plugin requirement checks. Shown until the user acknowledges and the agreement is accepted.</summary>
    Welcome,

    /// <summary>Service agreement that must be accepted before any setup proceeds.</summary>
    Agreement,

    /// <summary>Storage/cache directory selection and the initial file scan.</summary>
    Storage,

    /// <summary>Service selection, account/key setup and connection.</summary>
    Service,

    /// <summary>Setup is finished; the main UI takes over.</summary>
    Complete
}

/// <summary>
/// The minimal, impure-free set of facts the onboarding flow needs to decide which step to show.
/// Callers compute these from config/server/connection state (including filesystem checks) so the
/// resolution itself stays pure and testable.
/// </summary>
/// <param name="AgreementAccepted">The service agreement has been accepted.</param>
/// <param name="RequirementsAcknowledged">The user pressed "Next" past the welcome/requirements page.</param>
/// <param name="StorageReady">A valid cache folder exists and the initial scan has completed.</param>
/// <param name="Connected">The API controller has an established connection.</param>
public readonly record struct OnboardingInputs(
    bool AgreementAccepted,
    bool RequirementsAcknowledged,
    bool StorageReady,
    bool Connected);

public static class OnboardingStateMachine
{
    /// <summary>
    /// Resolves the active onboarding step. The order is resilient to returning users, imported
    /// backups and interrupted scans: each later step is only reachable once the earlier guards pass,
    /// and a regression in any guard (e.g. a deleted cache folder, a dropped connection) falls back to
    /// the step that owns it.
    /// </summary>
    public static OnboardingStep Resolve(OnboardingInputs inputs)
    {
        if (!inputs.AgreementAccepted)
            return inputs.RequirementsAcknowledged ? OnboardingStep.Agreement : OnboardingStep.Welcome;

        if (!inputs.StorageReady)
            return OnboardingStep.Storage;

        if (!inputs.Connected)
            return OnboardingStep.Service;

        return OnboardingStep.Complete;
    }
}
