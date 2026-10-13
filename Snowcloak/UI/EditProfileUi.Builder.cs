using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Files;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;
using Snowcloak.Core.Profiles;
using Snowcloak.UI.Components;

namespace Snowcloak.UI;

public sealed partial class EditProfileUi
{
    private readonly ProfileBuilderEditorSection _builderEditor = new();
    private Task<(ImageUploadReplyDto Reply, byte[] Bytes, bool Background)>? _builderUpload;
    private Task<List<ProfileRevisionSummaryDto>>? _historyList;
    private Task<CharacterProfileDocumentDto>? _historyDocument;
    private List<ProfileRevisionSummaryDto> _historyVersions = [];
    private string _builderOperationIdent = string.Empty;
    private string _historyIdent = string.Empty;
    private string _historyRequestIdent = string.Empty;
    private string _historyDocumentIdent = string.Empty;
    private string? _builderImageTarget;

    private void DrawBuilderAudienceSelector()
    {
        var audiences = new[] { "Public", "Paired", "Owner" };
        ImGui.Combo("Preview audience", ref _previewMode, audiences, audiences.Length);
    }

    private void DrawBuilderPreview()
    {
        var document = _session.ToDocument();
        if (document.Builder == null) return;
        var audience = _previewMode == 0 ? ProfileAudience.Public : _previewMode == 1 ? ProfileAudience.Paired : ProfileAudience.Owner;
        document = ProfileEditSession.BuildPreviewDocument(document, audience);
        var own = _snowProfileManager.GetOwnProfile(ProfileVisibility.Private);
        var preview = new Snowcloak.Services.SnowProfileData(_session.LoadedIdent, null, ProfileVisibility.Private,
            Math.Max(1, _session.LoadedRevision), false, "", true, null, document);
        _profileView.DrawStandalone(new(preview, "Current character", _headerImageTexture, _profileImageTexture,
            document.Tags, null, "builder-preview:" + _session.LoadedIdent,
            ViewerPrivateDocument: own.Revision > 0 ? own.Document : null,
            GetAssetTexture: GetBuilderPreviewTexture,
            RetryOwnProfile: () => _ = _snowProfileManager.GetOwnProfileAsync(ProfileVisibility.Private, forceRefresh: true),
            CanvasLayoutOverride: !_previewCompactLayout));
    }

    private Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? GetBuilderPreviewTexture(string hash)
    {
        if (_imageTransferService.TryGetImage(hash, out var bytes)) return _textureService.GetProfileImage(hash, bytes);
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) return null;
        var source = Path.Combine(_configService.ConfigurationDirectory, "rp-profile-draft-images", hash + ".png");
        return File.Exists(source) ? _textureService.GetProfileFile(hash, source) : null;
    }

    private void OpenBuilderImageDialog(bool background)
    {
        if (_builderUpload != null) return;
        if (!background && _session.Builder?.Assets.Count >= 32) { SetStatus("This profile already has 32 gallery images. Remove an image before adding another.", true); return; }
        _builderImageTarget = _builderEditor.SelectedElementId;
        _webImageDialogOpen = true;
        var selectionIdent = _session.LoadedIdent;
        _fileDialogManager.OpenFileDialog(background ? "Select background PNG" : "Select gallery PNG", ".png", (success, file) =>
        {
            _webImageDialogOpen = false;
            if (!success || selectionIdent != _session.LoadedIdent || _session.Builder == null || _builderUpload != null) return;
            _builderOperationIdent = _session.LoadedIdent;
            Interlocked.Increment(ref _pendingImageUploads);
            _builderUpload = UploadBuilderImage(file, background);
        });
    }

    private async Task<(ImageUploadReplyDto Reply, byte[] Bytes, bool Background)> UploadBuilderImage(string file, bool background)
    {
        var info = new FileInfo(file);
        if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException("Profile images must be at most 8 MiB.");
        var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        var validation = ProfileImageValidationPolicy.ValidateUpload(bytes, ProfileImageKind.Header);
        if (!validation.IsValid) throw new InvalidDataException("Select a valid PNG within the image size and resolution limits.");
        var reply = await _imageTransferService.UploadImageAsync(bytes, background ? ImageKind.ProfileBackground : ImageKind.ProfileGallery, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidDataException("Image upload did not complete.");
        var directory = Path.Combine(_configService.ConfigurationDirectory, "rp-profile-draft-images");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, reply.Hash + ".png"), bytes).ConfigureAwait(false);
        return (reply, bytes, background);
    }

    private void DrawServerHistory()
    {
        if (!_apiController.SupportsProfileBuilder) return;
        if (_historyIdent != _session.LoadedIdent) { _historyVersions = []; _historyIdent = _session.LoadedIdent; }
        ImGui.TextUnformatted("Published history - latest 20 versions");
        if (ImGui.Button("Refresh published history") && _historyList == null) { _historyRequestIdent = _session.LoadedIdent; _historyList = _apiController.CharacterProfileHistoryList(); }
        foreach (var revision in _historyVersions)
        {
            ImGui.PushID(revision.Revision.ToString());
            ImGui.TextUnformatted($"Revision {revision.Revision} - {revision.PublishedAtUtc:u}");
            if (ImGui.Button("Load into draft") && _historyDocument == null) { _historyDocumentIdent = _session.LoadedIdent; _historyDocument = _apiController.CharacterProfileHistoryGet(revision.Revision); }
            ImGui.SameLine();
            if (ImGui.Button("Restore and publish") && !IsBusy)
            {
                var restore = new ProfileRevisionRestoreDto(revision.Revision, _session.LoadedRevision, _session.LoadedIdent);
                _ = _publishOperation.Run(async () =>
                {
                    var saved = await _apiController.CharacterProfileHistoryRestore(restore).ConfigureAwait(false);
                    return new PublishedProfileResult(saved, saved.Document.CharacterName, false);
                });
            }
            ImGui.PopID();
        }
    }

    private void ConsumeBuilderOperations()
    {
        if (_builderUpload is { IsCompleted: true } upload)
        {
            _builderUpload = null; Interlocked.Decrement(ref _pendingImageUploads);
            if (upload.IsCompletedSuccessfully && _builderOperationIdent == _session.LoadedIdent && _session.Builder != null)
            {
                var result = upload.Result;
                _builderEditor.RememberExternalEdit(_session);
                if (result.Background) _session.Builder.Theme.BackgroundHash = result.Reply.Hash;
                else if (!_session.Builder.Assets.Any(a => a.Hash == result.Reply.Hash)) _session.Builder.Assets.Add(new() { Hash = result.Reply.Hash });
                if (!result.Background) _builderEditor.UseUploadedAsset(_session, result.Reply.Hash, _builderImageTarget);
                MarkDirty(); SetStatus($"Image added. Storage: {result.Reply.CharacterUsageBytes / 1048576f:F1} / {result.Reply.CharacterQuotaBytes / 1048576f:F1} MiB.");
            }
            else if (upload.IsFaulted) SetStatus(upload.Exception!.GetBaseException().Message, true);
        }
        if (_historyList is { IsCompleted: true } list)
        {
            _historyList = null;
            if (list.IsCompletedSuccessfully && _historyRequestIdent == _session.LoadedIdent) _historyVersions = list.Result;
            else if (list.IsFaulted) SetStatus("Could not load published history. Refresh to retry.", true);
        }
        if (_historyDocument is { IsCompleted: true } history)
        {
            _historyDocument = null;
            if (history.IsCompletedSuccessfully && _historyDocumentIdent == _session.LoadedIdent)
            { _session.ReplaceDocument(history.Result, markDirty: true); SetStatus("Revision loaded into draft. Publish when ready."); }
            else if (history.IsFaulted) SetStatus("Could not load that revision.", true);
        }
    }

    private async Task RecoverBuilderImages(CharacterProfileDocumentDto document)
    {
        if (document.Builder == null) return;
        ProfileBuilderPolicy.Validate(document.Builder);
        foreach (var hash in ProfileBuilderPolicy.AssetHashes(document.Builder).ToArray())
        {
            var path = Path.Combine(_configService.ConfigurationDirectory, "rp-profile-draft-images", hash + ".png");
            if (!File.Exists(path)) continue;
            var kind = hash == document.Builder.Theme.BackgroundHash ? ImageKind.ProfileBackground : ImageKind.ProfileGallery;
            var reply = await _imageTransferService.UploadImageAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false), kind, CancellationToken.None).ConfigureAwait(false);
            if (reply == null || reply.Hash != hash) throw new InvalidDataException("Draft image recovery failed; upload the image again before publishing.");
        }
    }
}
