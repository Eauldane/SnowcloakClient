using System.Numerics;

namespace Snowcloak.Core.Profiles;

public readonly record struct ProfileWebBounds(int X, int Y, int Width, int Height);

public static class ProfileWebViewport
{
    public static ProfileWebBounds Map(Vector2 origin, Vector2 size, Vector2 viewportOrigin,
        Vector2 viewportSize, Vector2 clientScreenOrigin, Vector2 clientSize)
    {
        if (viewportSize.X <= 0 || viewportSize.Y <= 0 || clientSize.X <= 0 || clientSize.Y <= 0 || size.X <= 0 || size.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportSize));
        var scale = clientSize / viewportSize;
        var position = clientScreenOrigin + (origin - viewportOrigin) * scale;
        var pixels = size * scale;
        return new(Pixel(position.X), Pixel(position.Y), Math.Max(1, Pixel(pixels.X)), Math.Max(1, Pixel(pixels.Y)));
    }

    private static int Pixel(float value)
    {
        if (!float.IsFinite(value) || value < int.MinValue || value >= int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return checked((int)MathF.Round(value, MidpointRounding.AwayFromZero));
    }
}
