namespace Snowcloak.API.Data.Enum;

[Flags]
public enum GroupPermissions
{
    NoneSet = 0x0,
    DisableAnimations = 0x1,
    DisableSounds = 0x2,
    DisableInvites = 0x4,
    DisableVFX = 0x8,
}