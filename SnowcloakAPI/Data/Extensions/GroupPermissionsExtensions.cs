using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Data.Extensions;

public static class GroupPermissionsExtensions
{
    public static bool IsDisableAnimations(this GroupPermissions perm)
    {
        return perm.HasFlag(GroupPermissions.DisableAnimations);
    }

    public static bool IsDisableSounds(this GroupPermissions perm)
    {
        return perm.HasFlag(GroupPermissions.DisableSounds);
    }

    public static bool IsDisableInvites(this GroupPermissions perm)
    {
        return perm.HasFlag(GroupPermissions.DisableInvites);
    }

    public static bool IsDisableVFX(this GroupPermissions perm)
    {
        return perm.HasFlag(GroupPermissions.DisableVFX);
    }

    public static void SetDisableAnimations(this ref GroupPermissions perm, bool set)
    {
        if (set) perm |= GroupPermissions.DisableAnimations;
        else perm &= ~GroupPermissions.DisableAnimations;
    }

    public static void SetDisableSounds(this ref GroupPermissions perm, bool set)
    {
        if (set) perm |= GroupPermissions.DisableSounds;
        else perm &= ~GroupPermissions.DisableSounds;
    }

    public static void SetDisableInvites(this ref GroupPermissions perm, bool set)
    {
        if (set) perm |= GroupPermissions.DisableInvites;
        else perm &= ~GroupPermissions.DisableInvites;
    }

    public static void SetDisableVFX(this ref GroupPermissions perm, bool set)
    {
        if (set) perm |= GroupPermissions.DisableVFX;
        else perm &= ~GroupPermissions.DisableVFX;
    }
}