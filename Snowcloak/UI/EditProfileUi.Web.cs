using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Utility;
using Dalamud.Interface.Utility;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;
using Snowcloak.Core.Profiles;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using Snowcloak.Utils;

namespace Snowcloak.UI;

public sealed partial class EditProfileUi
{
    private ProfileWebClient? _webBuilder;
    private readonly string _webPluginDirectory;
    private bool _useNativeEditor;
    private bool _webPreview;
    private ProfileAudience _webPreviewAudience = ProfileAudience.Owner;
    private bool _webImageDialogOpen;
    private string _webFingerprint = string.Empty;
    private DateTime _webNextState;
    private DateTime _webNextViewport;
    private string _webIdent = string.Empty;
    private string? _webLastGesture;
    private bool _webPublishRequested;
    private bool _webPublishConfirmed;
    private long _webEditEpoch;
    private (long Epoch, string Ident, long Revision, bool Preview, ProfileAudience Audience, bool Busy, long OwnRevision)? _webStateKey;
    private string? _webCatalogueSession;
    private (string Session, long Parent, ProfileWebBounds Bounds, bool Visible, string Regions)? _webViewportKey;

    private unsafe bool DrawWebWorkspace()
    {
        _webBuilder ??= new ProfileWebClient(_webPluginDirectory);
        var host = _webBuilder;
        if (_webIdent != _session.LoadedIdent)
        { _webIdent = _session.LoadedIdent; _webPreview = false; _webFingerprint = string.Empty; _webLastGesture = null; _webPublishRequested = false; }
        DrainWebCommands();
        if (ProfilePresentationPolicy.UsesTraditional(_session.Builder)) return true;
        var startupStatus = host.StartupStatus;
        if (host.Failure != null)
        {
            ImGui.TextWrapped(host.Failure);
            if (ImGui.Button("Retry web builder")) { host.Dispose(); _webBuilder = null; _webFingerprint = string.Empty; }
            ImGui.SameLine(); if (ImGui.Button("Use native editor")) { _useNativeEditor = true; host.Hide(); }
            return true;
        }
        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(200 * scale, ImGui.GetContentRegionAvail().X), Math.Max(180 * scale, ImGui.GetContentRegionAvail().Y - 100 * scale));
        ImGui.InvisibleButton("web-profile-editor-surface", size);
        if (ImGui.IsItemHovered()) ImGui.GetIO().WantCaptureMouse = true;
        if (host.Focused) ImGui.GetIO().WantCaptureKeyboard = true;
        if (startupStatus != null) ImGui.GetWindowDrawList().AddText(origin + new Vector2(20) * scale, 0xFFFFFFFF, startupStatus);
        if (host.Connected)
        {
            var viewport = ImGui.GetWindowViewport();
            var handle = ProfileWebClient.ResolveParentWindow((nint)viewport.PlatformHandleRaw, (nint)viewport.PlatformHandle);
            if (handle == 0) { host.Fail("The designer could not find the game window. Your draft is retained; retry the builder."); return true; }
            if (!ProfileWebClient.TryMapViewport(handle, origin, size, viewport.Pos, viewport.Size, out var bounds))
            { host.Hide(); return true; }
            var visible = !_webImageDialogOpen && !_showProfileSettings && !ImGui.IsPopupOpen("Snowcloak Profile Editor Guide");
            var regions = ProfileWebClient.CaptureOcclusion(origin, size, bounds);
            var viewportKey = (host.Session, (long)handle, bounds, visible, JsonSerializer.Serialize(regions));
            if (_webViewportKey != viewportKey || host.Hidden == visible || DateTime.UtcNow >= _webNextViewport)
            {
                _webViewportKey = viewportKey; _webNextViewport = DateTime.UtcNow.AddMilliseconds(500);
                host.SendViewport(new { type = "viewport", session = host.Session, parent = (long)handle, x = bounds.X, y = bounds.Y,
                    width = bounds.Width, height = bounds.Height, visible, regions }, visible);
            }
        }
        if (host.Connected && DateTime.UtcNow >= _webNextState) { _webNextState = DateTime.UtcNow.AddMilliseconds(250); SendWebState(); }
        ImGui.Separator(); DrawSaveControls(compact: true);
        return true;
    }

    private void SendWebState(bool force = false)
    {
        if (_webBuilder is not { Ready: true } host || _session.Builder == null) return;
        var own = _snowProfileManager.GetOwnProfile(ProfileVisibility.Private);
        var key = (_webEditEpoch, _session.LoadedIdent, _session.LoadedRevision, _webPreview, _webPreviewAudience, IsBusy, own.Revision);
        if (!force && _webStateKey == key) return;
        var document = _session.ToDocument();
        if (_webPreview) document = ProfileEditSession.BuildPreviewDocument(document, _webPreviewAudience);
        if (_webPreview && document.ContentRating != ProfileContentRating.Adult) document = document with { AdultPreferences = string.Empty };
        var comparisonAvailable = own.Revision > 0 && own.Ident == _session.LoadedIdent;
        var mine = comparisonAvailable ? own.Document.Builder?.Preferences ?? [] : [];
        var comparisons = (_webPreview ? document.Builder?.Preferences ?? [] : []).ToDictionary(p => p.Id, p => new
        {
            general = ProfileBuilderPolicy.ComparisonRating(p, ProfilePreferenceDirection.General, mine),
            doing = ProfileBuilderPolicy.ComparisonRating(p, ProfilePreferenceDirection.Doing, mine),
            receiving = ProfileBuilderPolicy.ComparisonRating(p, ProfilePreferenceDirection.Receiving, mine),
        });
        var hashes = ProfileBuilderPolicy.AssetHashes(document.Builder).Concat(new[] { document.HeaderImageHash, document.ProfilePictureHash }
            .Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!)).Distinct(StringComparer.Ordinal).ToArray();
        var state = new { type = "state", session = host.Session, ident = _session.LoadedIdent, revision = _session.LoadedRevision,
            document, imageHashes = hashes, catalogue = _webCatalogueSession == host.Session ? null : ProfilePreferenceCatalogue.Entries, comparison = comparisons,
            comparisonAvailable, preview = _webPreview, busy = key.Item6 };
        var fingerprint = JsonSerializer.Serialize(state, ProfileWebClient.JsonOptions);
        if (!force && fingerprint == _webFingerprint) return;
        _webStateKey = key; _webCatalogueSession = host.Session;
        _webFingerprint = fingerprint; host.Send(state);
    }

    private void DrainWebCommands()
    {
        if (_webBuilder == null) return;
        while (_webBuilder is { } host && host.TryRead(out var message)) ConsumeWebCommand(message);
    }

    public override void OnClose()
    {
        _webPublishRequested = false;
        DrainWebCommands();
        SaveCurrentDraftImmediately();
        _webFlushRequest = null; _pendingProfilePresentation = null;
        _webBuilder?.Hide();
        base.OnClose();
    }

    private void SaveCurrentDraftImmediately()
    {
        if (!_session.Dirty || string.IsNullOrWhiteSpace(_session.LoadedIdent)) return;
        _backupService.SaveDraft(_session.LoadedIdent,
            string.IsNullOrWhiteSpace(_session.CharacterName) ? _session.LoadedIdent : _session.CharacterName, _session.ToDocument());
    }

    private void ConsumeWebCommand(JsonElement message)
    {
        var host = _webBuilder!;
        try
        {
            var type = message.GetProperty("type").GetString();
            if (type == "ready") { SendWebState(force: true); return; }
            if (type == "focus") { host.Focused = message.GetProperty("focused").GetBoolean(); return; }
            if (type == "hostError")
            { host.Fail(ProfileWebClient.HostFailureMessage(message)); return; }
            if (message.GetProperty("ident").GetString() != _session.LoadedIdent || message.GetProperty("revision").GetInt64() != _session.LoadedRevision)
                throw new ArgumentException("The character or published revision changed. Current version saved locally.");
            if (type == "assetRequest") { _ = SendWebImageAsync(host, message.GetProperty("hash").GetString()!); return; }
            if (type is "flushCancelled" or "flushComplete")
            {
                if (_webFlushRequest?.Matches(message) != true) return;
                _webFlushRequest = null;
                if (type == "flushCancelled" || IsBusy)
                {
                    var reason = message.TryGetProperty("reason", out var explanation) ? explanation.GetString() : null;
                    CancelWebFlush(reason == "colour" ? "Finish the incomplete colour value before switching or publishing."
                        : "The designer could not finish its edits. Wait for any upload to finish, then retry", true);
                    return;
                }
                if (_pendingProfilePresentation is { } presentation) { ApplyProfilePresentation(presentation); return; }
                if (_webPublishRequested)
                {
                    _webPublishRequested = false; _webPublishConfirmed = true;
                    try { PublishDraft(); } finally { _webPublishConfirmed = false; }
                }
                return;
            }
            if (IsBusy) throw new ArgumentException("Wait for the current profile operation to finish.");
            if (type == "preview")
            {
                var audience = (ProfileAudience)message.GetProperty("audience").GetInt32();
                if (!Enum.IsDefined(audience)) throw new ArgumentException("Invalid preview audience.");
                _webPreview = message.GetProperty("enabled").GetBoolean(); _webPreviewAudience = audience;
            }
            else if (type == "refresh") { }
            else if (type == "retryComparison") Mediator.Publish(new ClearCharacterProfileDataMessage(_session.LoadedIdent));
            else if (type == "settings")
            {
                _showProfileSettings = true; host.Hide();
                _editorSection = message.TryGetProperty("section", out var section) && section.GetString() == "boundaries" ? EditorSection.Boundaries :
                    message.TryGetProperty("section", out section) && section.GetString() == "hooks" ? EditorSection.Story :
                    message.TryGetProperty("section", out section) && section.GetString() == "sections" ? EditorSection.Reusable :
                    message.TryGetProperty("section", out section) && section.GetString() == "review" ? EditorSection.Review : EditorSection.Identity;
            }
            else if (type == "saveSection")
            {
                if (_webPreview) throw new ArgumentException("Return to design before saving a section.");
                SaveReusableSection(message.GetProperty("id").GetString()!, message.GetProperty("name").GetString());
            }
            else if (type == "openLink")
            { var link = message.GetProperty("url").GetString()!; if (_webPreview && ProfileBuilderPolicy.IsLink(link)) Util.OpenLink(link); }
            else if (type == "uploadImage")
            {
                if (_webPreview) throw new ArgumentException("Return to design before uploading.");
                if (!message.GetProperty("background").GetBoolean())
                {
                    var target = message.TryGetProperty("id", out var targetId) ? ProfilePageEditPolicy.Locate(
                        ProfileBuilderPolicy.LayoutFor(_session.Builder!, message.TryGetProperty("pageId", out var uploadPage) ? uploadPage.GetString() : null), targetId.GetString()!) : null;
                    if (target == null || target.Value.Nodes[target.Value.Index].Kind is not (ProfileBlockKind.Image or ProfileBlockKind.Gallery))
                        throw new ArgumentException("Select an image or gallery before uploading.");
                }
                _builderEditor.SelectElement(message.TryGetProperty("id", out var id) ? id.GetString() : null);
                OpenBuilderImageDialog(message.GetProperty("background").GetBoolean()); host.Hide();
            }
            else if (type is "undo" or "redo")
            { if (_webPreview) throw new ArgumentException("Return to design before editing."); if (_builderEditor.TryPageHistory(_session, type == "redo")) MarkDirty(); }
            else
            {
                if (_webPreview) throw new ArgumentException("Return to design before editing.");
                var document = ProfileWebEditPolicy.Apply(_session, message);
                if (JsonSerializer.Serialize(document) == JsonSerializer.Serialize(_session.ToDocument())) return;
                var gesture = message.TryGetProperty("gesture", out var gestureId) && gestureId.ValueKind == JsonValueKind.String ? gestureId.GetString() : null;
                if (gesture == null || gesture != _webLastGesture) _builderEditor.RememberExternalEdit(_session);
                _webLastGesture = gesture;
                _session.ReplaceDocument(document, true); MarkDirty();
            }
            SendWebState(force: true);
        }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            if (_webFlushRequest != null) CancelWebFlush("The designer rejected an edit. Correct it before switching or publishing.", true);
            host.Send(new { type = "error", session = host.Session, message = e is ArgumentException ? e.Message : "Invalid profile editor operation. " });
            SendWebState(force: true);
        }
    }

    private async Task SendWebImageAsync(ProfileWebClient host, string hash)
    {
        byte[]? bytes = null;
        var document = _session.ToDocument();
        var allowed = ProfileBuilderPolicy.AssetHashes(document.Builder).Concat(new[] { document.HeaderImageHash, document.ProfilePictureHash });
        if (hash.Length == 64 && hash.All(char.IsAsciiHexDigit) && allowed.Contains(hash, StringComparer.Ordinal))
        {
            if (_imageTransferService.TryGetImage(hash, out var cached)) bytes = cached;
            else
            {
                var path = Path.Combine(_configService.ConfigurationDirectory, "rp-profile-draft-images", hash + ".png");
                try { if (File.Exists(path) && new FileInfo(path).Length <= 8 * 1024 * 1024) bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (bytes == null)
                {
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < until && !host.Lifetime.IsCancellationRequested)
                    {
                        if (_imageTransferService.TryGetImage(hash, out cached)) { bytes = cached; break; }
                        try { await Task.Delay(100, host.Lifetime).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }
            }
        }
        if (bytes?.Length > 8 * 1024 * 1024) bytes = null;
        host.Send(new { type = "asset", session = host.Session, hash, bytes });
    }
}
