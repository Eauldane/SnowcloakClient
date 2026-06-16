using Snowcloak.API.Data.Extensions;

namespace Snowcloak.Core.PlayerData;

public static class EffectivePermissionsResolver
{
    public static EffectivePermissions Resolve(DirectPermissions? direct, IReadOnlyCollection<GroupPermissionView> groups)
    {
        bool hasDirect = direct.HasValue;
        var d = direct.GetValueOrDefault();

        bool paused = IsPausedCore(hasDirect, d, groups);

        // Only memberships that are not paused at any level participate in the group disable vote.
        var activeGroups = groups
            .Where(g => !g.OwnGroupUserPermissions.IsPaused()
                        && !g.OtherGroupUserPermissions.IsPaused()
                        && !g.GroupUserPermissions.IsPaused())
            .ToList();

        bool disableIndividualAnimations = hasDirect && (d.Other.IsDisableAnimations() || d.Own.IsDisableAnimations());
        bool disableIndividualSounds = hasDirect && (d.Other.IsDisableSounds() || d.Own.IsDisableSounds());
        bool disableIndividualVFX = hasDirect && (d.Other.IsDisableVFX() || d.Own.IsDisableVFX());

        bool disableGroupAnimations = activeGroups.All(g =>
            g.OwnGroupUserPermissions.IsDisableAnimations() || g.OtherGroupUserPermissions.IsDisableAnimations()
            || g.GroupPermissions.IsDisableAnimations() || g.GroupUserPermissions.IsDisableAnimations());
        bool disableGroupSounds = activeGroups.All(g =>
            g.OwnGroupUserPermissions.IsDisableSounds() || g.OtherGroupUserPermissions.IsDisableSounds()
            || g.GroupPermissions.IsDisableSounds() || g.GroupUserPermissions.IsDisableSounds());
        bool disableGroupVFX = activeGroups.All(g =>
            g.OwnGroupUserPermissions.IsDisableVFX() || g.OtherGroupUserPermissions.IsDisableVFX()
            || g.GroupPermissions.IsDisableVFX() || g.GroupUserPermissions.IsDisableVFX());

        bool disableAnimations = (hasDirect && disableIndividualAnimations) || (!hasDirect && disableGroupAnimations);
        bool disableSounds = (hasDirect && disableIndividualSounds) || (!hasDirect && disableGroupSounds);
        bool disableVFX = (hasDirect && disableIndividualVFX) || (!hasDirect && disableGroupVFX);

        return new EffectivePermissions(paused, disableSounds, disableAnimations, disableVFX);
    }
    
    public static bool IsPaused(DirectPermissions? direct, IReadOnlyCollection<GroupPermissionView> groups)
        => IsPausedCore(direct.HasValue, direct.GetValueOrDefault(), groups);

    private static bool IsPausedCore(bool hasDirect, DirectPermissions d, IReadOnlyCollection<GroupPermissionView> groups)
    {
        return hasDirect && d.Other.IsPaired()
            ? d.Other.IsPaused() || d.Own.IsPaused()
            : groups.All(g => g.GroupUserPermissions.IsPaused()
                              || g.OwnGroupUserPermissions.IsPaused()
                              || g.OtherGroupUserPermissions.IsPaused());
    }
}
