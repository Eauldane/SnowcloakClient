using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.UI.Components;

public sealed partial class ProfileViewComponent
{
    private ProfileWebClient? _webViewer;
    private bool _nativeViewer;
    private string _webViewFingerprint = string.Empty;
    private string _webViewId = string.Empty;
    private DateTime _webViewLastDraw;
    private DateTime _webViewNextState;
    private DateTime _webViewNextViewport;
    private long _webViewOpening;
    private (CharacterProfileDocumentDto Document, CharacterProfileDocumentDto? Own, string Ident, long Revision, long Opening, bool Acknowledged, bool Expanded, float TextScale, bool Contrast, bool Images)? _webViewStateKey;
    private string? _webViewCatalogueSession;
    private (string Session, long Parent, Snowcloak.Core.Profiles.ProfileWebBounds Bounds, string Regions)? _webViewViewportKey;

    public void DisposeWebRenderer()
    {
        _webViewer?.Dispose(); _webViewer = null; _webViewStateKey = null;
        _webViewCatalogueSession = null; _webViewViewportKey = null;
        _webViewLastDraw = DateTime.MinValue; _webViewFingerprint = string.Empty;
    }
    public void HideWebRenderer()
    {
        _webViewer?.Hide(); _webViewLastDraw = DateTime.MinValue; _webViewFingerprint = string.Empty;
    }

    private unsafe bool DrawWebProfile(ProfileViewRequest request)
    {
        if (_nativeViewer || request.GetAssetBytes == null || request.Profile.Document.Builder?.LayoutMode == ProfileLayoutMode.Canvas) return false;
        var now = DateTime.UtcNow;
        if (_webViewId != request.IdPrefix || _webViewLastDraw == DateTime.MinValue)
        { _webViewId = request.IdPrefix; _webViewOpening++; _webViewFingerprint = string.Empty; }
        _webViewLastDraw = now;
        _webViewer ??= new ProfileWebClient(_fontService.PluginDirectory); var host = _webViewer;
        while (host.TryRead(out var message))
        {
            try
            {
                var type = message.GetProperty("type").GetString();
                if (type == "ready") { SendWebViewState(request, true); continue; }
                if (type == "focus") { host.Focused = message.GetProperty("focused").GetBoolean(); continue; }
                if (type == "hostError")
                { host.Fail(ProfileWebClient.HostFailureMessage(message)); continue; }
                if (message.GetProperty("ident").GetString() != request.Profile.Ident || message.GetProperty("revision").GetInt64() != request.Profile.Revision) continue;
                if (type == "retryComparison") request.RetryOwnProfile?.Invoke();
                else if (type == "refresh") SendWebViewState(request, true);
                else if (type == "openLink")
                { var link = message.GetProperty("url").GetString()!; if (ProfileBuilderPolicy.IsLink(link)) Dalamud.Utility.Util.OpenLink(link); }
                else if (type == "assetRequest")
                {
                    var hash = message.GetProperty("hash").GetString()!;
                    if (Snowcloak.Core.Profiles.ProfileReaderLayoutPolicy.ImageHashes(request.Profile.Document).Contains(hash, StringComparer.OrdinalIgnoreCase))
                        _ = SendWebViewImageAsync(host, request.GetAssetBytes, hash);
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
        }
        var startupStatus = host.StartupStatus;
        if (host.Failure != null)
        {
            ImGui.TextWrapped(host.Failure);
            if (ImGui.Button("Use native profile viewer")) { _nativeViewer = true; host.Hide(); }
            ImGui.SameLine(); if (ImGui.Button("Retry web viewer")) { host.Dispose(); _webViewer = null; _webViewFingerprint = string.Empty; }
            return true;
        }
        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(100, ImGui.GetContentRegionAvail().X),
            Snowcloak.Core.Profiles.ProfileReaderLayoutPolicy.SurfaceHeight(ImGui.GetContentRegionAvail().Y, scale, request.Profile.Document.Boundaries != null));
        ImGui.InvisibleButton("web-profile-view-surface", size);
        if (ImGui.IsItemHovered()) ImGui.GetIO().WantCaptureMouse = true;
        if (host.Focused) ImGui.GetIO().WantCaptureKeyboard = true;
        if (startupStatus != null) ImGui.GetWindowDrawList().AddText(origin + new Vector2(12) * scale, 0xFFFFFFFF, startupStatus);
        if (host.Connected)
        {
            var viewport = ImGui.GetWindowViewport();
            var parent = ProfileWebClient.ResolveParentWindow((nint)viewport.PlatformHandleRaw, (nint)viewport.PlatformHandle);
            if (parent == 0) { host.Fail("The profile renderer could not find the game window. Retry or use the native viewer."); return true; }
            if (!ProfileWebClient.TryMapViewport(parent, origin, size, viewport.Pos, viewport.Size, out var bounds))
            { host.Hide(); return true; }
            var regions = ProfileWebClient.CaptureOcclusion(origin, size, bounds);
            var viewportKey = (host.Session, (long)parent, bounds, JsonSerializer.Serialize(regions));
            if (_webViewViewportKey != viewportKey || host.Hidden || now >= _webViewNextViewport)
            {
                _webViewViewportKey = viewportKey; _webViewNextViewport = now.AddMilliseconds(500);
                host.SendViewport(new { type = "viewport", session = host.Session, parent = (long)parent, x = bounds.X, y = bounds.Y,
                    width = bounds.Width, height = bounds.Height, visible = true, regions }, true);
            }
        }
        if (host.Connected && now >= _webViewNextState) { _webViewNextState = now.AddMilliseconds(250); SendWebViewState(request); }
        if (host.Ready && _readerSearchTarget is { } hit)
        {
            host.Send(new { type = "navigate", pageId = hit.PageId, blockId = hit.BlockId });
            _readerSearchTarget = null;
        }
        return true;
    }

    private void SendWebViewState(ProfileViewRequest request, bool force = false)
    {
        if (_webViewer is not { Ready: true }) return;
        var document = request.Profile.Document;
        var boundaryKey = request.Profile.User?.UID is { Length: > 0 } uid ? uid + ":" + request.Profile.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        var boundaries = document.Boundaries;
        var acknowledged = request.Profile.IsOwnProfile || boundaries?.RequireAcknowledgement != true ||
            boundaryKey != null && _configService.Current.RpBoundaryAcknowledgements.GetValueOrDefault(boundaryKey) >= request.Profile.Revision;
        var expanded = request.Profile.IsOwnProfile || boundaries?.RequireAcknowledgement == true || boundaryKey == null || _expandedGeneralBoundaries.Contains(boundaryKey);
        var stateKey = (request.Profile.Document, request.ViewerPrivateDocument, request.Profile.Ident, request.Profile.Revision, _webViewOpening, acknowledged, expanded,
            ReaderScale, _configService.Current.ProfileReaderHighContrast, _configService.Current.ProfileReaderReduceImages);
        if (!force && _webViewStateKey == stateKey) return;
        if (!acknowledged || !expanded) document = document with { Boundaries = null };
        if (document.ContentRating != Snowcloak.API.Data.Enum.ProfileContentRating.Adult) document = document with { AdultPreferences = string.Empty };
        var mine = request.ViewerPrivateDocument?.Builder?.Preferences ?? [];
        var comparison = document.Builder!.Preferences.ToDictionary(p => p.Id, p => new
        {
            general = ProfileBuilderPolicy.ComparisonRating(p, ProfilePreferenceDirection.General, mine),
            doing = ProfileBuilderPolicy.ComparisonRating(p, ProfilePreferenceDirection.Doing, mine),
            receiving = ProfileBuilderPolicy.ComparisonRating(p, ProfilePreferenceDirection.Receiving, mine),
        });
        var state = new { type = "state", session = _webViewer!.Session, ident = request.Profile.Ident, revision = request.Profile.Revision,
            document, imageHashes = Snowcloak.Core.Profiles.ProfileReaderLayoutPolicy.ImageHashes(document), catalogue = _webViewCatalogueSession == _webViewer.Session ? null : ProfilePreferenceCatalogue.Entries,
            comparison, comparisonAvailable = request.ViewerPrivateDocument != null, preview = true, busy = false,
            mode = "view", opening = _webViewOpening, omitHeader = false,
            reader = new { textScale = ReaderScale, highContrast = _configService.Current.ProfileReaderHighContrast, reduceImages = _configService.Current.ProfileReaderReduceImages } };
        var fingerprint = JsonSerializer.Serialize(state, ProfileWebClient.JsonOptions);
        if (!force && fingerprint == _webViewFingerprint) return;
        _webViewStateKey = stateKey; _webViewCatalogueSession = _webViewer.Session;
        _webViewFingerprint = fingerprint; _webViewer.Send(state);
    }

    private static async Task SendWebViewImageAsync(ProfileWebClient host, Func<string, byte[]?> load, string hash)
    {
        byte[]? bytes = null;
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!host.Lifetime.IsCancellationRequested && DateTime.UtcNow < until)
        {
            bytes = load(hash); if (bytes is { Length: > 0 }) break;
            try { await Task.Delay(100, host.Lifetime).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
        if (bytes?.Length > 8 * 1024 * 1024) bytes = null;
        host.Send(new { type = "asset", session = host.Session, hash, bytes });
    }
}
