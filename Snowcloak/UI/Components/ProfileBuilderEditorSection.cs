using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public enum ProfileBuilderEditorPage { Layout, Fields, Preferences, Theme }

public sealed unsafe partial class ProfileBuilderEditorSection
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string _ident = string.Empty;
    private string _search = string.Empty;
    private string? _selected;
    private string? _dragged;
    private int _newKind;
    private int _template;
    private string _error = string.Empty;
    private bool _selectedOnly;
    private bool _snapCanvas = true;
    private float _canvasZoom = .5f;
    private string? _canvasGestureBefore;
    private string? _canvasDragging;
    private bool _canvasResizing;

    public void RememberExternalEdit(ProfileEditSession session)
    {
        if (session.Builder == null) return;
        EnsureCharacter(session.LoadedIdent);
        _undo.Push(JsonSerializer.Serialize(session.Builder)); _redo.Clear();
        _pageUndo.Push(JsonSerializer.Serialize(session.ToDocument())); _pageRedo.Clear(); TrimPageUndo();
        if (_undo.Count > 64) { var retained = _undo.Take(64).Reverse().ToArray(); _undo.Clear(); foreach (var state in retained) _undo.Push(state); }
    }

    public void Draw(ProfileEditSession session, Action changed, Action<bool> uploadImage,
        ProfileBuilderEditorPage page = ProfileBuilderEditorPage.Layout)
    {
        EnsureCharacter(session.LoadedIdent);
        if (session.Builder == null)
        {
            ImGui.TextWrapped("Create a layout from existing profile data. Publication is required to save changes.");
            if (ImGui.Button("Create classic layout")) { session.Builder = ProfileComponentDefaults.CreateTemplate(session.ToDocument()); changed(); }
            return;
        }
        if (session.Builder.Version == 2 && page == ProfileBuilderEditorPage.Layout)
        { ImGui.TextWrapped("Edit multiple pages in Design page."); return; }
        var before = JsonSerializer.Serialize(session.Builder);
        if (_canvasGestureBefore != null && page != ProfileBuilderEditorPage.Layout)
        {
            if (_canvasGestureBefore != before) { _undo.Push(_canvasGestureBefore); _redo.Clear(); }
            _canvasGestureBefore = null; _canvasDragging = null;
        }
        if (ImGui.Button("Undo") && _undo.TryPop(out var undo)) { _redo.Push(before); session.Builder = JsonSerializer.Deserialize<ProfileBuilderDto>(undo); changed(); return; }
        ImGui.SameLine();
        if (ImGui.Button("Redo") && _redo.TryPop(out var redo)) { _undo.Push(before); session.Builder = JsonSerializer.Deserialize<ProfileBuilderDto>(redo); changed(); return; }
        if (page == ProfileBuilderEditorPage.Layout)
        {
            ImGui.Combo("Template", ref _template, ProfileTemplates.Names, ProfileTemplates.Names.Length);
            if (ImGui.Button("Apply template")) ImGui.OpenPopup("Replace page layout?");
            if (ImGui.BeginPopup("Replace page layout?"))
            {
                ImGui.TextWrapped("Replace layout? Fields, interests, images and theme are kept.");
                if (ImGui.Button("Replace layout")) { session.Builder = ProfileComponentDefaults.CreateTemplate(session.ToDocument(), _template); ImGui.CloseCurrentPopup(); }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }
        var builder = session.Builder!;
        switch (page)
        {
            case ProfileBuilderEditorPage.Layout: DrawLayout(builder); break;
            case ProfileBuilderEditorPage.Fields: DrawFields(builder); break;
            case ProfileBuilderEditorPage.Preferences: DrawPreferences(builder); break;
            case ProfileBuilderEditorPage.Theme: DrawTheme(builder, uploadImage); break;
        }
        if (builder.LayoutMode == ProfileLayoutMode.Canvas)
            foreach (var block in builder.Layout) FitCanvasBlock(builder, block);
        var after = JsonSerializer.Serialize(builder);
        if (after != before)
        {
            try
            {
                ProfileBuilderPolicy.Validate(builder, allowIncomplete: true);
                if (_canvasGestureBefore == null) _undo.Push(before);
                _redo.Clear(); _error = string.Empty; changed();
                if (_undo.Count > 64) { var retained = _undo.Take(64).Reverse().ToArray(); _undo.Clear(); foreach (var state in retained) _undo.Push(state); }
                try { ProfileBuilderPolicy.Validate(builder); } catch (ArgumentException exception) { _error = exception.Message; }
            }
            catch (ArgumentException exception) { session.Builder = JsonSerializer.Deserialize<ProfileBuilderDto>(before); _error = exception.Message; }
        }
        if (!string.IsNullOrWhiteSpace(_error)) ImGui.TextWrapped(_error);
        if (_canvasGestureBefore != null && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (_canvasGestureBefore != JsonSerializer.Serialize(session.Builder))
            {
                _undo.Push(_canvasGestureBefore); _redo.Clear();
                if (_undo.Count > 64) { var retained = _undo.Take(64).Reverse().ToArray(); _undo.Clear(); foreach (var state in retained) _undo.Push(state); }
            }
            _canvasGestureBefore = null; _canvasDragging = null;
        }
    }

    private void DrawLayout(ProfileBuilderDto builder)
    {
        var mode = (int)builder.LayoutMode;
        if (ImGui.Combo("Layout style", ref mode, new[] { "Traditional flow", "Freeform canvas" }, 2))
        {
            builder.LayoutMode = (ProfileLayoutMode)mode;
            if (builder.LayoutMode == ProfileLayoutMode.Canvas)
            {
                if (builder.Layout.All(b => b.X == 0 && b.Y == 0)) ArrangeCanvas(builder);
                foreach (var block in builder.Layout) FitCanvasBlock(builder, block);
            }
        }
        if (builder.LayoutMode == ProfileLayoutMode.Canvas)
        {
            var width = builder.CanvasWidth; var heightValue = builder.CanvasHeight;
            if (ImGui.DragFloat("Canvas width", ref width, 10, 320, 4096)) builder.CanvasWidth = Math.Clamp(width, 320, 4096);
            if (ImGui.DragFloat("Canvas height", ref heightValue, 10, 320, 4096)) builder.CanvasHeight = Math.Clamp(heightValue, 320, 4096);
            foreach (var block in builder.Layout) FitCanvasBlock(builder, block);
            if (ImGui.Button("Arrange blocks in grid")) ArrangeCanvas(builder);
            DrawCanvasEditor(builder);
        }
        var kinds = Enum.GetNames<ProfileBlockKind>();
        ImGui.Combo("New block", ref _newKind, kinds, kinds.Length);
        if (ImGui.Button("Add to page")) builder.Layout.Add(new() { Kind = (ProfileBlockKind)_newKind, Rounding = 0, Title = kinds[_newKind], Audience = ProfileAudience.Public });
        var selected = Find(builder.Layout, _selected);
        if (selected != null && ProfileBuilderPolicy.IsContainer(selected.Kind))
        { ImGui.SameLine(); if (ImGui.Button("Add inside selected container")) selected.Children.Add(new() { Kind = (ProfileBlockKind)_newKind, Rounding = 0, Title = kinds[_newKind], Audience = selected.Audience }); }
        ImGui.TextWrapped("Drop on a container to nest, or drop on an element to insert before it.");
        var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var split = ImGui.GetContentRegionAvail().X >= 600 * scale;
        using var panels = ImRaii.Table("layout-outline-inspector", split ? 2 : 1, ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchSame);
        if (!panels) return;
        ImGui.TableNextColumn();
        ImGui.TextUnformatted("Page outline");
        var height = split ? Math.Max(210 * scale, ImGui.GetContentRegionAvail().Y - 12 * scale) : 230 * scale;
        using (var outline = ImRaii.Child("page-outline", new System.Numerics.Vector2(0, height), true))
            if (outline) Outline(builder.Layout, builder.Layout);
        ImGui.TableNextColumn();
        selected = Find(builder.Layout, _selected);
        if (selected != null && builder.LayoutMode == ProfileLayoutMode.Canvas && builder.Layout.Contains(selected))
        {
            ImGui.TextUnformatted("Canvas position and size");
            var x = selected.X; var y = selected.Y; var width = selected.Width; var heightValue = selected.Height;
            if (ImGui.DragFloat("X", ref x, 1, 0, builder.CanvasWidth)) selected.X = x;
            if (ImGui.DragFloat("Y", ref y, 1, 0, builder.CanvasHeight)) selected.Y = y;
            if (ImGui.DragFloat("Width", ref width, 1, 80, builder.CanvasWidth)) selected.Width = width;
            if (ImGui.DragFloat("Height", ref heightValue, 1, 80, builder.CanvasHeight)) selected.Height = heightValue;
            FitCanvasBlock(builder, selected);
            ImGui.TextWrapped("Outline order controls reading order and stacking.");
        }
        DrawBlockProperties(builder, selected);
    }

    private static void FitCanvasBlock(ProfileBuilderDto builder, ProfileBlockDto block)
    {
        block.Width = Math.Clamp(block.Width, 80, builder.CanvasWidth);
        block.Height = Math.Clamp(block.Height, 80, builder.CanvasHeight);
        block.X = Math.Clamp(block.X, 0, builder.CanvasWidth - block.Width);
        block.Y = Math.Clamp(block.Y, 0, builder.CanvasHeight - block.Height);
    }

    private static void ArrangeCanvas(ProfileBuilderDto builder)
    {
        if (builder.Layout.Count == 0) return;
        var columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(builder.Layout.Count)), 1, (int)(builder.CanvasWidth / 100));
        var rows = (int)Math.Ceiling(builder.Layout.Count / (float)columns);
        var width = (builder.CanvasWidth - (columns - 1) * 20) / columns;
        var height = Math.Max(80, Math.Min(280, (4096f - (rows - 1) * 20) / rows));
        builder.CanvasHeight = Math.Min(4096, Math.Max(builder.CanvasHeight, rows * height + (rows - 1) * 20));
        for (var index = 0; index < builder.Layout.Count; index++)
        {
            var block = builder.Layout[index];
            block.Width = width; block.Height = height;
            block.X = index % columns * (width + 20); block.Y = index / columns * (height + 20);
            FitCanvasBlock(builder, block);
        }
    }

    private void DrawCanvasEditor(ProfileBuilderDto builder)
    {
        ImGui.TextWrapped("Drag to move, or drag the corner to resize.");
        ImGui.Checkbox("Snap to 20 px grid", ref _snapCanvas);
        ImGui.SliderFloat("Canvas zoom", ref _canvasZoom, .25f, 1, "%.2fx");
        var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale * _canvasZoom;
        using var canvas = ImRaii.Child("canvas-design", new System.Numerics.Vector2(0, 330 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale), true, ImGuiWindowFlags.HorizontalScrollbar);
        if (!canvas) return;
        var origin = ImGui.GetCursorScreenPos();
        var size = new System.Numerics.Vector2(builder.CanvasWidth, builder.CanvasHeight) * scale;
        ImGui.InvisibleButton("canvas-surface", size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, 0xFF22201C);
        if (_snapCanvas)
        {
            for (var x = 0f; x <= builder.CanvasWidth; x += 20) draw.AddLine(origin + new System.Numerics.Vector2(x * scale, 0), origin + new System.Numerics.Vector2(x * scale, size.Y), 0x203F3F3F);
            for (var y = 0f; y <= builder.CanvasHeight; y += 20) draw.AddLine(origin + new System.Numerics.Vector2(0, y * scale), origin + new System.Numerics.Vector2(size.X, y * scale), 0x203F3F3F);
        }
        var mouse = (ImGui.GetIO().MousePos - origin) / scale;
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var hit = builder.Layout.LastOrDefault(b => mouse.X >= b.X && mouse.Y >= b.Y && mouse.X <= b.X + b.Width && mouse.Y <= b.Y + b.Height);
            _selected = hit?.Id; _canvasDragging = hit?.Id;
            if (hit != null)
            {
                _canvasGestureBefore = JsonSerializer.Serialize(builder);
                _canvasResizing = mouse.X > hit.X + hit.Width - 16 / scale && mouse.Y > hit.Y + hit.Height - 16 / scale;
            }
        }
        var moving = builder.Layout.FirstOrDefault(b => b.Id == _canvasDragging);
        if (moving != null && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta = ImGui.GetIO().MouseDelta / scale;
            if (_canvasResizing) { moving.Width += delta.X; moving.Height += delta.Y; }
            else { moving.X += delta.X; moving.Y += delta.Y; }
            FitCanvasBlock(builder, moving);
        }
        if (moving != null && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (_snapCanvas)
            {
                moving.X = MathF.Round(moving.X / 20) * 20; moving.Y = MathF.Round(moving.Y / 20) * 20;
                moving.Width = MathF.Round(moving.Width / 20) * 20; moving.Height = MathF.Round(moving.Height / 20) * 20;
                FitCanvasBlock(builder, moving);
            }
            _canvasDragging = null;
        }
        foreach (var block in builder.Layout)
        {
            var min = origin + new System.Numerics.Vector2(block.X, block.Y) * scale;
            var max = min + new System.Numerics.Vector2(block.Width, block.Height) * scale;
            draw.AddRectFilled(min, max, block.Id == _selected ? 0xD0604828 : 0xD03E3530);
            draw.AddRect(min, max, block.Id == _selected ? 0xFFFFAD55 : 0xFF8B8277);
            draw.PushClipRect(min, max, true);
            draw.AddText(min + new System.Numerics.Vector2(6), 0xFFFFFFFF, string.IsNullOrWhiteSpace(block.Title) ? block.Kind.ToString() : block.Title);
            draw.AddText(min + new System.Numerics.Vector2(6, 26), 0xFFBBBBBB, $"{block.Kind} - {block.Width:0} × {block.Height:0}");
            draw.PopClipRect();
            draw.AddRectFilled(max - new System.Numerics.Vector2(12), max, 0xFFFFAD55);
        }
    }

    private static void DrawBlockProperties(ProfileBuilderDto builder, ProfileBlockDto? selected, bool content = true, bool appearance = true)
    {
        ImGui.TextUnformatted("Element settings");
        if (selected == null) { ImGui.TextWrapped("Select an element."); return; }
        ImGui.Separator();
        if (content)
        {
        selected.Title = Input("Title", selected.Title, 160);
        if (selected.Kind is ProfileBlockKind.Heading or ProfileBlockKind.RichText or ProfileBlockKind.Link)
            selected.Text = Input(selected.Kind == ProfileBlockKind.Link ? "Destination URL" : "Text", selected.Text, 8000, selected.Kind != ProfileBlockKind.Link);
        selected.Audience = Audience("Visible to", selected.Audience);
        }
        if (appearance)
        {
        selected.Background = Colour("Panel background", selected.Background);
        var themeText = selected.UseThemeForeground; if (ImGui.Checkbox("Use page text colour", ref themeText)) selected.UseThemeForeground = themeText;
        if (!selected.UseThemeForeground) selected.Foreground = Colour("Text colour", selected.Foreground);
        var padding = selected.Padding; if (ImGui.SliderFloat("Padding", ref padding, 0, 64)) selected.Padding = padding;
        var spacing = selected.Spacing; if (ImGui.SliderFloat("Spacing", ref spacing, 0, 64)) selected.Spacing = spacing;
        var rounding = selected.Rounding; if (ImGui.SliderFloat("Corners", ref rounding, 0, 32)) selected.Rounding = rounding;
        var border = selected.Border; if (ImGui.SliderFloat("Border", ref border, 0, 4)) selected.Border = border;
        var size = selected.FontScale; if (ImGui.SliderFloat("Text size", ref size, .5f, 3)) selected.FontScale = size;
        var font = (int)selected.Font; var fonts = Enum.GetNames<ProfileFont>(); if (ImGui.Combo("Font", ref font, fonts, fonts.Length)) selected.Font = (ProfileFont)font;
        }
        if (content && selected.Kind == ProfileBlockKind.Fields)
            foreach (var field in builder.Fields)
            { var include = selected.References.Contains(field.Id); if (ImGui.Checkbox(field.Group + ": " + field.Label + "###" + field.Id, ref include)) { if (include) selected.References.Add(field.Id); else selected.References.Remove(field.Id); } }
        if (content && selected.Kind == ProfileBlockKind.Gallery) DrawGalleryLayout(selected);
        if (content && selected.Kind is ProfileBlockKind.Image or ProfileBlockKind.Gallery)
            foreach (var asset in builder.Assets)
            { var include = selected.References.Contains(asset.Hash); if (ImGui.Checkbox((asset.Caption.Length > 0 ? asset.Caption : asset.Hash[..8]) + "###" + asset.Hash, ref include)) { if (include) selected.References.Add(asset.Hash); else selected.References.Remove(asset.Hash); } }
    }

    private static void DrawGalleryLayout(ProfileBlockDto block)
    {
        var layout = (int)block.GalleryLayout;
        if (ImGui.Combo("Gallery display", ref layout, new[] { "Stacked", "Carousel" }, 2)) block.GalleryLayout = (ProfileGalleryLayout)layout;
    }

    private void Outline(List<ProfileBlockDto> nodes, List<ProfileBlockDto> roots)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index]; using var id = ImRaii.PushId(node.Id);
            if (ImGui.Selectable(node.Kind + " - " + node.Title, _selected == node.Id)) _selected = node.Id;
            if (ImGui.BeginDragDropSource()) { _dragged = node.Id; ImGui.SetDragDropPayload("PROFILE_NODE", new byte[] { 1 }); ImGui.TextUnformatted(node.Title); ImGui.EndDragDropSource(); }
            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("PROFILE_NODE");
                if (!payload.IsNull && payload.IsDelivery() && _dragged != null && _dragged != node.Id)
                {
                    var dragged = Find(roots, _dragged);
                    if (dragged != null && Find(dragged.Children, node.Id) == null)
                    {
                        Remove(roots, dragged.Id);
                        if (ProfileBuilderPolicy.IsContainer(node.Kind)) node.Children.Add(dragged);
                        else nodes.Insert(Math.Max(0, nodes.IndexOf(node)), dragged);
                        _dragged = null; ImGui.EndDragDropTarget(); return;
                    }
                }
                ImGui.EndDragDropTarget();
            }
            if (ImGui.SmallButton("Up") && index > 0) { (nodes[index - 1], nodes[index]) = (nodes[index], nodes[index - 1]); return; }
            ImGui.SameLine(); if (ImGui.SmallButton("Down") && index + 1 < nodes.Count) { (nodes[index + 1], nodes[index]) = (nodes[index], nodes[index + 1]); return; }
            ImGui.SameLine(); if (ImGui.SmallButton("Duplicate"))
            { var copy = JsonSerializer.Deserialize<ProfileBlockDto>(JsonSerializer.Serialize(node))!; NewIds(copy); nodes.Insert(index + 1, copy); return; }
            ImGui.SameLine(); if (ImGui.SmallButton("Delete")) { nodes.RemoveAt(index); return; }
            if (node.Children.Count > 0) { ImGui.Indent(); Outline(node.Children, roots); ImGui.Unindent(); }
        }
    }

    private static void DrawFields(ProfileBuilderDto builder, ProfileBlockDto? binding = null)
    {
        if (ImGui.Button("Add custom field"))
        {
            var field = new ProfileFieldDto { Label = "New field" }; builder.Fields.Add(field);
            if (binding != null) binding.References.Add(field.Id);
            else ProfilePresentationPolicy.PlaceNewTraditionalField(builder, field);
        }
        foreach (var field in builder.Fields.Where(f => binding == null || binding.References.Contains(f.Id)).ToArray())
        {
            using var id = ImRaii.PushId(field.Id);
            if (!ImGui.CollapsingHeader(field.Group + " - " + field.Label)) continue;
            field.Group = Input("Group", field.Group, 80); field.Label = Input("Label", field.Label, 160);
            field.Value = Input("Value", field.Value, 8000, true); field.Audience = Audience("Audience", field.Audience);
            if (ImGui.Button("Remove field")) { builder.Fields.Remove(field); foreach (var layout in ProfileBuilderPolicy.Layouts(builder)) RemoveReference(layout, field.Id); }
        }
    }

    private static void DrawTheme(ProfileBuilderDto builder, Action<bool> upload)
    {
        builder.Theme.Background = Colour("Page background", builder.Theme.Background);
        builder.Theme.Foreground = Colour("Page text", builder.Theme.Foreground);
        builder.Theme.Accent = Colour("Accent", builder.Theme.Accent);
        var windowColours = builder.Theme.CustomWindowColours;
        if (ImGui.Checkbox("Custom profile window colours", ref windowColours)) builder.Theme.CustomWindowColours = windowColours;
        if (windowColours)
        {
            builder.Theme.WindowBackground = Colour("Window background", builder.Theme.WindowBackground);
            builder.Theme.WindowText = Colour("Window text", builder.Theme.WindowText);
            builder.Theme.WindowBorder = Colour("Window border", builder.Theme.WindowBorder);
            builder.Theme.WindowTitle = Colour("Window title bar", builder.Theme.WindowTitle);
            builder.Theme.WindowButton = Colour("Window buttons", builder.Theme.WindowButton);
            builder.Theme.WindowAccent = Colour("Window headings and accents", builder.Theme.WindowAccent);
            builder.Theme.WindowMutedText = Colour("Window muted labels", builder.Theme.WindowMutedText);
            builder.Theme.WindowSection = Colour("Window section headers", builder.Theme.WindowSection);
            builder.Theme.WindowCard = Colour("Window cards", builder.Theme.WindowCard);
            var chips = builder.Theme.CustomChipColours;
            if (ImGui.Checkbox("Custom tag and badge colours", ref chips)) builder.Theme.CustomChipColours = chips;
            if (chips)
            {
                builder.Theme.WindowTag = Colour("Window tags", builder.Theme.WindowTag);
                builder.Theme.WindowBadge = Colour("Window RP badges", builder.Theme.WindowBadge);
            }
        }
        builder.Theme.BackgroundAudience = Audience("Background audience", builder.Theme.BackgroundAudience);
        if (ImGui.Button("Upload background PNG")) upload(true);
        ImGui.SameLine(); if (ImGui.Button("Clear background")) builder.Theme.BackgroundHash = null;
        if (ImGui.Button("Upload gallery PNG")) upload(false);
        foreach (var asset in builder.Assets.ToArray())
        {
            using var id = ImRaii.PushId(asset.Hash); ImGui.TextDisabled(asset.Hash[..8]);
            asset.Caption = Input("Caption", asset.Caption, 500); asset.Audience = Audience("Image audience", asset.Audience);
            if (ImGui.Button("Remove image")) { builder.Assets.Remove(asset); foreach (var layout in ProfileBuilderPolicy.Layouts(builder)) RemoveReference(layout, asset.Hash); }
        }
    }

    private static string Input(string label, string value, int max, bool multiline = false)
    { var text = value; if (multiline) ImGui.InputTextMultiline(label, ref text, max + 1, new System.Numerics.Vector2(-1, 100)); else ImGui.InputText(label, ref text, max + 1); return text; }
    private static string Colour(string label, string value)
    {
        var rgba = new System.Numerics.Vector4(Convert.ToByte(value.Substring(1, 2), 16), Convert.ToByte(value.Substring(3, 2), 16),
            Convert.ToByte(value.Substring(5, 2), 16), Convert.ToByte(value.Substring(7, 2), 16)) / 255f;
        if (!ImGui.ColorEdit4(label, ref rgba)) return value;
        return $"#{(byte)Math.Clamp((int)MathF.Round(rgba.X * 255), 0, 255):X2}{(byte)Math.Clamp((int)MathF.Round(rgba.Y * 255), 0, 255):X2}{(byte)Math.Clamp((int)MathF.Round(rgba.Z * 255), 0, 255):X2}{(byte)Math.Clamp((int)MathF.Round(rgba.W * 255), 0, 255):X2}";
    }
    private static ProfileAudience Audience(string label, ProfileAudience value) { var index = (int)value; var names = Enum.GetNames<ProfileAudience>(); ImGui.Combo(label, ref index, names, names.Length); return (ProfileAudience)index; }
    private static ProfileBlockDto? Find(List<ProfileBlockDto> nodes, string? id) => nodes.FirstOrDefault(n => n.Id == id) ?? nodes.Select(n => Find(n.Children, id)).FirstOrDefault(n => n != null);
    private static bool Remove(List<ProfileBlockDto> nodes, string id) { var node = nodes.FirstOrDefault(n => n.Id == id); if (node != null) return nodes.Remove(node); return nodes.Any(n => Remove(n.Children, id)); }
    private static void NewIds(ProfileBlockDto node) { node.Id = Guid.NewGuid().ToString("N"); foreach (var child in node.Children) NewIds(child); }
    private static void RemoveReference(List<ProfileBlockDto> nodes, string reference) { foreach (var node in nodes) { node.References.Remove(reference); RemoveReference(node.Children, reference); } }
}
