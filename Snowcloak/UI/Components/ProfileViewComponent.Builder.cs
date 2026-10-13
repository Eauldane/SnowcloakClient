using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.UI.Components;

public sealed partial class ProfileViewComponent
{
    public void DrawEditablePage(ProfileViewRequest request) => DrawBuilder(request.Profile.Document,
        request.ViewerPrivateDocument, request.IdPrefix, request);
    private bool _comparePreferences;
    private int _comparisonFilter;
    private string _comparisonProfile = string.Empty;
    private string? _nativeProfilePage;
    private readonly Dictionary<string, float> _blockHeights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _galleryImageSelections = new(StringComparer.Ordinal);
    private static Vector4 PreferenceColour(ProfilePreferenceRating rating) => rating switch
    {
        ProfilePreferenceRating.Favourite => new(.25f, .6f, 1, 1),
        ProfilePreferenceRating.Yes => new(.35f, .85f, .4f, 1),
        ProfilePreferenceRating.Maybe => new(1, .8f, .25f, 1),
        ProfilePreferenceRating.No => new(1, .35f, .35f, 1), _ => new(.6f, .6f, .6f, 1)
    };

    private void DrawBuilder(CharacterProfileDocumentDto document, CharacterProfileDocumentDto? mine, string id, ProfileViewRequest? request)
    {
        var builder = document.Builder!;
        if (_comparisonProfile != id) { _comparisonProfile = id; _comparePreferences = false; _comparisonFilter = 0; _nativeProfilePage = null; _blockHeights.Clear(); }
        if (builder.Version == 2)
        {
            var pages = builder.Pages.Where(p => p.Layout.Any(block => HasContent(block, document))).ToArray();
            if (!pages.Any(p => p.Id == _nativeProfilePage)) _nativeProfilePage = pages.FirstOrDefault(p => p.Id == builder.StartPageId)?.Id ?? pages.FirstOrDefault()?.Id;
            foreach (var page in builder.Theme.Navigation == ProfileNavigationStyle.Hidden ? Array.Empty<ProfilePageDto>() : pages)
            {
                using var pageId = ImRaii.PushId(page.Id);
                if (ImGui.Button(page.Id == _nativeProfilePage ? $"[{page.Title}]" : page.Title)) _nativeProfilePage = page.Id;
                ImGui.SameLine();
            }
            ImGui.NewLine();
            builder = builder with { Layout = pages.FirstOrDefault(p => p.Id == _nativeProfilePage)?.Layout ?? [] };
            document = document with { Builder = builder };
        }
        DrawPreferenceComparisonControls(builder, mine, request);
        using var foreground = ImRaii.PushColor(ImGuiCol.Text, ReaderHighContrast ? Vector4.One : ParseColour(builder.Theme.Foreground));
        using var pageBackground = ImRaii.PushColor(ImGuiCol.ChildBg, ReaderHighContrast ? new Vector4(0,0,0,1) : ParseColour(builder.Theme.Background));
        var canvas = builder.LayoutMode == ProfileLayoutMode.Canvas &&
            (request?.CanvasLayoutOverride ?? ImGui.GetContentRegionAvail().X >= 600 * ImGuiHelpers.GlobalScale);
        if (builder.LayoutMode == ProfileLayoutMode.Canvas && !canvas) ImGui.TextDisabled("Compact view - blocks shown in reading order");
        var pageHeight = canvas ? Math.Min(builder.CanvasHeight, 700) * ImGuiHelpers.GlobalScale
            : _blockHeights.GetValueOrDefault(id + ":page", 400 * ImGuiHelpers.GlobalScale);
        ImGui.BeginChild("profile-page", new Vector2(0, pageHeight), false, canvas ? ImGuiWindowFlags.HorizontalScrollbar
            : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var origin = ImGui.GetCursorPos();
        if (!ReaderReduceImages && builder.Theme.BackgroundHash != null && request?.GetAssetTexture?.Invoke(builder.Theme.BackgroundHash) is { } background)
        {
            var min = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddImage(background.Handle, min, min + (canvas
                ? new Vector2(builder.CanvasWidth, builder.CanvasHeight) * ImGuiHelpers.GlobalScale
                : new Vector2(ImGui.GetContentRegionAvail().X, pageHeight)));
        }
        for (var index = 0; index < builder.Layout.Count; index++)
        {
            var block = builder.Layout[index];
            if (!canvas) request?.PageEditing?.InsertPoint(null, index);
            if (request?.PageEditing == null && !HasContent(block, document)) continue;
            if (canvas) ImGui.SetCursorPos(origin + new Vector2(block.X, block.Y) * ImGuiHelpers.GlobalScale);
            DrawBlock(block, document, mine, id, request, 0,
                canvas ? new Vector2(block.Width, block.Height) * ImGuiHelpers.GlobalScale : null);
        }
        if (!canvas) request?.PageEditing?.InsertPoint(null, builder.Layout.Count);
        if (canvas) { ImGui.SetCursorPos(origin); ImGui.Dummy(new Vector2(builder.CanvasWidth, builder.CanvasHeight) * ImGuiHelpers.GlobalScale); }
        else _blockHeights[id + ":page"] = Math.Max(1, ImGui.GetCursorPosY());
        ImGui.EndChild();
    }

    private void DrawBlock(ProfileBlockDto block, CharacterProfileDocumentDto document, CharacterProfileDocumentDto? mine,
        string prefix, ProfileViewRequest? request, int depth, Vector2? fixedSize = null)
    {
        if (depth > 4 || (request?.PageEditing == null && !HasContent(block, document))) return;
        using var id = ImRaii.PushId(prefix + block.Id);
        using var colour = ImRaii.PushColor(ImGuiCol.Text, ReaderHighContrast ? Vector4.One : ParseColour(block.UseThemeForeground ? document.Builder!.Theme.Foreground : block.Foreground));
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, ReaderHighContrast ? new Vector4(0,0,0,1) : ParseColour(block.Background));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, block.Rounding * ImGuiHelpers.GlobalScale);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, block.Rounding * ImGuiHelpers.GlobalScale);
        using var border = ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, block.Border * ImGuiHelpers.GlobalScale);
        var inset = Math.Min(block.Padding * ImGuiHelpers.GlobalScale, Math.Max(0, ((fixedSize?.X ?? ImGui.GetContentRegionAvail().X) - 120 * ImGuiHelpers.GlobalScale) / 2));
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(inset));
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(block.Spacing * ImGuiHelpers.GlobalScale));
        var heightKey = prefix + block.Id;
        var height = _blockHeights.GetValueOrDefault(heightKey, 160f * ImGuiHelpers.GlobalScale);
        ImGui.BeginChild("block", fixedSize ?? new Vector2(0, height), block.Border > 0,
            fixedSize.HasValue ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        using var font = block.Font switch { ProfileFont.Game => _fontService.GameFont.Push(), ProfileFont.Sans => _fontService.ProfileSansFont.Push(), ProfileFont.Serif => _fontService.ProfileSerifFont.Push(), _ => null };
        ImGui.SetWindowFontScale(block.FontScale);
        try
        {
            request?.PageEditing?.BeforeElement(block);
            if (!string.IsNullOrWhiteSpace(block.Title)) ImGui.TextColored(ReaderHighContrast ? new Vector4(.7f,.85f,1,1) : ParseColour(document.Builder!.Theme.Accent), block.Title);
            if (request?.PageEditing?.EditContent(block) == true) { }
            else DrawBlockContent(block, document, mine, prefix, request, depth);
        }
        finally
        {
            _blockHeights[heightKey] = Math.Max(ImGui.GetTextLineHeight() + block.Padding * 2, ImGui.GetCursorPosY() + block.Padding);
            ImGui.SetWindowFontScale(1); ImGui.EndChild();
        }
        request?.PageEditing?.AfterElement(block, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        if (_readerSearchTarget?.BlockId == block.Id) { ImGui.SetScrollHereY(.15f); _readerSearchTarget = null; }
    }

    private void DrawPreferenceComparisonControls(ProfileBuilderDto builder, CharacterProfileDocumentDto? mine, ProfileViewRequest? request)
    {
        if (builder.Preferences.Count > 0)
        {
            ImGui.Checkbox("Compare with mine", ref _comparePreferences);
            if (!_comparePreferences || mine == null) _comparisonFilter = 0;
            if (_comparePreferences)
            {
                using (var unavailable = ImRaii.Disabled(mine == null))
                    ImGui.Combo("Comparison filter", ref _comparisonFilter, new[] { "All interests", "Shared favourites", "Different ratings", "Unspecified for me" }, 4);
                ImGui.TextUnformatted("Colours show your preferences");
                foreach (var rating in Enum.GetValues<ProfilePreferenceRating>())
                { ImGui.TextColored(PreferenceColour(rating), rating.ToString()); ImGui.SameLine(); }
                ImGui.NewLine();
                if (mine == null)
                {
                    ImGui.TextWrapped("Published preferences unavailable. Unmatched entries remain unspecified.");
                    if (request?.RetryOwnProfile != null && ImGui.Button("Retry loading my profile")) request.RetryOwnProfile();
                }
            }
        }
    }

    private void DrawBlockContent(ProfileBlockDto block, CharacterProfileDocumentDto document, CharacterProfileDocumentDto? mine, string prefix, ProfileViewRequest? request, int depth)
    {
        switch (block.Kind)
            {
                case ProfileBlockKind.Flow:
                    for (var index = 0; index < block.Children.Count; index++)
                    { request?.PageEditing?.InsertPoint(block, index); DrawBlock(block.Children[index], document, mine, prefix, request, depth + 1); }
                    request?.PageEditing?.InsertPoint(block, block.Children.Count);
                    break;
                case ProfileBlockKind.Columns:
                    var visibleChildren = block.Children.Where(b => request?.PageEditing != null || HasContent(b, document)).ToList();
                    var columns = visibleChildren.Count;
                    if (columns > 1 && ImGui.GetContentRegionAvail().X >= columns * 180 * ImGuiHelpers.GlobalScale
                        && ImGui.BeginTable("columns", columns, ImGuiTableFlags.SizingStretchSame))
                    {
                        foreach (var child in visibleChildren) { ImGui.TableNextColumn(); DrawBlock(child, document, mine, prefix, request, depth + 1); }
                        ImGui.EndTable();
                    }
                    else foreach (var child in block.Children) DrawBlock(child, document, mine, prefix, request, depth + 1);
                    break;
                case ProfileBlockKind.Tabs:
                    if (ImGui.BeginTabBar("sections"))
                    {
                        foreach (var child in block.Children.Where(b => request?.PageEditing != null || HasContent(b, document)))
                            if (ImGui.BeginTabItem((string.IsNullOrWhiteSpace(child.Title) ? child.Kind.ToString() : child.Title) + "###" + child.Id,
                                _readerSearchTarget?.BlockId is { } target && Snowcloak.Core.Profiles.ProfileUsabilityPolicy.Walk([child]).Any(node => node.Id == target) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                            { DrawBlock(child, document, mine, prefix, request, depth + 1); ImGui.EndTabItem(); }
                        ImGui.EndTabBar();
                    }
                    break;
                case ProfileBlockKind.Heading: case ProfileBlockKind.RichText:
                    _bbCodeRenderService.Render(block.Text, ImGui.GetContentRegionAvail().X, ReaderReduceImages ? ProfileBbCodeRenderOptions.Compact : ProfileBbCodeRenderOptions.LongForm); break;
                case ProfileBlockKind.Overview: DrawBbCodeSection("", document.Overview); break;
                case ProfileBlockKind.OocNotes: DrawBbCodeSection("", document.OocNotes); break;
                case ProfileBlockKind.AdultPreferences:
                    if (document.ContentRating == Snowcloak.API.Data.Enum.ProfileContentRating.Adult) DrawBbCodeSection("", document.AdultPreferences); break;
                case ProfileBlockKind.Fields:
                    foreach (var field in document.Builder!.Fields.Where(f => block.References.Contains(f.Id) && !string.IsNullOrWhiteSpace(f.Value)))
                    { ImGui.TextWrapped(field.Group + " - " + field.Label); _bbCodeRenderService.Render(field.Value, ImGui.GetContentRegionAvail().X, ReaderReduceImages ? ProfileBbCodeRenderOptions.Compact : ProfileBbCodeRenderOptions.LongForm); }
                    break;
                case ProfileBlockKind.Image: case ProfileBlockKind.Gallery:
                    var galleryAssets = document.Builder!.Assets.Where(a => block.References.Contains(a.Hash)).ToArray();
                    var carousel = block.Kind == ProfileBlockKind.Gallery && block.GalleryLayout == ProfileGalleryLayout.Carousel;
                    var galleryKey = prefix + ":" + block.Id;
                    if (carousel && galleryAssets.Length > 0)
                    {
                        using var galleryId = ImRaii.PushId(galleryKey);
                        var selectedIndex = Array.FindIndex(galleryAssets, a => a.Hash == _galleryImageSelections.GetValueOrDefault(galleryKey));
                        if (selectedIndex < 0) selectedIndex = 0;
                        using (ImRaii.Disabled(galleryAssets.Length < 2))
                        {
                            if (ImGui.Button("<##previous-gallery")) selectedIndex = (selectedIndex + galleryAssets.Length - 1) % galleryAssets.Length;
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Previous gallery image");
                            ImGui.SameLine(); ImGui.TextUnformatted($"{selectedIndex + 1} / {galleryAssets.Length}");
                            ImGui.SameLine();
                            if (ImGui.Button(">##next-gallery")) selectedIndex = (selectedIndex + 1) % galleryAssets.Length;
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Next gallery image");
                        }
                        _galleryImageSelections[galleryKey] = galleryAssets[selectedIndex].Hash;
                        galleryAssets = [galleryAssets[selectedIndex]];
                    }
                    foreach (var asset in galleryAssets)
                    {
                        var texture = ReaderReduceImages ? null : request?.GetAssetTexture?.Invoke(asset.Hash);
                        if (texture != null)
                        {
                            var width = Math.Max(1, Math.Min(ImGui.GetContentRegionAvail().X, texture.Width));
                            if (carousel) width = Math.Min(width, 480 * ImGuiHelpers.GlobalScale * texture.Width / texture.Height);
                            ImGui.Image(texture.Handle, new Vector2(width, width * texture.Height / texture.Width));
                        }
                        else ImGui.TextDisabled(ReaderReduceImages ? "Image hidden by reader preference" : "Image loading or unavailable");
                        if (!string.IsNullOrWhiteSpace(asset.Caption)) ImGui.TextWrapped(asset.Caption);
                    }
                    break;
                case ProfileBlockKind.Hooks: DrawBbCodeHooks(document.Hooks, prefix + block.Id); break;
                case ProfileBlockKind.Preferences: DrawPreferences(document.Builder!, mine?.Builder); break;
                case ProfileBlockKind.Boundaries:
                    DrawBoundaries(document, request?.Profile.User?.UID, request?.Profile.Revision ?? 0,
                        request?.Profile.IsOwnProfile ?? true, false, prefix + block.Id, mine); break;
                case ProfileBlockKind.Divider: ImGui.Separator(); break;
                case ProfileBlockKind.Link:
                    if (block.PageTargetId != null)
                    {
                        if (ImGui.Button((string.IsNullOrWhiteSpace(block.Title) ? "Go to page" : block.Title) + "###link")) _nativeProfilePage = block.PageTargetId;
                    }
                    else if (ProfileBuilderPolicy.IsLink(block.Text) && ImGui.Button((string.IsNullOrWhiteSpace(block.Title) ? block.Text : block.Title) + "###link")) Dalamud.Utility.Util.OpenLink(block.Text);
                    break;
            }
    }

    private void DrawPreferences(ProfileBuilderDto target, ProfileBuilderDto? mine)
    {
        var rows = target.Preferences.SelectMany(entry =>
            ProfileBuilderPolicy.IsDirectional(entry)
                ? new[] { (entry, direction: ProfilePreferenceDirection.Doing, rating: entry.Doing), (entry, direction: ProfilePreferenceDirection.Receiving, rating: entry.Receiving) }
                : new[] { (entry, direction: ProfilePreferenceDirection.General, rating: entry.Rating) })
            .Where(row => row.rating != ProfilePreferenceRating.Unspecified && (!_comparePreferences || mine == null ||
                ProfileBuilderPolicy.ComparisonMatches(row.rating, ProfileBuilderPolicy.ComparisonRating(row.entry, row.direction, mine.Preferences), (ProfileComparisonFilter)_comparisonFilter)));
        var entries = rows.ToArray();
        var width = ImGui.GetContentRegionAvail().X;
        var columns = width >= 760 * ImGuiHelpers.GlobalScale ? 4 : width >= 380 * ImGuiHelpers.GlobalScale ? 2 : 1;
        using var textColour = ImRaii.PushColor(ImGuiCol.Text, new Vector4(.94f, .94f, .97f, 1));
        using var panelColour = ImRaii.PushColor(ImGuiCol.TableRowBg, new Vector4(.10f, .12f, .16f, 1));
        using var alternateColour = ImRaii.PushColor(ImGuiCol.TableRowBgAlt, new Vector4(.10f, .12f, .16f, 1));
        if (!ImGui.BeginTable("interest-ratings", columns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV)) return;
        try
        {
            foreach (var rating in new[] { ProfilePreferenceRating.Favourite, ProfilePreferenceRating.Yes, ProfilePreferenceRating.Maybe, ProfilePreferenceRating.No })
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(rating == ProfilePreferenceRating.Favourite ? "Favourites" : rating.ToString());
                var underline = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddLine(underline, underline + new Vector2(ImGui.GetContentRegionAvail().X, 0), ImGui.ColorConvertFloat4ToU32(PreferenceColour(rating)), 3 * ImGuiHelpers.GlobalScale);
                ImGui.Spacing();
                foreach (var category in entries.Where(row => row.rating == rating).GroupBy(row => row.entry.CatalogueId is { } catalogueId
                    ? ProfilePreferenceCatalogue.ById.GetValueOrDefault(catalogueId)?.Category ?? "Other" : "Custom"))
                {
                ImGui.TextColored(new Vector4(.72f, .76f, .85f, 1), category.Key);
                foreach (var row in category)
                {
                var definition = row.entry.CatalogueId == null ? null : ProfilePreferenceCatalogue.ById.GetValueOrDefault(row.entry.CatalogueId);
                var name = string.IsNullOrWhiteSpace(row.entry.Name) ? definition?.Name ?? "Retired interest" : row.entry.Name;
                var myRating = ProfileBuilderPolicy.ComparisonRating(row.entry, row.direction, mine?.Preferences ?? []);
                var myDirection = row.direction == ProfilePreferenceDirection.Doing ? "Receiving" : row.direction == ProfilePreferenceDirection.Receiving ? "Doing" : "General";
                var label = name + (row.direction == ProfilePreferenceDirection.General ? "" : " - " + row.direction);
                var comparison = $"You ({myDirection}): {myRating} - Them: {row.rating}";
                var start = ImGui.GetCursorScreenPos();
                var rowWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
                var padding = 5 * ImGuiHelpers.GlobalScale;
                var wrapWidth = Math.Max(1, rowWidth - padding * 2);
                var height = ImGui.CalcTextSize(label, false, wrapWidth).Y + padding * 2;
                if (_comparePreferences) height += ImGui.CalcTextSize(comparison, false, wrapWidth).Y + ImGui.GetStyle().ItemSpacing.Y;
                if (!string.IsNullOrWhiteSpace(row.entry.Note)) height += ImGui.CalcTextSize(row.entry.Note, false, wrapWidth).Y + ImGui.GetStyle().ItemSpacing.Y;
                var fill = _comparePreferences ? PreferenceColour(myRating) * new Vector4(.35f, .35f, .35f, 1) : new Vector4(.15f, .17f, .22f, 1);
                ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(rowWidth, height), ImGui.ColorConvertFloat4ToU32(fill));
                ImGui.SetCursorScreenPos(start + new Vector2(padding));
                using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
                ImGui.TextWrapped(label);
                var hovered = ImGui.IsItemHovered();
                if (_comparePreferences)
                {
                    ImGui.TextWrapped(comparison);
                }
                if (hovered || ImGui.IsItemHovered()) ImGui.SetTooltip(_comparePreferences
                    ? $"{label}\nThem: {row.rating}\nYou ({myDirection}): {myRating}\n{row.entry.Note}"
                    : $"{label}\nThem: {row.rating}\n{row.entry.Note}");
                if (!string.IsNullOrWhiteSpace(row.entry.Note)) ImGui.TextWrapped(row.entry.Note);
                ImGui.SetCursorScreenPos(start + new Vector2(0, height + ImGui.GetStyle().ItemSpacing.Y));
                ImGui.Dummy(new Vector2(rowWidth, 0));
                }
                ImGui.Spacing();
                }
            }
        }
        finally { ImGui.EndTable(); }
    }

    private static Vector4 ParseColour(string value)
    {
        if (value.Length != 9 || !uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgba)) return Vector4.One;
        return new((rgba >> 24) / 255f, ((rgba >> 16) & 255) / 255f, ((rgba >> 8) & 255) / 255f, (rgba & 255) / 255f);
    }

    private static bool HasContent(ProfileBlockDto block, CharacterProfileDocumentDto document) => block.Kind switch
    {
        ProfileBlockKind.Flow or ProfileBlockKind.Columns or ProfileBlockKind.Tabs => block.Children.Any(b => HasContent(b, document)),
        ProfileBlockKind.Fields => document.Builder!.Fields.Any(f => block.References.Contains(f.Id) && !string.IsNullOrWhiteSpace(f.Value)),
        ProfileBlockKind.Overview => !string.IsNullOrWhiteSpace(document.Overview),
        ProfileBlockKind.OocNotes => !string.IsNullOrWhiteSpace(document.OocNotes),
        ProfileBlockKind.AdultPreferences => document.ContentRating == Snowcloak.API.Data.Enum.ProfileContentRating.Adult && !string.IsNullOrWhiteSpace(document.AdultPreferences),
        ProfileBlockKind.Hooks => document.Hooks.Count > 0,
        ProfileBlockKind.Preferences => document.Builder!.Preferences.Any(p => p.Rating != ProfilePreferenceRating.Unspecified || p.Doing != ProfilePreferenceRating.Unspecified || p.Receiving != ProfilePreferenceRating.Unspecified),
        ProfileBlockKind.Boundaries => document.Boundaries != null && (document.Boundaries.Entries.Count > 0 || !string.IsNullOrWhiteSpace(document.Boundaries.Note)),
        ProfileBlockKind.Image or ProfileBlockKind.Gallery => document.Builder!.Assets.Any(a => block.References.Contains(a.Hash)),
        ProfileBlockKind.Heading or ProfileBlockKind.RichText => !string.IsNullOrWhiteSpace(block.Text) || !string.IsNullOrWhiteSpace(block.Title),
        _ => true
    };
}
