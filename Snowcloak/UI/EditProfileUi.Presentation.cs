using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Dto.User;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI;

public sealed partial class EditProfileUi
{
    private ProfilePresentation? _pendingProfilePresentation;
    private ProfileWebFlushRequest? _webFlushRequest;

    private void BeginWebFlush()
    {
        _webFlushRequest = ProfileWebFlushRequest.Begin(_session.LoadedIdent, _session.LoadedRevision, DateTime.UtcNow);
        // The hidden designer may still hold the busy/revision state from an earlier upload or save.
        SendWebState(force: true);
        _webBuilder!.Send(new { type = "flush", session = _webBuilder.Session, requestId = _webFlushRequest.Id,
            ident = _webFlushRequest.Ident, revision = _webFlushRequest.Revision,
            commitFocused = !_showProfileSettings && !_webPreview });
    }

    private void CancelWebFlush(string message, bool error)
    {
        _webFlushRequest = null; _pendingProfilePresentation = null; _webPublishRequested = false;
        SetStatus(message, error);
    }

    private void DrawProfilePresentationChoice()
    {
        if (_webFlushRequest != null && (_webBuilder?.Failure != null || _webFlushRequest.Expired(DateTime.UtcNow)))
        {
            CancelWebFlush("The designer did not finish its edits. Your presentation and draft are retained. Retry the change or reopen the designer.", true);
        }
        var traditional = ProfilePresentationPolicy.UsesTraditional(_session.Builder);
        using (var disabled = ImRaii.Disabled(IsBusy || _webPublishRequested || _pendingProfilePresentation != null || !_apiController.SupportsProfileBuilder))
        {
        if (ImGui.RadioButton("Traditional profile", traditional) && !traditional)
            RequestProfilePresentation(ProfilePresentation.Traditional);
        ImGui.SameLine();
        if (ImGui.RadioButton("Custom profile", !traditional) && traditional)
            RequestProfilePresentation(ProfilePresentation.Custom);
        }
        if (_webFlushRequest != null && ImGui.Button("Cancel pending editor change"))
            CancelWebFlush("Pending editor change cancelled. Your presentation and draft are retained.", false);
        ImGui.TextDisabled(traditional
            ? "Traditional layout."
            : "Custom layout. Switching modes preserves data and layout.");
    }

    private void RequestProfilePresentation(ProfilePresentation presentation)
    {
        if (!ProfilePresentationPolicy.UsesTraditional(_session.Builder) &&
            _webBuilder is { Ready: true, Connected: true, Failure: null } && !_useNativeEditor)
        {
            _pendingProfilePresentation = presentation;
            BeginWebFlush();
            SetStatus("Finishing editor changes before switching presentation...");
            return;
        }
        ApplyProfilePresentation(presentation);
    }

    private void ApplyProfilePresentation(ProfilePresentation presentation)
    {
        _pendingProfilePresentation = null;
        _webFlushRequest = null;
        _webPublishRequested = false;
        _builderEditor.RememberExternalEdit(_session);
        _session.Builder = ProfilePresentationPolicy.Change(_session.ToDocument(), presentation);
        _showProfileSettings = presentation == ProfilePresentation.Traditional;
        if (_showProfileSettings)
        {
            _webBuilder?.Dispose(); _webBuilder = null;
            _webStateKey = null; _webCatalogueSession = null; _webViewportKey = null;
            _webFingerprint = string.Empty; _webLastGesture = null; _webPreview = false;
        }
        MarkDirty();
    }
}
