namespace Snowcloak.API.Data.Enum;

[Flags]
public enum ChannelUserRole
{
    None = 0x0,
    Voice = 0x1,
    HalfOperator = 0x2,
    Operator = 0x4,
    Admin = 0x8,
    Owner = 0x10
}
