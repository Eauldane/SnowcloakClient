using System.Numerics;

namespace Snowcloak.UI;

internal static class SnowcloakColours
{
    public static readonly Vector4 OnlineBlue = new(0.4275f, 0.6863f, 1f, 1f);
    public static readonly Vector4 BooleanTrue = OnlineBlue;
    public static readonly Vector4 BooleanFalse = new(0.6863f, 0.2863f, 0.3333f, 1f);
    public static readonly Vector4 CompactBg = new(0.025f, 0.070f, 0.105f, 0.96f);
    public static readonly Vector4 CompactPanel = new(0.035f, 0.095f, 0.140f, 0.94f);
    public static readonly Vector4 CompactPanelAlt = new(0.060f, 0.120f, 0.185f, 0.82f);
    public static readonly Vector4 CompactBorder = new(0.180f, 0.290f, 0.390f, 0.68f);
    public static readonly Vector4 CompactBorderSubtle = new(0.130f, 0.220f, 0.300f, 0.52f);
    public static readonly Vector4 CompactTextMuted = new(0.720f, 0.760f, 0.800f, 1f);
    public static readonly Vector4 CompactOffline = new(0.720f, 0.750f, 0.780f, 1f);
}
