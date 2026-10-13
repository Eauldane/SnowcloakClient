using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Snowcloak.API.Dto.User;

namespace Snowcloak.UI.Components;

internal sealed class ProfileWindowColours : IDisposable
{
    private int _count;
    [ThreadStatic] private static ProfileThemeDto? _activeTheme;
    private readonly ProfileThemeDto? _previousTheme;
    internal static ProfileThemeDto? ActiveTheme => _activeTheme;
    internal static Vector4 Resolve(string? colour, Vector4 fallback)
    {
        if (colour is not { Length: 9 } || colour[0] != '#' || !uint.TryParse(colour.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba)) return fallback;
        return new Vector4(((rgba >> 24) & 255) / 255f, ((rgba >> 16) & 255) / 255f, ((rgba >> 8) & 255) / 255f, (rgba & 255) / 255f);
    }
    public ProfileWindowColours(ProfileThemeDto theme)
    {
        _previousTheme = _activeTheme; _activeTheme = theme;
        Push(ImGuiCol.WindowBg, theme.WindowBackground);
        Push(ImGuiCol.ChildBg, theme.WindowBackground);
        Push(ImGuiCol.Text, theme.WindowText);
        Push(ImGuiCol.Border, theme.WindowBorder);
        Push(ImGuiCol.TitleBg, theme.WindowTitle);
        Push(ImGuiCol.TitleBgActive, theme.WindowTitle);
        Push(ImGuiCol.TitleBgCollapsed, theme.WindowTitle);
        Push(ImGuiCol.Button, theme.WindowButton);
        Push(ImGuiCol.ButtonHovered, theme.WindowButton, 1.2f);
        Push(ImGuiCol.ButtonActive, theme.WindowButton, 1.4f);
        Push(ImGuiCol.TextDisabled, theme.WindowMutedText);
        Push(ImGuiCol.Header, theme.WindowSection);
        Push(ImGuiCol.HeaderHovered, theme.WindowSection, 1.2f);
        Push(ImGuiCol.HeaderActive, theme.WindowSection, 1.4f);
        Push(ImGuiCol.FrameBg, theme.WindowSection);
        Push(ImGuiCol.TableHeaderBg, theme.WindowSection);
        Push(ImGuiCol.Separator, theme.WindowAccent);
        Push(ImGuiCol.CheckMark, theme.WindowAccent);
        Push(ImGuiCol.ScrollbarBg, theme.WindowCard);
        Push(ImGuiCol.ScrollbarGrab, theme.WindowAccent);
        Push(ImGuiCol.ScrollbarGrabHovered, theme.WindowAccent, 1.2f);
        Push(ImGuiCol.ScrollbarGrabActive, theme.WindowAccent, 1.4f);
    }
    private void Push(ImGuiCol slot, string colour, float brightness = 1)
    {
        if (colour.Length != 9 || colour[0] != '#' || !uint.TryParse(colour.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba)) return;
        var value = new Vector4(Math.Min(1, ((rgba >> 24) & 255) / 255f * brightness),
            Math.Min(1, ((rgba >> 16) & 255) / 255f * brightness), Math.Min(1, ((rgba >> 8) & 255) / 255f * brightness), (rgba & 255) / 255f);
        ImGui.PushStyleColor(slot, value); _count++;
    }
    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        if (_count > 0) ImGui.PopStyleColor(_count);
        _count = 0; _activeTheme = _previousTheme; _disposed = true;
    }
}
