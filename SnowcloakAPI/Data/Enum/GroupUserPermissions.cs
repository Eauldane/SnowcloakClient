namespace Snowcloak.API.Data.Enum;

[Flags]
public enum GroupUserPermissions
{
    NoneSet = 0x0,
    Paused = 0x1,
    DisableAnimations = 0x2,
    DisableSounds = 0x4,
    DisableVFX = 0x8,
}