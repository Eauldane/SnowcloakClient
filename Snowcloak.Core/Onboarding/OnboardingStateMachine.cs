namespace Snowcloak.Core.Onboarding;

public enum OnboardingStep
{
    Welcome,
    Agreement,
    Storage,
    Service,
    Complete
}

public readonly record struct OnboardingInputs(
    bool AgreementAccepted,
    bool RequirementsAcknowledged,
    bool StorageReady,
    bool Connected);

public static class OnboardingStateMachine
{
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
