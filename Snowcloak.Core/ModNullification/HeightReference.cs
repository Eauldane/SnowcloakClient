namespace Snowcloak.Core.ModNullification;

public static class HeightReference
{
    private static readonly (float Male, float Female)[] OneInchRspScales =
    [
        (0f, 0f),
        (0.014523449f, 0.015483871f),
        (0.014525447f, 0.015483871f),
        (0.012578534f, 0.013310249f),
        (0.012578534f, 0.013310249f),
        (0.027631579f, 0.027631579f),
        (0.027631579f, 0.027631579f),
        (0.014513557f, 0.016298812f),
        (0.014513557f, 0.016298812f),
        (0.011438764f, 0.013227513f),
        (0.011438764f, 0.013227513f),
        (0.014518147f, 0.016173912f),
        (0.014518147f, 0.016173912f),
        (0.011554404f, 0.013581268f),
        (0.011554404f, 0.013581268f),
        (0.014513275f, 0.015781250f),
        (0.014513275f, 0.015781250f),
    ];

    public static bool TryConvertToCentimeters(byte clan, bool female, float rspScale, out float centimeters)
    {
        centimeters = 0f;
        if (clan is < 1 or > 16 || !float.IsFinite(rspScale) || rspScale <= 0f)
        {
            return false;
        }

        var scales = OneInchRspScales[clan];
        var oneInchRspScale = female ? scales.Female : scales.Male;
        if (oneInchRspScale <= 0f)
        {
            return false;
        }

        centimeters = rspScale / oneInchRspScale * 2.54f;
        return float.IsFinite(centimeters);
    }
}
