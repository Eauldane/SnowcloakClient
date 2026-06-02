namespace Snowcloak.Services.ModNullification;

[Flags]
public enum ModNullificationKind
{
    None = 0,
    Height = 1 << 0,
    Vfx = 1 << 1,
    Sfx = 1 << 2,
}
