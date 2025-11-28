using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Data.Extensions;

public static class GroupUserInfoExtensions
{
    public static bool IsModerator(this GroupUserInfo info)
    {
        return info.HasFlag(GroupUserInfo.IsModerator);
    }

    public static bool IsPinned(this GroupUserInfo info)
    {
        return info.HasFlag(GroupUserInfo.IsPinned);
    }

    public static void SetModerator(this ref GroupUserInfo info, bool isModerator)
    {
        if (isModerator) info |= GroupUserInfo.IsModerator;
        else info &= ~GroupUserInfo.IsModerator;
    }

    public static void SetPinned(this ref GroupUserInfo info, bool isPinned)
    {
        if (isPinned) info |= GroupUserInfo.IsPinned;
        else info &= ~GroupUserInfo.IsPinned;
    }
}