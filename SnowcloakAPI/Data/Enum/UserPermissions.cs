namespace Snowcloak.API.Data.Enum;

[Flags]
public enum UserPermissions
{
    NoneSet = 0,
    Paired = 1,
    Paused = 2,
    DisableAnimations = 4,
    DisableSounds = 8,
    DisableVFX = 16,
}