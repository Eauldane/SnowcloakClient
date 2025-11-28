using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Data.Extensions;

public static class GroupUserPermissionsExtensions
{
    public static bool IsDisableAnimations(this GroupUserPermissions perm)
    {
        return perm.HasFlag(GroupUserPermissions.DisableAnimations);
    }

    public static bool IsDisableSounds(this GroupUserPermissions perm)
    {
        return perm.HasFlag(GroupUserPermissions.DisableSounds);
    }

    public static bool IsPaused(this GroupUserPermissions perm)
    {
        return perm.HasFlag(GroupUserPermissions.Paused);
    }

    public static bool IsDisableVFX(this GroupUserPermissions perm)
    {
        return perm.HasFlag(GroupUserPermissions.DisableVFX);
    }

    public static void SetDisableAnimations(this ref GroupUserPermissions perm, bool set)
    {
        if (set) perm |= GroupUserPermissions.DisableAnimations;
        else perm &= ~GroupUserPermissions.DisableAnimations;
    }

    public static void SetDisableSounds(this ref GroupUserPermissions perm, bool set)
    {
        if (set) perm |= GroupUserPermissions.DisableSounds;
        else perm &= ~GroupUserPermissions.DisableSounds;
    }

    public static void SetPaused(this ref GroupUserPermissions perm, bool set)
    {
        if (set) perm |= GroupUserPermissions.Paused;
        else perm &= ~GroupUserPermissions.Paused;
    }

    public static void SetDisableVFX(this ref GroupUserPermissions perm, bool set)
    {
        if (set) perm |= GroupUserPermissions.DisableVFX;
        else perm &= ~GroupUserPermissions.DisableVFX;
    }
}