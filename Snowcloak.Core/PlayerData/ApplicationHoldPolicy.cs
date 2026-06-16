namespace Snowcloak.Core.PlayerData;

public static class ApplicationHoldPolicy
{
    public static bool ShouldHoldForCombatOrPerformance(bool holdApplicationDuringCombat, bool isInCombatOrPerforming)
        => holdApplicationDuringCombat && isInCombatOrPerforming;
}
