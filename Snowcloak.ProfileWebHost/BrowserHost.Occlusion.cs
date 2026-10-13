using System.Runtime.InteropServices;
using System.Text.Json;

namespace Snowcloak.ProfileWebHost;

internal sealed partial class BrowserHost
{
    private string? _regionKey;
    private bool ApplyOcclusion(JsonElement message, int width, int height)
    {
        var hasRegions = message.TryGetProperty("regions", out var regions);
        if (hasRegions && (regions.ValueKind != JsonValueKind.Array || regions.GetArrayLength() > 256)) return false;
        var key = width + ":" + height + ":" + (hasRegions ? regions.GetRawText() : "[]");
        if (_regionKey == key) return true;
        if (!hasRegions || regions.GetArrayLength() == 0)
        {
            if (SetWindowRgn(Handle, 0, true) == 0) return false;
            _regionKey = key; return true;
        }
        var region = CreateRectRgn(0,0,width,height);
        if (region == 0) return false;
        try
        {
            foreach (var cut in regions.EnumerateArray())
            {
                var x = Math.Clamp(cut.GetProperty("x").GetInt32(), 0, width);
                var y = Math.Clamp(cut.GetProperty("y").GetInt32(), 0, height);
                var w = Math.Clamp(cut.GetProperty("width").GetInt32(), 0, width-x);
                var h = Math.Clamp(cut.GetProperty("height").GetInt32(), 0, height-y);
                if (w == 0 || h == 0) continue;
                var exclusion = CreateRectRgn(x,y,x+w,y+h);
                if (exclusion == 0) return false;
                try { if (CombineRgn(region, region, exclusion, 4) == 0) return false; }
                finally { DeleteObject(exclusion); }
            }
            if (SetWindowRgn(Handle, region, true) == 0) return false;
            region = 0; // Windows owns the region after a successful SetWindowRgn.
            _regionKey = key; return true;
        }
        finally { if (region != 0) DeleteObject(region); }
    }
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
}
