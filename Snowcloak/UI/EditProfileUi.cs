using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.Core.Async;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.Core.Profiles;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using System.Globalization;
using System.Numerics;
using System.Threading;

namespace Snowcloak.UI;

public sealed partial class EditProfileUi : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly ApiController _apiController;
    private readonly CharacterProfileBackupService _backupService;
    private readonly BbCodeRenderService _bbCodeRenderService;
    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly ProfileDetailsEditorSection _detailsSection = new();
    private readonly ProfileEditSession _session = new();
    private readonly ProfileIdentityEditorSection _identitySection = new();
    private readonly ProfileImageEditorSection _imageSection = new();
    private readonly ProfilePublishEditorSection _publishSection = new();
    private readonly ProfileTagsEditorSection _tagsSection = new();
    private readonly ProfileViewComponent _profileView;
    private readonly NativeProfilePageCanvas _nativePageCanvas;
    private readonly SnowProfileManager _snowProfileManager;
    private readonly TextureService _textureService;
    private readonly ImageTransferService _imageTransferService;
    private readonly AsyncOp<PublishedProfileResult> _publishOperation = new();
    private readonly AsyncOp _deleteOperation = new();
    private int _pendingImageUploads;
    private int _headerUploadGeneration;
    private int _portraitUploadGeneration;
    private CharacterProfileDocumentDto? _pendingPublishedDocument;
    private string _pendingPublishedIdent = string.Empty;
    private long _pendingPublishedRevision;
    private IDalamudTextureWrap? _headerImageTexture;
    private IDalamudTextureWrap? _profileImageTexture;
    private string _headerTextureHash = string.Empty;
    private string _portraitTextureHash = string.Empty;
    private DateTimeOffset _lastDraftAutosaveUtc = DateTimeOffset.MinValue;
    private string _status = string.Empty;
    private bool _statusIsError;
    private int _previewMode;
    private bool _tutorialPopupOpened;
    private Guid? _selectedBackupId;
    private bool _wasOpen;
    private OwnRpStatusUpdatedMessage? _pendingRpStatusUpdate;

    public EditProfileUi(ILogger<EditProfileUi> logger, SnowMediator mediator,
        ApiController apiController, SnowcloakConfigService configService, TextureService textureService, FileDialogManager fileDialogManager,
        SnowProfileManager snowProfileManager, ImageTransferService imageTransferService, CharacterProfileBackupService backupService,
        DalamudUtilService dalamudUtilService, UiFontService fontService, BbCodeRenderService bbCodeRenderService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak RP Profile Editor###SnowcloakSyncEditProfileUI", performanceCollectorService)
    {
        _apiController = apiController;
        _configService = configService;
        _textureService = textureService;
        _imageTransferService = imageTransferService;
        _fileDialogManager = fileDialogManager;
        _snowProfileManager = snowProfileManager;
        _backupService = backupService;
        _bbCodeRenderService = bbCodeRenderService;
        _dalamudUtilService = dalamudUtilService;
        _profileView = new ProfileViewComponent(fontService, bbCodeRenderService, textureService, configService);
        _nativePageCanvas = new NativeProfilePageCanvas(fontService);
        _webPluginDirectory = fontService.PluginDirectory;
        IsOpen = false;
        SetScaledSizeConstraints(new Vector2(720f, 600f), new Vector2(2600f, 2000f));
        Size = new Vector2(1440f, 900f);
        SizeCondition = ImGuiCond.FirstUseEver;
        Mediator.Subscribe<GposeStartMessage>(this, _ => { _wasOpen = IsOpen; IsOpen = false; });
        Mediator.Subscribe<GposeStartMessage>(this, _ => _webBuilder?.Hide());
        Mediator.Subscribe<GposeEndMessage>(this, _ => IsOpen = _wasOpen);
        Mediator.Subscribe<DisconnectedMessage>(this, _ =>
        {
            InvalidatePendingImageSelections();
            _webBuilder?.Hide();
            _wasOpen = false;
            IsOpen = false;
        });
        Mediator.Subscribe<ClearCharacterProfileDataMessage>(this, message =>
        {
            if (string.IsNullOrEmpty(message.Ident)
                || string.Equals(message.Ident, _session.LoadedIdent, StringComparison.Ordinal))
            {
                _profileImageTexture?.Dispose();
                _profileImageTexture = null;
                _portraitTextureHash = string.Empty;
                _headerImageTexture?.Dispose();
                _headerImageTexture = null;
                _headerTextureHash = string.Empty;
            }
        });
        Mediator.Subscribe<OwnRpStatusUpdatedMessage>(this, message =>
            Interlocked.Exchange(ref _pendingRpStatusUpdate, message));
    }

    protected override void DrawInternal()
    {
        DrawFirstRunTutorial();
        DrawProfileScopeNotice();
        ConsumeOperations();
        ConsumeBuilderOperations();
        ApplyPendingRpStatusUpdate();

        var profile = _snowProfileManager.GetOwnProfile(ProfileVisibility.Private);
        if (string.IsNullOrWhiteSpace(profile.Ident))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Loading your current character profile...");
            return;
        }

        if (!string.Equals(_session.LoadedIdent, profile.Ident, StringComparison.Ordinal)
            || (!_session.Dirty && _session.LoadedRevision != profile.Revision))
        {
            LoadDraft(profile);
        }

        ApplyPendingPublishedDocument();
        EnsureImageTextures();
        DrainWebCommands();
        DrawEditorWorkspace();

        TryAutosaveDraft();
    }

    private void DrawFirstRunTutorial()
    {
        if (!_configService.Current.ProfileEditorTutorialSeen && !_tutorialPopupOpened)
        {
            ImGui.OpenPopup("Snowcloak Profile Editor Guide");
            _tutorialPopupOpened = true;
        }

        var popupOpen = true;
        if (!ImGui.BeginPopupModal("Snowcloak Profile Editor Guide", ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.Spacing();
        ImGui.BulletText("Add elements with +. Drag handles to reorder.");
        ImGui.BulletText("Select to edit. Double-click text to write. Preview supports Public, Paired and Owner views.");
        ImGui.BulletText("Frostbrand listing is public and opt-in.");
        ImGui.BulletText("Audience settings control which fields viewers can see.");
        ImGui.Spacing();

        if (ImGui.Button("Start editing", new Vector2(180f * ImGuiHelpers.GlobalScale, 0)))
        {
            _configService.Update(c => c.ProfileEditorTutorialSeen = true);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Show next time", new Vector2(180f * ImGuiHelpers.GlobalScale, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static void DrawProfileScopeNotice()
    {
        ElezenImgui.ColouredWrappedText("Profiles are character specific.", ImGuiColors.DalamudGrey);
        ImGui.Separator();
    }

    private void DrawLocalBackups()
    {
        CharacterProfileUiShared.DrawSectionTitle("Local Recovery");
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "Local drafts and published backups are available below.");

        var draft = _backupService.GetDraft(_session.LoadedIdent);
        if (draft != null)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"Autosaved draft available ({draft.UpdatedAtUtc:u}).");
            if (ImGui.Button("Restore autosaved draft"))
            {
                LoadDocument(draft.Document, markDirty: true);
                SetStatus("Loaded the autosaved draft into the editor.");
            }
            ImGui.SameLine();
            if (ImGui.Button("Discard autosaved draft"))
            {
                _backupService.ClearDraft(_session.LoadedIdent);
                SetStatus("Discarded the autosaved draft.");
            }
        }

        var backups = _backupService.GetBackups()
            .Where(backup => backup.Profile != null || backup.PrivateProfile != null || backup.PublicProfile != null)
            .ToList();
        var selected = backups.SingleOrDefault(backup => backup.Id == _selectedBackupId);
        if (ImGui.BeginCombo("Saved profile", selected?.CharacterLabel ?? "Choose a local backup..."))
        {
            foreach (var backup in backups)
            {
                var label = string.IsNullOrWhiteSpace(backup.CharacterLabel) ? backup.UpdatedAtUtc.ToString("u", CultureInfo.InvariantCulture) : backup.CharacterLabel;
                if (ImGui.Selectable($"{label} ({backup.UpdatedAtUtc:u})", backup.Id == _selectedBackupId))
                {
                    _selectedBackupId = backup.Id;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.BeginDisabled(!_selectedBackupId.HasValue);
        if (ImGui.Button("Restore selected backup into draft") && _selectedBackupId.HasValue)
        {
            var document = _backupService.GetDocument(_selectedBackupId.Value);
            if (document != null)
            {
                LoadDocument(document, markDirty: true);
                SetStatus("Loaded the local backup into the editor. Publish to attach it to this character ident.");
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawProfilePreview()
    {
        if (_session.Builder?.Presentation == ProfilePresentation.Custom) { DrawCustomPreviewLink(); return; }
        if (_session.Builder != null) { DrawBuilderAudienceSelector(); DrawBuilderPreview(); return; }
        CharacterProfileUiShared.DrawSectionTitle("Preview");
        if (ImGui.RadioButton("Frostbrand public view", _previewMode == 0))
        {
            _previewMode = 0;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Pair-only full view", _previewMode == 1))
        {
            _previewMode = 1;
        }

        var document = _previewMode == 0 ? _session.ToPublicPreviewDocument() : _session.ToPairedPreviewDocument();
        using var preview = ImRaii.Child("rp-profile-preview", new Vector2(0f, 360f * ImGuiHelpers.GlobalScale), true);
        if (preview)
        {
            _profileView.DrawEditorPreview(
                document,
                "Current character",
                _headerImageTexture,
                document.Tags,
                "rp-profile-preview",
                _previewMode == 1);
        }
    }

    private void DrawSaveControls(bool compact = false)
    {
        if (ImGui.Button("Review visibility"))
        { _showProfileSettings = true; _editorSection = EditorSection.Review; _webBuilder?.Hide(); }
        if (!compact) CharacterProfileUiShared.DrawSectionTitle("Publish");
        if (!string.IsNullOrWhiteSpace(_status))
        {
            ImGui.TextColored(_statusIsError ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen, _status);
        }

        if (_session.Dirty)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "This draft has unpublished changes.");
        }

        var busy = IsBusy;
        var publishLabel = _publishOperation.IsRunning
            ? "Publishing..."
            : HasPendingImageUploads
                ? "Waiting for image uploads..."
                : "Publish profile";
        ImGui.BeginDisabled(busy);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, publishLabel))
        {
            PublishDraft();
        }

        ImGui.SameLine();
        if (!compact && ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete published profile"))
        {
            DeletePublishedProfile();
        }
        ImGui.EndDisabled();
    }

    private void LoadDraft(SnowProfileData profile)
    {
        _webEditEpoch++;
        if (!string.IsNullOrEmpty(_session.LoadedIdent)
            && !string.Equals(_session.LoadedIdent, profile.Ident, StringComparison.Ordinal))
        {
            SaveCurrentDraftImmediately();
            InvalidatePendingImageSelections();
        }
        _session.Load(profile.Ident, profile.Revision, profile.Document);
        _pendingProfilePresentation = null;
        _webFlushRequest = null;
        _webPublishRequested = false;
        if (_session.Builder == null && _apiController.SupportsProfileBuilder)
            _session.Builder = Snowcloak.Core.Profiles.ProfilePresentationPolicy.Change(_session.ToDocument(), Snowcloak.API.Dto.User.ProfilePresentation.Traditional);
    }

    private void ApplyPendingRpStatusUpdate()
    {
        var update = Interlocked.Exchange(ref _pendingRpStatusUpdate, null);
        if (update != null && string.Equals(update.Ident, _session.LoadedIdent, StringComparison.Ordinal))
            _session.ApplyExternalRpStatus(update.Revision, update.Status);
    }

    private void LoadDocument(CharacterProfileDocumentDto document, bool markDirty)
    {
        _pendingProfilePresentation = null;
        _webFlushRequest = null;
        _webPublishRequested = false;
        _webEditEpoch++;
        InvalidatePendingImageSelections();
        _session.ReplaceDocument(document, markDirty);
        if (_session.Builder == null && _apiController.SupportsProfileBuilder)
            _session.Builder = Snowcloak.Core.Profiles.ProfilePresentationPolicy.Change(_session.ToDocument(), Snowcloak.API.Dto.User.ProfilePresentation.Traditional);
    }

    private void PublishDraft()
    {
        if (_pendingProfilePresentation != null) { SetStatus("Wait for the profile presentation change to finish.", true); return; }
        if (!_useNativeEditor && !_showProfileSettings && _webBuilder is { Ready: true, Connected: true, Failure: null } && !_webPublishConfirmed)
        {
            _webPublishRequested = true;
            BeginWebFlush();
            SetStatus("Finishing local editor changes..."); return;
        }
        if (HasPendingImageUploads)
        {
            SetStatus("Wait for the selected profile images to finish uploading before publishing.", true);
            return;
        }

        var validation = _session.Validate();
        if (!validation.IsValid)
        {
            SetStatus(BuildValidationStatus(validation), true);
            return;
        }

        var update = _session.ToUpdateDto();
        SetStatus("Publishing...");
        _ = Snowcloak.Core.Profiles.ProfileOperationTask.ObserveAsync(_publishOperation.Run(() => PublishDraftAsync(update)));
    }

    private async Task<PublishedProfileResult> PublishDraftAsync(CharacterProfileUpdateDto update)
    {
        var document = update.Document;
        try
        {
            if (document.Builder != null && !_apiController.SupportsProfileBuilder) throw new InvalidOperationException("This server does not support the profile builder.");
            if (document.Builder?.LayoutMode == Snowcloak.API.Dto.User.ProfileLayoutMode.Canvas && !_apiController.SupportsProfileCanvas)
                throw new InvalidOperationException("This server does not support freeform profiles. Update the server before publishing this draft.");
            await RecoverBuilderImages(document).ConfigureAwait(false);
            var saved = await _apiController.CharacterProfileSet(update).ConfigureAwait(false);
            var label = string.IsNullOrWhiteSpace(document.CharacterName)
                ? await _dalamudUtilService.GetPlayerNameAsync().ConfigureAwait(false)
                : document.CharacterName;
            var imagesOptimised = ImagesDiffer(document.ProfilePictureHash, saved.Document.ProfilePictureHash)
                || ImagesDiffer(document.HeaderImageHash, saved.Document.HeaderImageHash);
            return new PublishedProfileResult(saved, label, imagesOptimised);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish RP profile");
            throw;
        }
    }

    private void DeletePublishedProfile()
    {
        _ = Snowcloak.Core.Profiles.ProfileOperationTask.ObserveAsync(_deleteOperation.Run(DeletePublishedProfileAsync));
    }

    private async Task DeletePublishedProfileAsync()
    {
        try
        {
            await _apiController.CharacterProfileDelete().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete RP profile");
            throw;
        }
    }

    private void ConsumeOperations()
    {
        if (_publishOperation.IsCompleted)
        {
            ConsumePublishOperation();
        }

        if (_deleteOperation.IsCompleted)
        {
            ConsumeDeleteOperation();
        }
    }

    private void ConsumePublishOperation()
    {
        if (_publishOperation.Faulted)
        {
            SetStatus(_publishOperation.Error ?? "Could not publish RP profile.", true);
            _publishOperation.Reset();
            return;
        }

        var result = _publishOperation.Result;
        _backupService.Save(result.Saved.Ident, result.CharacterLabel, result.Saved.Document);
        _backupService.ClearDraft(result.Saved.Ident);
        Mediator.Publish(new ClearCharacterProfileDataMessage(result.Saved.Ident));
        _pendingPublishedIdent = result.Saved.Ident;
        _pendingPublishedRevision = result.Saved.Revision;
        _pendingPublishedDocument = result.Saved.Document;
        SetStatus(result.ImagesOptimised
            ? $"Published profile revision {result.Saved.Revision}. The server optimised one or more images and the editor refreshed to the stored version."
            : $"Published profile revision {result.Saved.Revision}.");
        _publishOperation.Reset();
    }

    private void ConsumeDeleteOperation()
    {
        if (_deleteOperation.Faulted)
        {
            SetStatus(_deleteOperation.Error ?? "Could not delete RP profile.", true);
            _deleteOperation.Reset();
            return;
        }

        Mediator.Publish(new ClearCharacterProfileDataMessage(_session.LoadedIdent));
        _backupService.ClearDraft(_session.LoadedIdent);
        _session.Load(_session.LoadedIdent, 0, new CharacterProfileDocumentDto());
        SetStatus("Deleted the published profile.");
        _deleteOperation.Reset();
    }

    private void OpenPortraitDialog()
    {
        _fileDialogManager.OpenFileDialog("Select RP profile portrait", ".png", (success, file) =>
        {
            if (success)
            {
                LoadImageFile(file, ProfileImageKind.Portrait);
            }
        });
    }

    private void OpenHeaderImageDialog()
    {
        _fileDialogManager.OpenFileDialog("Select RP profile header image", ".png", (success, file) =>
        {
            if (success)
            {
                LoadImageFile(file, ProfileImageKind.Header);
            }
        });
    }

    private void LoadImageFile(string file, ProfileImageKind kind)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load RP profile image");
            SetStatus(kind == ProfileImageKind.Header ? "Could not load that header image." : "Could not load that portrait.", true);
            return;
        }

        var validation = ProfileImageValidationPolicy.ValidateUpload(bytes, kind);
        if (!validation.IsValid)
        {
            SetStatus(FormatImageValidationError(kind, validation), true);
            return;
        }

        SetStatus(kind == ProfileImageKind.Header ? "Uploading header image..." : "Uploading portrait...");
        var generation = kind == ProfileImageKind.Header
            ? Interlocked.Increment(ref _headerUploadGeneration)
            : Interlocked.Increment(ref _portraitUploadGeneration);
        var ident = _session.LoadedIdent;
        Interlocked.Increment(ref _pendingImageUploads);
        _ = UploadImageAsync(bytes, kind, ident, generation);
    }

    private async Task UploadImageAsync(byte[] bytes, ProfileImageKind kind, string ident, int generation)
    {
        try
        {
            var apiKind = kind == ProfileImageKind.Header ? ImageKind.ProfileHeader : ImageKind.ProfilePortrait;
            var reply = await _imageTransferService.UploadImageAsync(bytes, apiKind, CancellationToken.None).ConfigureAwait(false);
            if (!IsCurrentImageSelection(kind, ident, generation))
            {
                return;
            }
            if (reply == null || string.IsNullOrEmpty(reply.Hash))
            {
                SetStatus(kind == ProfileImageKind.Header ? "Could not upload that header image." : "Could not upload that portrait.", true);
                return;
            }

            if (kind == ProfileImageKind.Header)
            {
                _session.HeaderImageHash = reply.Hash;
                SetStatus($"Header image added to the draft. Image storage: {FormatImageQuota(reply.CharacterUsageBytes, reply.CharacterQuotaBytes)}.");
            }
            else
            {
                _session.ProfileImageHash = reply.Hash;
                SetStatus($"Portrait added to the draft. Image storage: {FormatImageQuota(reply.CharacterUsageBytes, reply.CharacterQuotaBytes)}.");
            }

            MarkDirty();
        }
        catch (ImageUploadException ex)
        {
            if (IsCurrentImageSelection(kind, ident, generation))
            {
                SetStatus(ex.Message, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not upload RP profile image");
            if (IsCurrentImageSelection(kind, ident, generation))
            {
                SetStatus(kind == ProfileImageKind.Header ? "Could not upload that header image." : "Could not upload that portrait.", true);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingImageUploads);
        }
    }

    private void RemoveHeaderImage()
    {
        Interlocked.Increment(ref _headerUploadGeneration);
        _session.HeaderImageHash = string.Empty;
        MarkDirty();
    }

    private void RemovePortrait()
    {
        Interlocked.Increment(ref _portraitUploadGeneration);
        _session.ProfileImageHash = string.Empty;
        MarkDirty();
    }

    private void EnsureImageTextures()
    {
        EnsureImageTexture(ref _headerImageTexture, ref _headerTextureHash, _session.HeaderImageHash);
        EnsureImageTexture(ref _profileImageTexture, ref _portraitTextureHash, _session.ProfileImageHash);
    }

    private void EnsureImageTexture(ref IDalamudTextureWrap? texture, ref string trackedHash, string? desiredHash)
    {
        desiredHash ??= string.Empty;
        if (!string.Equals(trackedHash, desiredHash, StringComparison.Ordinal))
        {
            texture?.Dispose();
            texture = null;
            trackedHash = desiredHash;
        }

        if (texture == null && !string.IsNullOrEmpty(desiredHash)
            && _imageTransferService.TryGetImage(desiredHash, out var bytes) && bytes.Length > 0)
        {
            texture = _textureService.LoadImage(bytes);
        }
    }

    private void ApplyPendingPublishedDocument()
    {
        if (_pendingPublishedDocument == null)
        {
            return;
        }

        _session.Load(_pendingPublishedIdent, _pendingPublishedRevision, _pendingPublishedDocument);
        _pendingPublishedDocument = null;
        _pendingPublishedIdent = string.Empty;
        _pendingPublishedRevision = 0;
    }

    private void SetStatus(string message, bool isError = false)
    {
        _status = message;
        _statusIsError = isError;
    }

    private void MarkDirty()
    {
        _webEditEpoch++;
        _session.MarkDirty();
    }

    private void TryAutosaveDraft()
    {
        if (!_session.Dirty || IsBusy || string.IsNullOrWhiteSpace(_session.LoadedIdent))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastDraftAutosaveUtc < TimeSpan.FromSeconds(5))
        {
            return;
        }

        var label = string.IsNullOrWhiteSpace(_session.CharacterName) ? _session.LoadedIdent : _session.CharacterName;
        _backupService.SaveDraft(_session.LoadedIdent, label, _session.ToDocument());
        _lastDraftAutosaveUtc = now;
    }

    private bool HasPendingImageUploads => Volatile.Read(ref _pendingImageUploads) > 0;

    private bool IsBusy => _publishOperation.IsRunning || _deleteOperation.IsRunning || HasPendingImageUploads;

    private bool IsCurrentImageSelection(ProfileImageKind kind, string ident, int generation)
    {
        if (!string.Equals(_session.LoadedIdent, ident, StringComparison.Ordinal))
        {
            return false;
        }

        var currentGeneration = kind == ProfileImageKind.Header
            ? Volatile.Read(ref _headerUploadGeneration)
            : Volatile.Read(ref _portraitUploadGeneration);
        return generation == currentGeneration;
    }

    private void InvalidatePendingImageSelections()
    {
        Interlocked.Increment(ref _headerUploadGeneration);
        Interlocked.Increment(ref _portraitUploadGeneration);
    }

    private static bool ImagesDiffer(string? left, string? right)
    {
        var normalizedLeft = string.IsNullOrWhiteSpace(left) ? string.Empty : left;
        var normalizedRight = string.IsNullOrWhiteSpace(right) ? string.Empty : right;
        return !string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static string BuildValidationStatus(ProfileEditValidationResult validation)
    {
        if (validation.Issues.Count == 0) return "The profile contains invalid values.";
        var issue = validation.Issues[0];
        return issue.Kind switch
        {
            ProfileEditValidationIssueKind.TextTooLong => $"{issue.Field} must be {issue.Limit} characters or shorter.",
            ProfileEditValidationIssueKind.TooManyAtAGlanceEntries => $"Profiles can contain at most {issue.Limit} at-a-glance details.",
            ProfileEditValidationIssueKind.TooManyHooks => $"Profiles can contain at most {issue.Limit} hooks.",
            ProfileEditValidationIssueKind.TooManyTags => $"Profiles can contain at most {issue.Limit} tags.",
            _ => "The profile draft is not valid.",
        };
    }

    private static string FormatImageValidationError(ProfileImageKind kind, ProfileImageValidationResult validation)
    {
        var subject = kind == ProfileImageKind.Header ? "Header image" : "Portrait";
        var target = kind == ProfileImageKind.Header ? "1600x400" : "512x512";
        return validation.Failure switch
        {
            ProfileImageValidationFailure.TooLarge => $"{subject} files must be {FormatMebibytes(validation.MaxBytes)} MiB or smaller.",
            ProfileImageValidationFailure.InvalidPng or ProfileImageValidationFailure.Empty =>
                $"{subject} source resolution must be a valid PNG at {FormatMegapixels(validation.MaxPixels)} megapixels or lower. The server will resize it to fit {target}.",
            ProfileImageValidationFailure.TooManyPixels =>
                $"{subject} source resolution must be {FormatMegapixels(validation.MaxPixels)} megapixels or lower. The server will resize it to fit {target}.",
            _ => $"{subject} could not be used.",
        };
    }

    private static string FormatMegapixels(int pixels)
        => (pixels / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatMebibytes(long bytes)
        => (bytes / 1024d / 1024d).ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatImageQuota(long usageBytes, long quotaBytes)
        => quotaBytes > 0
            ? $"{FormatMebibytes(usageBytes)} of {FormatMebibytes(quotaBytes)} MiB"
            : $"{FormatMebibytes(usageBytes)} MiB used";

    protected override void Dispose(bool disposing)
    {
        _webPublishRequested = false;
        SaveCurrentDraftImmediately();
        _webBuilder?.Dispose();
        _headerImageTexture?.Dispose();
        _profileImageTexture?.Dispose();
        base.Dispose(disposing);
    }

    private readonly record struct PublishedProfileResult(
        CharacterProfileDto Saved,
        string CharacterLabel,
        bool ImagesOptimised);
}
