namespace Snowcloak.API.Data.Enum;

[Flags]
public enum GroupUserInfo
{
    None = 0x0,
    IsModerator = 0x2,
    IsPinned = 0x4
}