using System.Numerics;

namespace Snowcloak.Core.Profiles;

public readonly record struct ProfileWebWindow(uint Id, uint RootId, uint ViewportId, bool Renderable,
    bool TopLayer, Vector2 Min, Vector2 Max);

public static class ProfileWebOcclusion
{
    public static ProfileWebBounds[] Cuts(IReadOnlyList<ProfileWebWindow> windows, uint ownerRoot, uint viewport,
        Vector2 origin, Vector2 size, ProfileWebBounds surface, Vector2? clipMin = null, Vector2? clipMax = null)
    {
        var ownerIndex = -1;
        for (var i = 0; i < windows.Count; i++) if (windows[i].Id == ownerRoot) { ownerIndex = i; break; }
        if (ownerIndex < 0 || size.X <= 0 || size.Y <= 0) return [new(0,0,surface.Width,surface.Height)];
        var cuts = new HashSet<ProfileWebBounds>();
        if (clipMin is { } minimum && clipMax is { } maximum)
        {
            var scale = new Vector2(surface.Width, surface.Height) / size;
            var left = Math.Clamp((int)MathF.Ceiling((minimum.X-origin.X)*scale.X), 0, surface.Width);
            var top = Math.Clamp((int)MathF.Ceiling((minimum.Y-origin.Y)*scale.Y), 0, surface.Height);
            var right = Math.Clamp((int)MathF.Floor((maximum.X-origin.X)*scale.X), 0, surface.Width);
            var bottom = Math.Clamp((int)MathF.Floor((maximum.Y-origin.Y)*scale.Y), 0, surface.Height);
            if (right <= left || bottom <= top) return [new(0,0,surface.Width,surface.Height)];
            if (top > 0) cuts.Add(new(0,0,surface.Width,top));
            if (bottom < surface.Height) cuts.Add(new(0,bottom,surface.Width,surface.Height-bottom));
            if (left > 0) cuts.Add(new(0,top,left,bottom-top));
            if (right < surface.Width) cuts.Add(new(right,top,surface.Width-right,bottom-top));
        }
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (!window.Renderable || window.ViewportId != viewport ||
                window.RootId == ownerRoot && !window.TopLayer || i <= ownerIndex && !window.TopLayer) continue;
            var min = Vector2.Max(window.Min, origin); var max = Vector2.Min(window.Max, origin + size);
            if (max.X <= min.X || max.Y <= min.Y) continue;
            var scale = new Vector2(surface.Width, surface.Height) / size;
            var left = Math.Clamp((int)MathF.Floor((min.X-origin.X)*scale.X), 0, surface.Width);
            var top = Math.Clamp((int)MathF.Floor((min.Y-origin.Y)*scale.Y), 0, surface.Height);
            var right = Math.Clamp((int)MathF.Ceiling((max.X-origin.X)*scale.X), 0, surface.Width);
            var bottom = Math.Clamp((int)MathF.Ceiling((max.Y-origin.Y)*scale.Y), 0, surface.Height);
            if (right > left && bottom > top) cuts.Add(new(left,top,right-left,bottom-top));
        }
        if (cuts.Count > 256) return [new(0,0,surface.Width,surface.Height)];
        return cuts.OrderBy(cut => cut.X).ThenBy(cut => cut.Y).ThenBy(cut => cut.Width).ThenBy(cut => cut.Height).ToArray();
    }
}
