using System.Numerics;

namespace Snowcloak.UI;

internal static class SnowcloakModernPalette
{
    public static ModernPalette Value { get; } = new(
        Accent: SnowcloakColours.OnlineBlue,
        BooleanTrue: SnowcloakColours.BooleanTrue,
        BooleanFalse: SnowcloakColours.BooleanFalse,
        CompactBg: SnowcloakColours.CompactBg,
        CompactPanel: SnowcloakColours.CompactPanel,
        CompactPanelAlt: SnowcloakColours.CompactPanelAlt,
        CompactBorder: SnowcloakColours.CompactBorder,
        CompactBorderSubtle: SnowcloakColours.CompactBorderSubtle,
        CompactTextMuted: SnowcloakColours.CompactTextMuted,
        CompactOffline: SnowcloakColours.CompactOffline,
        TitleBg: new Vector4(0.025f, 0.060f, 0.095f, 1f),
        TitleBgActive: new Vector4(0.035f, 0.080f, 0.125f, 1f),
        TitleBgCollapsed: new Vector4(0.020f, 0.050f, 0.080f, 1f),
        Button: new Vector4(0.145f, 0.290f, 0.470f, 1f),
        ButtonHovered: new Vector4(0.190f, 0.350f, 0.540f, 1f),
        ButtonActive: new Vector4(0.230f, 0.410f, 0.620f, 1f),
        TabActive: ElezenColours.SnowcloakBlue,
        TabHovered: Colour.Lighten(ElezenColours.SnowcloakBlue, 0.18f));
}
