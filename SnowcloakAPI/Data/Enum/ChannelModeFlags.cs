namespace Snowcloak.API.Data.Enum;

[Flags]
public enum ChannelModeFlags
{
    None = 0x0,
    VoiceOnly = 0x1,
    Muted = 0x2,
    SlowMode = 0x4
}
