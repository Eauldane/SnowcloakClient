using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;
public sealed class NativeProfilePageCanvas(UiFontService fonts)
{
    private string? _pressed;
    private string? _dragging;
    private string _character = string.Empty;
    private static readonly Regex FormattingTags = new(@"\[/?(?:b|i|u|s|color|size|font|center|left|right|url)(?:=[^\]]*)?\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public void Draw(string character, CharacterProfileDocumentDto document, string? selected,
        Func<string, IDalamudTextureWrap?> texture, Action<string> select, Action<string> editText,
        Action<string, string?, int> move, Action<string?, int> insert, bool preview)
    {
        if (_character != character) { _character = character; _pressed = null; _dragging = null; }
        var page = document.Builder!;
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        IDisposable? PushFont(ProfileBlockDto element) => element.Font switch
        { ProfileFont.Game => fonts.GameFont.Push(), ProfileFont.Sans => fonts.ProfileSansFont.Push(), ProfileFont.Serif => fonts.ProfileSerifFont.Push(), _ => null };
        Vector2 TextSize(ProfileBlockDto element, string text, float available)
        {
            using var font = PushFont(element);
            var factor = element.FontScale;
            return ImGui.CalcTextSize(text, false, Math.Max(1, available / factor)) * factor;
        }
        string Body(ProfileBlockDto element) => PlainText(element.Kind switch
        {
            ProfileBlockKind.RichText or ProfileBlockKind.Heading => element.Text,
            ProfileBlockKind.Overview => document.Overview,
            ProfileBlockKind.OocNotes => document.OocNotes,
            ProfileBlockKind.AdultPreferences => document.ContentRating == Snowcloak.API.Data.Enum.ProfileContentRating.Adult ? document.AdultPreferences : "",
            ProfileBlockKind.Fields => string.Join("\n\n", page.Fields.Where(f => element.References.Contains(f.Id) && !string.IsNullOrWhiteSpace(f.Value)).Select(f => f.Label + "\n" + f.Value)),
            ProfileBlockKind.Hooks => string.Join("\n\n", document.Hooks.Select(h => h.Title + "\n" + h.Description)),
            ProfileBlockKind.Preferences => string.Join("\n", page.Preferences.Select(p => PreferenceText(p)).Where(s => s.Length > 0)),
            ProfileBlockKind.Boundaries => document.Boundaries == null ? "" : string.Join("\n", document.Boundaries.Entries.Select(e => e.Key + " - " + e.Rating)) + "\n" + document.Boundaries.Note,
            _ => ""
        });
        float Measure(ProfileBlockDto element, float available)
        {
            var titleHeight = string.IsNullOrWhiteSpace(element.Title) ? 0 : TextSize(element, element.Title, available).Y + 12 * scale;
            if (ProfileBuilderPolicy.IsContainer(element.Kind)) return titleHeight;
            if (element.Kind == ProfileBlockKind.Divider) return 16 * scale;
            if (element.Kind == ProfileBlockKind.Link) return Math.Max(44 * scale, titleHeight + 20 * scale);
            if (element.Kind is ProfileBlockKind.Image or ProfileBlockKind.Gallery)
            {
                foreach (var asset in page.Assets.Where(a => element.References.Contains(a.Hash)))
                {
                    var image = texture(asset.Hash);
                    titleHeight += image == null ? 150 * scale : available * image.Height / Math.Max(1, image.Width);
                    if (!string.IsNullOrWhiteSpace(asset.Caption)) titleHeight += TextSize(element, asset.Caption, available).Y + 12 * scale;
                }
                return Math.Max(150 * scale, titleHeight);
            }
            return titleHeight + Math.Max(ImGui.GetTextLineHeight(), TextSize(element, Body(element), available).Y);
        }
        var layout = ProfilePageLayout.Arrange(page, width, scale, Measure);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("native-profile-page", new Vector2(width, layout.Height));
        var surfaceHovered = ImGui.IsItemHovered();
        var surfaceActive = ImGui.IsItemActive();
        var mouse = ImGui.GetIO().MousePos - origin;
        var hovered = layout.Elements.LastOrDefault(p => p.Bounds.Contains(mouse));
        var draw = ImGui.GetWindowDrawList();
        var pageMax = origin + new Vector2(width, layout.Height);
        draw.AddRectFilled(origin, pageMax, Colour(page.Theme.Background), 12 * scale);
        if (page.Theme.BackgroundHash != null && texture(page.Theme.BackgroundHash) is { } background)
            draw.AddImage(background.Handle, origin, pageMax);
        foreach (var placement in layout.Elements)
        {
            var element = placement.Element;
            var min = origin + new Vector2(placement.Bounds.X, placement.Bounds.Y);
            var max = min + new Vector2(placement.Bounds.Width, placement.Bounds.Height);
            draw.PushClipRect(min, max, true);
            draw.AddRectFilled(min, max, Colour(element.Background), element.Rounding * scale);
            if (element.Border > 0) draw.AddRect(min, max, Colour(page.Theme.Accent), element.Rounding * scale, ImDrawFlags.None, element.Border * scale);
            var content = origin + new Vector2(placement.ContentBounds.X, placement.ContentBounds.Y);
            using var font = PushFont(element);
            var size = ImGui.GetFontSize() * element.FontScale;
            var foreground = Colour(element.UseThemeForeground ? page.Theme.Foreground : element.Foreground);
            void Write(string text, uint colour)
            {
                draw.AddText(ImGui.GetFont(), size, content, colour, text, placement.ContentBounds.Width);
                content.Y += TextSize(element, text, placement.ContentBounds.Width).Y + 12 * scale;
            }
            if (element.Kind != ProfileBlockKind.Link && !string.IsNullOrWhiteSpace(element.Title)) Write(element.Title, Colour(page.Theme.Accent));
            if (element.Kind == ProfileBlockKind.Divider)
                draw.AddLine(content + new Vector2(0, 8 * scale), content + new Vector2(placement.ContentBounds.Width, 8 * scale), Colour(page.Theme.Accent));
            else if (element.Kind == ProfileBlockKind.Link)
            {
                draw.AddRectFilled(min, max, Colour(page.Theme.Accent), element.Rounding * scale);
                Write(string.IsNullOrWhiteSpace(element.Title) ? "Button" : element.Title, foreground);
            }
            else if (element.Kind is ProfileBlockKind.Image or ProfileBlockKind.Gallery)
            {
                var assets = page.Assets.Where(a => element.References.Contains(a.Hash)).ToList();
                if (assets.Count == 0 && !preview) Write("Select an image", foreground);
                foreach (var asset in assets)
                {
                    var image = texture(asset.Hash);
                    var imageHeight = image == null ? 150 * scale : placement.ContentBounds.Width * image.Height / Math.Max(1, image.Width);
                    if (image != null) draw.AddImage(image.Handle, content, content + new Vector2(placement.ContentBounds.Width, imageHeight));
                    else draw.AddRect(content, content + new Vector2(placement.ContentBounds.Width, imageHeight), Colour(page.Theme.Accent));
                    content.Y += imageHeight;
                    if (!string.IsNullOrWhiteSpace(asset.Caption)) Write(asset.Caption, foreground);
                }
            }
            else if (!ProfileBuilderPolicy.IsContainer(element.Kind))
            {
                var body = Body(element);
                if (body.Length > 0) Write(body, foreground);
                else if (!preview) Write("Select to add " + element.Kind.ToString().ToLowerInvariant(), Colour(page.Theme.Accent));
            }
            draw.PopClipRect();
            if (!preview && (selected == element.Id || hovered?.Element.Id == element.Id))
            {
                draw.AddRect(min, max, Colour(page.Theme.Accent), 3 * scale, ImDrawFlags.None, 2 * scale);
                var label = element.Kind == ProfileBlockKind.Flow ? "SECTION" : element.Kind == ProfileBlockKind.RichText ? "TEXT" : element.Kind.ToString().ToUpperInvariant();
                var labelSize = ImGui.CalcTextSize(label);
                var labelMin = min + new Vector2(0, -labelSize.Y - 6 * scale);
                draw.AddRectFilled(labelMin, labelMin + labelSize + new Vector2(12, 6) * scale, Colour(page.Theme.Accent), 3 * scale);
                draw.AddText(labelMin + new Vector2(6, 3) * scale, Colour(page.Theme.Background), label);
            }
        }
        if (preview) return;
        if (surfaceHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        { _pressed = hovered?.Element.Id; if (_pressed != null) select(_pressed); }
        if (surfaceHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && hovered != null)
            editText(hovered.Element.Id);
        if (surfaceActive && _pressed != null && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) _dragging = _pressed;
        var target = ProfilePageLayout.DropTarget(page, layout.Elements, mouse, width, _dragging);
        if (_dragging != null)
        {
            var line = origin + new Vector2(target.Marker.X, target.Marker.Y);
            draw.AddLine(line, line + new Vector2(target.Marker.Width, 0), Colour(page.Theme.Accent), 3 * scale);
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            { if (surfaceHovered) move(_dragging, target.ParentId, target.Index); _dragging = null; _pressed = null; }
        }
        else if (surfaceHovered && hovered == null && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            insert(target.ParentId, target.Index);
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        { _pressed = null; _dragging = null; }
    }

    private static string PlainText(string value) => FormattingTags.Replace(value, "");
    private static string PreferenceText(ProfilePreferenceDto entry)
    {
        var name = entry.Name.Length > 0 ? entry.Name : entry.CatalogueId == null ? "Custom interest"
            : ProfilePreferenceCatalogue.ById.GetValueOrDefault(entry.CatalogueId)?.Name ?? "Retired interest";
        if (ProfileBuilderPolicy.IsDirectional(entry))
            return entry.Doing == ProfilePreferenceRating.Unspecified && entry.Receiving == ProfilePreferenceRating.Unspecified ? ""
                : name + " - Doing: " + entry.Doing + " - Receiving: " + entry.Receiving;
        return entry.Rating == ProfilePreferenceRating.Unspecified ? "" : name + " - " + entry.Rating;
    }
    private static uint Colour(string rgba) => uint.TryParse(rgba.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var value)
        ? ((value >> 24) & 255) | ((value >> 8) & 0xFF00) | ((value << 8) & 0xFF0000) | ((value << 24) & 0xFF000000) : 0xFFFFFFFF;
}
