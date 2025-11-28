using System.Globalization;
using System.Numerics;

namespace Snowcloak.Utils
{
    public static class Colours
    {
        public static readonly Vector4 _snowcloakOnline = new(0.4275f, 0.6863f, 1f, 1f);

        public static Vector4 Hex2Vector4(string? hex)
        {
            if (hex == null || hex.Length != 6)
            {
                return new Vector4(255f, 255f, 255f, 1f);
            }
            else
            {
                byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);

                return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
            }
        }
    }
    
    
}
