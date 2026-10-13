using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Profiles;
using Snowcloak.Core.Profiles;
using Snowcloak.UI.Components;
using System.Numerics;
using Microsoft.Extensions.Logging;

namespace Snowcloak.UI;

public sealed partial class EditProfileUi
{
    private enum EditorSection { Layout, Identity, Fields, Story, Interests, Media, Tags, Boundaries, Visibility, Review, History, Reusable }
    private EditorSection _editorSection = EditorSection.Identity;
    private bool _compactPreview;
    private bool _previewCompactLayout;
    private bool _showProfileSettings;
    private string? _webStartupFailure;
    private static readonly string[] EditorSections =
    ["Back to designer", "Character card", "Character fields", "Story and hooks", "Interests", "Images and theme",
        "Discovery tags", "Scene boundaries", "Visibility", "Publish review", "History and recovery", "Reusable sections"];

    private void DrawEditorWorkspace()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.TextUnformatted("Profile editor");
        ImGui.TextDisabled($"Revision {_session.LoadedRevision} - Local draft");
        DrawProfilePresentationChoice();
        if (ProfilePresentationPolicy.UsesTraditional(_session.Builder))
        {
            _showProfileSettings = true;
            if (_editorSection == EditorSection.Layout) _editorSection = EditorSection.Identity;
            _webBuilder?.Hide();
            DrawProfileSettingsWorkspace();
            return;
        }
        if (ImGui.Button("Design page")) _showProfileSettings = false;
        ImGui.SameLine();
        if (ImGui.Button("Character data and publishing")) _showProfileSettings = true;
        if (_showProfileSettings) { _webBuilder?.Hide(); DrawProfileSettingsWorkspace(); return; }
        if (!_apiController.SupportsProfileBuilder)
        { ImGui.TextWrapped("Connect to a server with profile builder support."); return; }
        _session.Builder ??= ProfilePresentationPolicy.Change(_session.ToDocument(), Snowcloak.API.Dto.User.ProfilePresentation.Traditional);
        if (_useNativeEditor && ImGui.Button("Use web builder")) { _useNativeEditor = false; return; }
        if (_session.Builder.LayoutMode == Snowcloak.API.Dto.User.ProfileLayoutMode.Canvas)
        {
            ImGui.TextWrapped("Positioned layout. Responsive preview uses reading order.");
            if (ImGui.Button("Convert to responsive page"))
            { _builderEditor.RememberExternalEdit(_session); _session.Builder.LayoutMode = Snowcloak.API.Dto.User.ProfileLayoutMode.Traditional; MarkDirty(); }
        }
        if (!_useNativeEditor)
        {
            if (_session.Builder.LayoutMode == Snowcloak.API.Dto.User.ProfileLayoutMode.Canvas)
            { _webBuilder?.Hide(); ImGui.TextWrapped("Convert to responsive flow to use Design page."); if (ImGui.Button("Keep positioned layout in native editor")) _useNativeEditor = true; DrawSaveControls(compact: true); return; }
            if (_webStartupFailure != null)
            {
                ImGui.TextWrapped(_webStartupFailure);
                if (ImGui.Button("Retry designer startup")) _webStartupFailure = null;
                ImGui.SameLine();
                if (ImGui.Button("Use native editor")) _useNativeEditor = true;
            }
            else
            {
                try { DrawWebWorkspace(); }
                catch (Exception error)
                {
                    // don't include profile content or configuration in startup diagnostics
                    _webStartupFailure = $"The designer could not start ({error.GetType().Name}).";
                    _logger.LogError("Profile designer startup failed: {FailureType}", error.GetType().Name);
                    _webBuilder?.Dispose(); _webBuilder = null; _webFingerprint = string.Empty;
                }
            }
            return;
        }
        var height = Math.Max(180 * scale, ImGui.GetContentRegionAvail().Y - 135 * scale);
        using (var designer = ImRaii.Child("profile-visual-designer", new Vector2(0, height), false))
            if (designer) _builderEditor.DrawPage(_session, MarkDirty, OpenBuilderImageDialog, DrawDesignPage, DrawBuilderAudienceSelector,
                kind =>
                {
                    if (kind == Snowcloak.API.Dto.User.ProfileBlockKind.Hooks) ProfileDetailsEditorSection.DrawHooksEditor(_session, MarkDirty);
                    else ProfileBoundariesEditorSection.Draw(_session, MarkDirty);
                });
        ImGui.Separator(); DrawSaveControls(compact: true);
    }

    private void DrawDesignPage(ProfilePageEditingContext? editing)
    {
        var document = _session.ToDocument();
        if (editing != null) document.Builder = _session.Builder;
        else document = Snowcloak.Core.Profiles.ProfileEditSession.BuildPreviewDocument(document,
            _previewMode == 0 ? Snowcloak.API.Data.Enum.ProfileAudience.Public : _previewMode == 1
                ? Snowcloak.API.Data.Enum.ProfileAudience.Paired : Snowcloak.API.Data.Enum.ProfileAudience.Owner);
        if (editing != null)
        {
            _builderEditor.DrawNativeSurface(_nativePageCanvas, _session, document, GetBuilderPreviewTexture);
            return;
        }
        var own = _snowProfileManager.GetOwnProfile(Snowcloak.API.Data.Enum.ProfileVisibility.Private);
        var profile = new Snowcloak.Services.SnowProfileData(_session.LoadedIdent, null,
            Snowcloak.API.Data.Enum.ProfileVisibility.Private, Math.Max(1, _session.LoadedRevision), false, "", true, null, document);
        _profileView.DrawEditablePage(new(profile, "Current character", _headerImageTexture, _profileImageTexture,
            document.Tags, null, "visual-designer:" + _session.LoadedIdent,
            ViewerPrivateDocument: own.Revision > 0 ? own.Document : null,
            GetAssetTexture: GetBuilderPreviewTexture, CanvasLayoutOverride: editing != null ? false : null,
            PageEditing: editing));
    }

    private void DrawProfileSettingsWorkspace()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var wide = ImGui.GetContentRegionAvail().X >= 1180f * scale;
        var name = string.IsNullOrWhiteSpace(_session.CharacterName) ? "Current character" : _session.CharacterName;
        ImGui.TextUnformatted(name + " - Profile studio");
        ImGui.TextDisabled($"Revision {_session.LoadedRevision} - Local draft");
        if (!wide)
        {
            if (ImGui.RadioButton("Edit", !_compactPreview)) _compactPreview = false;
            ImGui.SameLine();
            if (ImGui.RadioButton("Preview", _compactPreview)) _compactPreview = true;
        }
        ImGui.Separator();
        // Reserve a fixed footer; each workspace pane scrolls independently.
        var height = Math.Max(160f * scale, ImGui.GetContentRegionAvail().Y - 135f * scale);
        using (var workspace = ImRaii.Table("profile-studio-workspace", wide ? 3 : 2,
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            if (workspace)
            {
                ImGui.TableSetupColumn("Sections", ImGuiTableColumnFlags.WidthFixed, 185f * scale);
                ImGui.TableSetupColumn("Editor", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                if (wide) ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableNextColumn();
                using (var navigation = ImRaii.Child("studio-navigation", new Vector2(0, height), true))
                {
                    if (navigation)
                    {
                        for (var index = ProfilePresentationPolicy.UsesTraditional(_session.Builder) ? 1 : 0; index < EditorSections.Length; index++)
                            if (ImGui.Selectable(EditorSections[index], (int)_editorSection == index))
                            { _editorSection = (EditorSection)index; _compactPreview = false; if (index == 0) _showProfileSettings = false; }
                        ImGui.Separator();

                    }
                }
                ImGui.TableNextColumn();
                if (!wide && _compactPreview) DrawStudioPreview(height);
                else
                {
                    using var editor = ImRaii.Child("studio-editor", new Vector2(0, height), true);
                    if (editor)
                    {
                        CharacterProfileUiShared.DrawSectionTitle(EditorSections[(int)_editorSection]);
                        DrawStudioSection();
                    }
                }
                if (wide) { ImGui.TableNextColumn(); DrawStudioPreview(height); }
            }
        }
        ImGui.Separator();
        DrawSaveControls(compact: true);
    }

    private void DrawStudioPreview(float height)
    {
        using var preview = ImRaii.Child("studio-preview", new Vector2(0, height), true);
        if (!preview) return;
        CharacterProfileUiShared.DrawSectionTitle("Live preview");
        if (_session.Builder?.Presentation == Snowcloak.API.Dto.User.ProfilePresentation.Custom)
        {
            DrawCustomPreviewLink();
            return;
        }
        DrawBuilderAudienceSelector();
        if (_session.Builder?.LayoutMode == Snowcloak.API.Dto.User.ProfileLayoutMode.Canvas)
            ImGui.Checkbox("Preview compact reading order", ref _previewCompactLayout);
        if (_session.Builder != null) DrawBuilderPreview();
        else DrawProfilePreview();
    }

    private void DrawCustomPreviewLink()
    {
        DrawBuilderAudienceSelector();

        if (ImGui.Button("Open custom profile preview"))
        {
            _showProfileSettings = false;
            _webIdent = _session.LoadedIdent;
            _webPreview = true;
            _webPreviewAudience = _previewMode == 0 ? Snowcloak.API.Data.Enum.ProfileAudience.Public
                : _previewMode == 1 ? Snowcloak.API.Data.Enum.ProfileAudience.Paired : Snowcloak.API.Data.Enum.ProfileAudience.Owner;
            _webFingerprint = string.Empty;
        }
    }

    private void DrawStudioSection()
    {
        switch (_editorSection)
        {
            case EditorSection.Layout: _showProfileSettings = false; break;
            case EditorSection.Fields: DrawStudioBuilder(ProfileBuilderEditorPage.Fields); break;
            case EditorSection.Interests: DrawStudioBuilder(ProfileBuilderEditorPage.Preferences); break;
            case EditorSection.Media:
                _imageSection.Draw(_session, _headerImageTexture, _profileImageTexture, OpenHeaderImageDialog,
                    RemoveHeaderImage, OpenPortraitDialog, RemovePortrait, MarkDirty);
                if (!ProfilePresentationPolicy.UsesTraditional(_session.Builder)) DrawStudioBuilder(ProfileBuilderEditorPage.Theme);
                break;
            case EditorSection.Identity: _identitySection.Draw(_session, MarkDirty); break;
            case EditorSection.Story: _detailsSection.Draw(_session, MarkDirty); break;
            case EditorSection.Tags: _tagsSection.Draw(_session, MarkDirty); break;
            case EditorSection.Boundaries: ProfileBoundariesEditorSection.Draw(_session, MarkDirty); break;
            case EditorSection.Visibility: ProfileVisibilityEditorSection.Draw(_session, MarkDirty); break;
            case EditorSection.Review:
                _publishSection.DrawContentControls(_session, MarkDirty);
                _publishSection.DrawChecklist(_session);
                DrawVisibilityInventory();
                DrawSaveControls();
                break;
            case EditorSection.History: DrawLocalBackups(); ImGui.Separator(); DrawServerHistory(); break;
            case EditorSection.Reusable: DrawReusableSections(); break;
        }
    }

    private void DrawStudioBuilder(ProfileBuilderEditorPage page)
    {
        if (!_apiController.SupportsProfileBuilder)
        { ImGui.TextWrapped("Connect to a server with profile builder support."); return; }
        _session.Builder ??= ProfilePresentationPolicy.Change(_session.ToDocument(), Snowcloak.API.Dto.User.ProfilePresentation.Traditional);
        if (!_apiController.SupportsProfileCanvas)
            ImGui.TextWrapped("Server update required to publish custom layouts.");
        _builderEditor.Draw(_session, MarkDirty, OpenBuilderImageDialog, page);
    }
}
