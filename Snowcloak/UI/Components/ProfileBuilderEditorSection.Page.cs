using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public sealed unsafe partial class ProfileBuilderEditorSection
{
    private readonly Stack<string> _pageUndo = new();
    private readonly Stack<string> _pageRedo = new();
    private string? _pageEditBefore;
    private string? _inlineElement;
    private bool _focusInline;
    private bool _selectionHandled;
    private bool _pagePreview;
    private bool _compactInspector;
    private int _pageWidth;
    private Func<ProfileBuilderDto, ProfileBuilderDto>? _pendingPageEdit;

    public void DrawPage(ProfileEditSession session, Action changed, Action<bool> uploadImage,
        Action<ProfilePageEditingContext?> renderPage, Action? previewSettings = null,
        Action<ProfileBlockKind>? editCanonicalContent = null)
    {
        if (session.Builder == null) return;
        if (session.Builder.Version == 2)
        { ImGui.TextWrapped("Edit multiple pages in Design page."); return; }
        EnsureCharacter(session.LoadedIdent);
        _selectionHandled = false;
        var before = JsonSerializer.Serialize(session.ToDocument());
        if (PageCommand("Undo"))
        {
            CompletePageEdit();
            if (_pageUndo.TryPop(out var undo))
            { _pageRedo.Push(before); _inlineElement = null; session.ReplaceDocument(JsonSerializer.Deserialize<CharacterProfileDocumentDto>(undo)!, true); changed(); return; }
        }
        ImGui.SameLine();
        if (PageCommand("Redo") && _pageRedo.TryPop(out var redo))
        { _pageUndo.Push(before); _inlineElement = null; session.ReplaceDocument(JsonSerializer.Deserialize<CharacterProfileDocumentDto>(redo)!, true); changed(); return; }
        ImGui.SameLine();
        if (PageCommand("+ Add")) ImGui.OpenPopup("add-page-element");
        if (ImGui.BeginPopup("add-page-element")) { DrawInsertMenu(session.Builder, null, session.Builder.Layout.Count); ImGui.EndPopup(); }
        ImGui.SameLine();
        if (PageCommand("Templates")) ImGui.OpenPopup("page-templates");
        if (ImGui.BeginPopup("page-templates"))
        {
            var templates = ProfileTemplates.Names.Take(4).ToArray();
            _template = Math.Min(_template, templates.Length - 1);
            ImGui.Combo("Starting design", ref _template, templates, templates.Length);
            ImGui.TextWrapped("Replace page elements? Data, interests, images and theme are retained. Undo is available.");
            if (ImGui.Button("Use template"))
            { var document = session.ToDocument(); _pendingPageEdit = _ => ProfileComponentDefaults.CreateTemplate(document, _template); _selected = null; ImGui.CloseCurrentPopup(); }
            if (ImGui.Button("Start with an empty page"))
            { _pendingPageEdit = page => { var copy = ProfileBuilderPolicy.Clone(page); copy.Layout.Clear(); copy.LayoutMode = ProfileLayoutMode.Traditional; return copy; }; _selected = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        if (PageCommand(_pagePreview ? "Back to design" : "Preview", _pagePreview)) _pagePreview = !_pagePreview;
        if (_pagePreview) previewSettings?.Invoke();
        ImGui.SetNextItemWidth(190 * ImGuiHelpers.GlobalScale);
        ImGui.Combo("View width", ref _pageWidth, new[] { "Available width", "Phone - 360", "Compact - 640", "Desktop - 960" }, 4);
        var scale = ImGuiHelpers.GlobalScale;
        var wide = ImGui.GetContentRegionAvail().X >= 900 * scale;
        if (!wide && !_pagePreview)
        { ImGui.Checkbox("Show selected element settings", ref _compactInspector); }
        ImGui.TextDisabled(_pagePreview ? "Preview - Local draft" : "Select to edit - Drag to reorder - Double-click to add");
        var height = Math.Max(150 * scale, ImGui.GetContentRegionAvail().Y - 15 * scale);
        var inspector = !_pagePreview && (wide || _compactInspector);
        using (var panels = ImRaii.Table("visual-page-editor", inspector && wide ? 2 : 1,
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            if (panels)
            {
                ImGui.TableSetupColumn("Page", ImGuiTableColumnFlags.WidthStretch);
                if (inspector && wide) ImGui.TableSetupColumn("Inspector", ImGuiTableColumnFlags.WidthFixed, 320 * scale);
                ImGui.TableNextColumn();
                if (!inspector || wide)
                {
                    using var stageColour = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(.08f, .09f, .11f, 1));
                    using var stage = ImRaii.Child("page-design-stage", new Vector2(0, height), false);
                    if (stage)
                    {
                        var available = ImGui.GetContentRegionAvail().X;
                        var requested = _pageWidth switch { 1 => 360, 2 => 640, 3 => 960, _ => available / scale };
                        var width = Math.Min(available, requested * scale);
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (available - width) / 2));
                        using var page = ImRaii.Child("rendered-editable-page", new Vector2(width, 0), false);
                        if (page) renderPage(_pagePreview ? null : new(
                            element => DrawElementHandle(session.Builder!, element),
                            element => DrawInlineContent(session, element),
                            DrawSelectionOutline,
                            (parent, index) => DrawInsertPoint(session.Builder!, parent, index)));
                    }
                }
                if (inspector)
                {
                    if (wide) ImGui.TableNextColumn();
                    using var properties = ImRaii.Child("page-context-inspector", new Vector2(0, height), true);
                    if (properties) DrawPageInspector(session, uploadImage, editCanonicalContent);
                }
            }
        }
        if (_pendingPageEdit != null)
        {
            try { session.Builder = _pendingPageEdit(session.Builder!); _error = string.Empty; }
            catch (ArgumentException exception) { _error = exception.Message; }
            finally { _pendingPageEdit = null; }
        }
        try { ProfileBuilderPolicy.Validate(session.Builder!, allowIncomplete: true); }
        catch (ArgumentException exception)
        { session.ReplaceDocument(JsonSerializer.Deserialize<CharacterProfileDocumentDto>(before)!, session.Dirty); _error = exception.Message; }
        var after = JsonSerializer.Serialize(session.ToDocument());
        if (after != before) { _pageEditBefore ??= before; _pageRedo.Clear(); changed(); }
        if (_pageEditBefore != null && !ImGui.IsAnyItemActive())
            CompletePageEdit();
        if (!string.IsNullOrWhiteSpace(_error)) ImGui.TextWrapped(_error);
    }

    private void EnsureCharacter(string ident)
    {
        if (_ident == ident) return;
        _ident = ident; _selected = null; _inlineElement = null; _dragged = null;
        _undo.Clear(); _redo.Clear(); _pageUndo.Clear(); _pageRedo.Clear();
        _pageEditBefore = null; _pendingPageEdit = null; _canvasGestureBefore = null; _canvasDragging = null;
        _nativeInsertParent = null; _nativeInsertIndex = 0;
    }

    private static bool PageCommand(string label, bool selected = false)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var text = ImGui.CalcTextSize(label);
        var size = text + new Vector2(24, 16) * scale;
        var origin = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("command:" + label, size);
        var draw = ImGui.GetWindowDrawList();
        var colour = selected ? 0xFF594A38u : ImGui.IsItemHovered() ? 0xFF494139u : 0xFF332D27u;
        draw.AddRectFilled(origin, origin + size, colour, 6 * scale);
        draw.AddText(origin + new Vector2(12, 8) * scale, 0xFFF0E8E0, label);
        return clicked;
    }

    private void CompletePageEdit()
    {
        if (_pageEditBefore == null) return;
        _pageUndo.Push(_pageEditBefore); TrimPageUndo(); _pageEditBefore = null;
    }

    public string? SelectedElementId => _selected;
    public void SelectElement(string? id) => _selected = id;
    public bool TryPageHistory(ProfileEditSession session, bool redo)
    {
        EnsureCharacter(session.LoadedIdent); CompletePageEdit();
        var source = redo ? _pageRedo : _pageUndo;
        var target = redo ? _pageUndo : _pageRedo;
        if (!source.TryPop(out var saved)) return false;
        target.Push(JsonSerializer.Serialize(session.ToDocument()));
        session.ReplaceDocument(JsonSerializer.Deserialize<CharacterProfileDocumentDto>(saved)!, true);
        _selected = null; return true;
    }

    public void DrawNativeSurface(NativeProfilePageCanvas canvas, ProfileEditSession session,
        CharacterProfileDocumentDto document, Func<string, Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap?> texture)
    {
        string? insertParent = null;
        var insertIndex = 0;
        canvas.Draw(session.LoadedIdent, document, _selected, texture,
            id => { _selected = id; _inlineElement = null; },
            id => { _selected = id; _compactInspector = true; _inlineElement = null; },
            (id, parent, index) => _pendingPageEdit = source => ProfilePageEditPolicy.Move(source, id, parent, index),
            (parent, index) => { insertParent = parent; insertIndex = index; ImGui.OpenPopup("native-insert-element"); }, false);
        if (ImGui.BeginPopup("native-insert-element"))
        {
            if (ImGui.IsWindowAppearing()) { _nativeInsertParent = insertParent; _nativeInsertIndex = insertIndex; }
            DrawInsertMenu(session.Builder!, _nativeInsertParent, _nativeInsertIndex);
            ImGui.EndPopup();
        }
    }

    private string? _nativeInsertParent;
    private int _nativeInsertIndex;

    public void UseUploadedAsset(ProfileEditSession session, string hash, string? elementId)
    {
        var selected = Find(session.Builder == null ? [] : ProfileBuilderPolicy.Layouts(session.Builder).SelectMany(layout => layout).ToList(), elementId);
        if (selected?.Kind == ProfileBlockKind.Image) { selected.References.Clear(); selected.References.Add(hash); }
        else if (selected?.Kind == ProfileBlockKind.Gallery && !selected.References.Contains(hash)) selected.References.Add(hash);
    }

    private void TrimPageUndo()
    {
        if (_pageUndo.Count <= 64) return;
        var retained = _pageUndo.Take(64).Reverse().ToArray(); _pageUndo.Clear();
        foreach (var state in retained) _pageUndo.Push(state);
    }

    private static string ElementName(ProfileBlockKind kind) => kind switch
    {
        ProfileBlockKind.Flow => "Section", ProfileBlockKind.RichText => "Text", ProfileBlockKind.Heading => "Heading",
        ProfileBlockKind.Fields => "Character details", ProfileBlockKind.Link => "Button",
        ProfileBlockKind.OocNotes => "OOC notes", ProfileBlockKind.Overview => "Story",
        ProfileBlockKind.Preferences => "Interests", _ => kind.ToString()
    };

    private void DrawElementHandle(ProfileBuilderDto page, ProfileBlockDto element)
    {
        if (ImGui.SmallButton("Move " + ElementName(element.Kind) + "###select-element"))
        { if (_selected != element.Id) _inlineElement = null; _selected = element.Id; _selectionHandled = true; }
        if (ImGui.BeginDragDropSource())
        { _selected = element.Id; _inlineElement = null; _dragged = element.Id; ImGui.SetDragDropPayload("PROFILE_PAGE_ELEMENT", new ReadOnlySpan<byte>(new byte[] { 1 })); ImGui.TextUnformatted(ElementName(element.Kind)); ImGui.EndDragDropSource(); }
        if (_selected != element.Id) return;
        if (element.Kind is ProfileBlockKind.RichText or ProfileBlockKind.Heading or ProfileBlockKind.Overview or ProfileBlockKind.OocNotes or ProfileBlockKind.AdultPreferences)
        { ImGui.SameLine(); if (ImGui.SmallButton("Edit text")) { _inlineElement = element.Id; _focusInline = true; } }
        if (ProfileBuilderPolicy.IsContainer(element.Kind))
        {
            ImGui.SameLine(); if (ImGui.SmallButton("Add inside")) ImGui.OpenPopup("add-inside");
            if (ImGui.BeginPopup("add-inside")) { DrawInsertMenu(page, element.Id, element.Children.Count); ImGui.EndPopup(); }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy")) _pendingPageEdit = source => ProfilePageEditPolicy.Duplicate(source, element.Id);
        ImGui.SameLine();
        if (ImGui.SmallButton("Remove")) _pendingPageEdit = source => ProfilePageEditPolicy.Delete(source, element.Id);
    }

    private void DrawInsertPoint(ProfileBuilderDto page, ProfileBlockDto? parent, int index)
    {
        using var id = ImRaii.PushId("insert:" + (parent?.Id ?? "root") + ":" + index);
        if (ImGui.Button("+###insert", new Vector2(-1, 18 * ImGuiHelpers.GlobalScale))) ImGui.OpenPopup("insert-element");
        if (ImGui.BeginPopup("insert-element")) { DrawInsertMenu(page, parent?.Id, index); ImGui.EndPopup(); }
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("PROFILE_PAGE_ELEMENT");
            if (!payload.IsNull && payload.IsDelivery() && _dragged != null)
            { var dragged = _dragged; var parentId = parent?.Id; _pendingPageEdit = source => ProfilePageEditPolicy.Move(source, dragged, parentId, index); _dragged = null; }
            ImGui.EndDragDropTarget();
        }
    }

    private void DrawInsertMenu(ProfileBuilderDto page, string? parentId, int index)
    {
        foreach (var kind in new[] { ProfileBlockKind.RichText, ProfileBlockKind.Heading, ProfileBlockKind.Image,
            ProfileBlockKind.Gallery, ProfileBlockKind.Link, ProfileBlockKind.Divider, ProfileBlockKind.Flow,
            ProfileBlockKind.Columns, ProfileBlockKind.Tabs, ProfileBlockKind.Fields, ProfileBlockKind.Hooks,
            ProfileBlockKind.Overview, ProfileBlockKind.OocNotes, ProfileBlockKind.Preferences, ProfileBlockKind.Boundaries })
        {
            if (!ImGui.Selectable(ElementName(kind))) continue;
            var parent = Find(page.Layout, parentId);
            var element = new ProfileBlockDto { Kind = kind, Rounding = 0, Audience = parent?.Audience ?? ProfileAudience.Public };
            if (kind is ProfileBlockKind.RichText or ProfileBlockKind.Heading) element.Text = kind == ProfileBlockKind.Heading ? "New heading" : "Write your text in Content settings.";
            if (kind == ProfileBlockKind.Fields) element.References = page.Fields.Select(f => f.Id).ToList();
            if (kind is ProfileBlockKind.Image or ProfileBlockKind.Gallery) element.References = page.Assets.Take(kind == ProfileBlockKind.Image ? 1 : 32).Select(a => a.Hash).ToList();
            if (kind == ProfileBlockKind.Columns) element.Children = [new() { Rounding = 0, Audience = element.Audience }, new() { Rounding = 0, Audience = element.Audience }];
            if (kind == ProfileBlockKind.Tabs) element.Children = [new() { Title = "First tab", Rounding = 0, Audience = element.Audience }];
            _pendingPageEdit = source => ProfilePageEditPolicy.Insert(source, parentId, index, element);
            _selected = element.Id; _inlineElement = null;
        }
    }

    private bool DrawInlineContent(ProfileEditSession session, ProfileBlockDto element)
    {
        if (_inlineElement != element.Id) return false;
        var value = element.Kind switch
        { ProfileBlockKind.Overview => session.Overview, ProfileBlockKind.OocNotes => session.OocNotes,
            ProfileBlockKind.AdultPreferences => session.AdultPreferences, _ => element.Text };
        if (_focusInline) { ImGui.SetKeyboardFocusHere(); _focusInline = false; }
        if (ImGui.InputTextMultiline("##write-on-page", ref value, 8000, new Vector2(-1, 140 * ImGuiHelpers.GlobalScale)))
            switch (element.Kind)
            { case ProfileBlockKind.Overview: session.Overview = value; break; case ProfileBlockKind.OocNotes: session.OocNotes = value; break;
                case ProfileBlockKind.AdultPreferences: session.AdultPreferences = value; break; default: element.Text = value; break; }
        if (ImGui.Button("Done editing")) _inlineElement = null;
        return true;
    }

    private void DrawSelectionOutline(ProfileBlockDto element, Vector2 min, Vector2 max)
    {
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        if (element.Id == _selected || hovered)
            ImGui.GetWindowDrawList().AddRect(min, max, element.Id == _selected ? 0xFFFFAD55 : 0x708B8277, 3, ImDrawFlags.None, 2);
        if (!_selectionHandled && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsAnyItemActive())
        { if (_selected != element.Id) _inlineElement = null; _selected = element.Id; _selectionHandled = true; }
        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && element.Kind is ProfileBlockKind.RichText or ProfileBlockKind.Heading or ProfileBlockKind.Overview or ProfileBlockKind.OocNotes or ProfileBlockKind.AdultPreferences)
        { _selected = element.Id; _inlineElement = element.Id; _focusInline = true; _selectionHandled = true; }
    }

    private void DrawPageInspector(ProfileEditSession session, Action<bool> uploadImage, Action<ProfileBlockKind>? editCanonicalContent)
    {
        var page = session.Builder!;
        var selected = Find(page.Layout, _selected);
        if (selected == null)
        { ImGui.TextUnformatted("Page appearance"); ImGui.TextWrapped("Select to edit. Drag to reorder. Double-click empty space to add."); DrawTheme(page, uploadImage); return; }
        ImGui.TextUnformatted(ElementName(selected.Kind));
        var parent = FindParent(page.Layout, selected.Id);
        if (parent != null && ImGui.SmallButton("Select containing " + ElementName(parent.Kind))) { _selected = parent.Id; return; }
        if (ImGui.Button("Page settings")) { _selected = null; _inlineElement = null; return; }
        ImGui.SameLine();
        if (ImGui.Button("Duplicate")) _pendingPageEdit = source => ProfilePageEditPolicy.Duplicate(source, selected.Id);
        ImGui.SameLine();
        if (ImGui.Button("Delete")) _pendingPageEdit = source => ProfilePageEditPolicy.Delete(source, selected.Id);
        if (ProfileBuilderPolicy.IsContainer(selected.Kind))
        {
            if (ImGui.Button("Add inside section")) ImGui.OpenPopup("native-add-inside");
            if (ImGui.BeginPopup("native-add-inside")) { DrawInsertMenu(page, selected.Id, selected.Children.Count); ImGui.EndPopup(); }
            var arrangement = selected.Kind == ProfileBlockKind.Columns ? 1 : selected.Kind == ProfileBlockKind.Tabs ? 2 : 0;
            if (ImGui.Combo("Arrangement", ref arrangement, new[] { "Vertical section", "Columns", "Tabs" }, 3))
                selected.Kind = arrangement == 1 ? ProfileBlockKind.Columns : arrangement == 2 ? ProfileBlockKind.Tabs : ProfileBlockKind.Flow;
        }
        if (ImGui.BeginTabBar("native-element-properties"))
        {
        if (ImGui.BeginTabItem("Appearance"))
        {
            DrawBlockProperties(page, selected, content: false);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Content"))
        {
        DrawBlockProperties(page, selected, appearance: false);
        if (selected.Kind == ProfileBlockKind.Overview) session.Overview = Input("Story", session.Overview, 8000, true);
        if (selected.Kind == ProfileBlockKind.OocNotes) session.OocNotes = Input("OOC notes", session.OocNotes, 8000, true);
        if (selected.Kind == ProfileBlockKind.AdultPreferences) session.AdultPreferences = Input("Adult preferences", session.AdultPreferences, 8000, true);
        if (selected.Kind is ProfileBlockKind.Image or ProfileBlockKind.Gallery)
        {
            if (selected.Kind == ProfileBlockKind.Gallery) DrawGalleryLayout(selected);
            if (ImGui.Button("Add image from file")) uploadImage(false);
            foreach (var asset in page.Assets.Where(a => selected.References.Contains(a.Hash)))
            {
                using var assetId = ImRaii.PushId(asset.Hash);
                asset.Caption = Input("Caption", asset.Caption, 500);
                asset.Audience = Audience("Image visible to", asset.Audience);
            }
        }
        if (selected.Kind == ProfileBlockKind.Preferences) DrawPreferences(page);
        if (selected.Kind == ProfileBlockKind.Fields) DrawFields(page, selected);
        if (selected.Kind is ProfileBlockKind.Hooks or ProfileBlockKind.Boundaries) editCanonicalContent?.Invoke(selected.Kind);
        ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
        }
        ImGui.Separator();
        if (ImGui.Button("Move earlier")) QueueNeighbourMove(page, selected.Id, -1);
        ImGui.SameLine(); if (ImGui.Button("Move later")) QueueNeighbourMove(page, selected.Id, 1);
    }

    private void QueueNeighbourMove(ProfileBuilderDto page, string id, int direction)
    {
        var location = ProfilePageEditPolicy.Locate(page.Layout, id);
        if (location == null) return;
        var destinationIndex = direction < 0 ? location.Value.Index - 1 : location.Value.Index + 2;
        if (destinationIndex < 0 || destinationIndex > location.Value.Nodes.Count) return;
        string? Parent(List<ProfileBlockDto> nodes)
        { foreach (var node in nodes) { if (node.Children == location.Value.Nodes) return node.Id; var nested = Parent(node.Children); if (nested != null) return nested; } return null; }
        var parent = Parent(page.Layout);
        _pendingPageEdit = source => ProfilePageEditPolicy.Move(source, id, parent, destinationIndex);
    }

    private static ProfileBlockDto? FindParent(List<ProfileBlockDto> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Any(child => child.Id == id)) return node;
            var nested = FindParent(node.Children, id);
            if (nested != null) return nested;
        }
        return null;
    }
}
