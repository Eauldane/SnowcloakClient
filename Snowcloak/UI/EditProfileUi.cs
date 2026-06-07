using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class EditProfileUi : WindowMediatorSubscriberBase
{
    private const int MaxLongTextLength = 8000;
    private const int MaxShortTextLength = 160;
    private const int MaxPortraitUploadBytes = 4 * 1024 * 1024;
    private const int MaxHeaderUploadBytes = 8 * 1024 * 1024;
    private const int MaxUploadSourcePixels = 16_777_216;
    private const string DefaultHeaderAccentColorHex = "#2E94D1";
    private static readonly ThemePreset[] ThemePresets =
    [
        new("Frost", "#2E94D1"),
        new("Aurora", "#47B878"),
        new("Amethyst", "#9B6BD3"),
        new("Ember", "#D45D3D"),
        new("Rose", "#D66A9A"),
        new("Gold", "#D6A43B"),
    ];
    private readonly ApiController _apiController;
    private readonly CharacterProfileBackupService _backupService;
    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly SnowProfileManager _snowProfileManager;
    private readonly UiSharedService _uiSharedService;
    private string _adultPreferences = string.Empty;
    private string _approachability = string.Empty;
    private string _atAGlanceText = string.Empty;
    private bool _busy;
    private string _characterName = string.Empty;
    private ProfileContentRating _contentRating;
    private bool _dirty;
    private List<UserProfileTagDto> _editableTags = [];
    private Vector3 _headerAccentColor = ToVector3(CharacterProfileUiShared.ParseAccentColor(DefaultHeaderAccentColorHex));
    private string _headerImageBase64 = string.Empty;
    private IDalamudTextureWrap? _headerImageTexture;
    private List<EditableHook> _hooks = [];
    private string _loadedIdent = string.Empty;
    private long _loadedRevision;
    private DateTimeOffset _lastDraftAutosaveUtc = DateTimeOffset.MinValue;
    private string _newTagValue = string.Empty;
    private ProfileTagType _newTagType = ProfileTagType.ChatStyle;
    private string _oocNotes = string.Empty;
    private string _overview = string.Empty;
    private string _profileImageBase64 = string.Empty;
    private IDalamudTextureWrap? _profileImageTexture;
    private string _pronouns = string.Empty;
    private CharacterProfileDocumentDto? _pendingPublishedDocument;
    private ProfileContentRating _publicContentRating;
    private string _rpStatus = string.Empty;
    private string _status = string.Empty;
    private bool _statusIsError;
    private string _tagEditorError = string.Empty;
    private string _tagline = string.Empty;
    private string _title = string.Empty;
    private int _previewMode;
    private bool _tutorialPopupOpened;
    private Guid? _selectedBackupId;
    private bool _wasOpen;

    public EditProfileUi(ILogger<EditProfileUi> logger, SnowMediator mediator,
        ApiController apiController, SnowcloakConfigService configService, UiSharedService uiSharedService, FileDialogManager fileDialogManager,
        SnowProfileManager snowProfileManager, CharacterProfileBackupService backupService,
        DalamudUtilService dalamudUtilService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak RP Profile Editor###SnowcloakSyncEditProfileUI", performanceCollectorService)
    {
        _apiController = apiController;
        _configService = configService;
        _uiSharedService = uiSharedService;
        _fileDialogManager = fileDialogManager;
        _snowProfileManager = snowProfileManager;
        _backupService = backupService;
        _dalamudUtilService = dalamudUtilService;
        IsOpen = false;
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(620f, 600f),
            MaximumSize = new Vector2(860f, 2000f),
        };
        Mediator.Subscribe<GposeStartMessage>(this, _ => { _wasOpen = IsOpen; IsOpen = false; });
        Mediator.Subscribe<GposeEndMessage>(this, _ => IsOpen = _wasOpen);
        Mediator.Subscribe<DisconnectedMessage>(this, _ => { _wasOpen = false; IsOpen = false; });
        Mediator.Subscribe<ClearCharacterProfileDataMessage>(this, message =>
        {
            if (string.IsNullOrEmpty(message.Ident)
                || string.Equals(message.Ident, _loadedIdent, StringComparison.Ordinal))
            {
                _profileImageTexture?.Dispose();
                _profileImageTexture = null;
                _headerImageTexture?.Dispose();
                _headerImageTexture = null;
            }
        });
    }

    protected override void DrawInternal()
    {
        DrawFirstRunTutorial();
        DrawProfileScopeNotice();
        var profile = _snowProfileManager.GetOwnProfile(ProfileVisibility.Private);
        if (string.IsNullOrWhiteSpace(profile.Ident))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Loading your current character profile...");
            return;
        }

        if (!string.Equals(_loadedIdent, profile.Ident, StringComparison.Ordinal)
            || (!_dirty && _loadedRevision != profile.Revision))
        {
            LoadDraft(profile);
        }
        ApplyPendingPublishedDocument();

        CharacterProfileUiShared.DrawHeader(BuildDocument(), "Current character", headerImageTexture: _headerImageTexture);
        CharacterProfileUiShared.DrawProfileBadges(BuildDocument(), "rp-profile-editor-badges");
        DrawCompletenessChecklist();
        DrawContentControls();
        DrawAppearanceControls();
        DrawIdentityFields();
        DrawStoryFields();
        DrawTags();
        DrawImageControls();
        DrawLocalBackups();
        DrawProfilePreview();
        DrawSaveControls();
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
            return;

        ImGui.TextWrapped("Snowcloak profiles allow you to show pairs and potential pairs what you're about.");
        ImGui.Spacing();
        ImGui.BulletText("Frostbrand can show your public card, hooks, portrait, and profile tags while you are opted in.");
        ImGui.BulletText("Kink tags are only shown to viewers who have matching kink tags on their own profile.");
        ImGui.BulletText("Pairs can see the full profile, including overview, OOC notes, and adult preferences.");
        ImGui.BulletText("Publishing a profile does not enable Frostbrand by itself - this is a separate opt-in system.");
        ImGui.BulletText("Drafts autosave locally while you edit, and successful publishes are kept as local recovery backups for copying to other characters, or restoring after world transfer/name change.");
        ImGui.Spacing();

        if (ImGui.Button("Start editing", new Vector2(180f * ImGuiHelpers.GlobalScale, 0)))
        {
            _configService.Current.ProfileEditorTutorialSeen = true;
            _configService.Save();
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

    private void DrawCompletenessChecklist()
    {
        var document = BuildDocument();
        var checks = GetCompletenessChecks(document);
        var complete = checks.Count(check => check.Complete);
        var completion = checks.Length == 0 ? 0f : complete / (float)checks.Length;

        CharacterProfileUiShared.DrawSectionTitle("Profile Checklist");
        ImGui.ProgressBar(completion, new Vector2(-1f, 0f), $"{complete}/{checks.Length} prompts");
        ImGui.TextColored(complete >= checks.Length - 1 ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow,
            complete >= checks.Length - 1
                ? "This profile is ready to publish."
                : "These hints are only shown here while editing.");
        foreach (var check in checks)
        {
            ImGui.TextColored(check.Complete ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey, check.Complete ? "[x]" : "[ ]");
            ImGui.SameLine();
            ImGui.TextUnformatted(check.Label);
            if (!check.Complete)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"- {check.Hint}");
            }
        }
    }

    private void DrawContentControls()
    {
        CharacterProfileUiShared.DrawSectionTitle("Publishing");
        DrawRatingCombo("Public profile rating", ref _publicContentRating);
        DrawRatingCombo("Full profile rating", ref _contentRating);
        if ((int)_contentRating < (int)_publicContentRating)
        {
            _contentRating = _publicContentRating;
            MarkDirty();
        }
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Adult public views are hidden locally for viewers who disabled adult profile content.");
    }

    private void DrawIdentityFields()
    {
        CharacterProfileUiShared.DrawSectionTitle("Public Character Card");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "These fields can appear in Frostbrand while you are opted in, and are also visible to pairs.");
        DrawShortInput("Character name", ref _characterName);
        DrawShortInput("Title or epithet", ref _title);
        DrawShortInput("Pronouns", ref _pronouns);
        DrawShortInput("Tagline", ref _tagline);
        DrawShortInput("RP status", ref _rpStatus);
        DrawShortInput("Approachability", ref _approachability);
        DrawMultiline("At a glance", ref _atAGlanceText, 500, 86f);
        ImGui.TextColored(ImGuiColors.DalamudGrey, "These are rendered as bulletpoints.");
    }

    private void DrawAppearanceControls()
    {
        CharacterProfileUiShared.DrawSectionTitle("Profile Appearance");

        DrawFieldLabel("Header accent colour", "Used for the header stripe and highlighted title text.");
        if (ImGui.ColorEdit3("##rp-profile-header-accent", ref _headerAccentColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.Uint8))
            MarkDirty();
        DrawThemePresets();

        DrawFieldLabel("Header image", "Optional PNG banner. Upload up to 8 MiB; the server resizes it to fit 1600x400.");
        if (_headerImageTexture != null)
        {
            var maxWidth = ImGui.GetContentRegionAvail().X;
            var maxHeight = 96f * ImGuiHelpers.GlobalScale;
            var scale = MathF.Min(maxWidth / _headerImageTexture.Width, maxHeight / _headerImageTexture.Height);
            scale = MathF.Min(scale, 1f);
            ImGui.Image(_headerImageTexture.Handle, new Vector2(_headerImageTexture.Width * scale, _headerImageTexture.Height * scale));
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileUpload, "Choose header image"))
            OpenHeaderImageDialog();
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_headerImageBase64));
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Remove header image"))
        {
            _headerImageBase64 = string.Empty;
            ReplaceHeaderTexture([]);
            MarkDirty();
        }
        ImGui.EndDisabled();
    }

    private void DrawStoryFields()
    {
        CharacterProfileUiShared.DrawSectionTitle("RP Hooks");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Give people viewing your profile some clues!");
        DrawHooksEditor();

        CharacterProfileUiShared.DrawSectionTitle("Pair-only Details");
        DrawMultiline("Overview", ref _overview, MaxLongTextLength, 180f);
        DrawMultiline("OOC notes", ref _oocNotes, MaxLongTextLength, 100f);
        if (_contentRating == ProfileContentRating.Adult)
            DrawMultiline("Adult preferences", ref _adultPreferences, MaxLongTextLength, 100f);
    }

    private void DrawHooksEditor()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, "These show as small cards that users can peruse.");
        for (var i = 0; i < _hooks.Count; i++)
        {
            var hook = _hooks[i];
            using var id = ImRaii.PushId($"rp-hook-{i}");
            ImGui.BeginGroup();
            ImGui.TextColored(ImGuiColors.HealerGreen, $"Hook {i + 1}");
            ImGui.SameLine();
            ImGui.BeginDisabled(i == 0);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowUp, "Move hook up"))
            {
                (_hooks[i - 1], _hooks[i]) = (_hooks[i], _hooks[i - 1]);
                MarkDirty();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(i >= _hooks.Count - 1);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowDown, "Move hook down"))
            {
                (_hooks[i + 1], _hooks[i]) = (_hooks[i], _hooks[i + 1]);
                MarkDirty();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Remove hook"))
            {
                _hooks.RemoveAt(i);
                MarkDirty();
                ImGui.EndGroup();
                continue;
            }

            DrawShortInput("Title", ref hook.Title);
            DrawMultiline("Description", ref hook.Description, MaxLongTextLength, 78f);
            ImGui.EndGroup();
            ImGui.Separator();
        }

        ImGui.BeginDisabled(_hooks.Count >= 32);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add hook"))
        {
            _hooks.Add(new EditableHook());
            MarkDirty();
        }
        ImGui.EndDisabled();
        if (_hooks.Count >= 32)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Profiles can contain at most 32 hooks.");
    }

    private void DrawTags()
    {
        CharacterProfileUiShared.DrawSectionTitle("Tags");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Tags can appear in Frostbrand. Kink tags are only shown to viewers with matching kink tags. Click an existing chip to remove it.");
        DrawFieldLabel("Tag type");
        if (ImGui.BeginCombo("##rp-profile-tag-type", ProfileTagChipRenderer.GetTypeLabel(_newTagType)))
        {
            foreach (var tagType in Enum.GetValues<ProfileTagType>())
            {
                if (ImGui.Selectable(ProfileTagChipRenderer.GetTypeLabel(tagType), tagType == _newTagType))
                    _newTagType = tagType;
            }
            ImGui.EndCombo();
        }
        DrawFieldLabel("New tag");
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputTextWithHint("##rp-profile-tag-input", "Type a tag value...", ref _newTagValue,
                ProfileTagUtilities.MaxTagLength, ImGuiInputTextFlags.EnterReturnsTrue))
            AddTag();
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add tag"))
            AddTag();

        var suggestions = ProfileTagUtilities.GetDefaultSuggestions(_newTagType, _newTagValue, _editableTags, 8);
        if (suggestions.Count > 0)
        {
            DrawFieldLabel("Suggested defaults");
            if (ImGui.BeginCombo("##rp-profile-tag-suggestions", "Choose a suggestion..."))
            {
                foreach (var suggestion in suggestions)
                {
                    if (!ImGui.Selectable(suggestion)) continue;
                    _newTagValue = suggestion;
                    AddTag();
                }
                ImGui.EndCombo();
            }
        }
        if (!string.IsNullOrWhiteSpace(_tagEditorError))
            ImGui.TextColored(ImGuiColors.DalamudRed, _tagEditorError);
        var removed = ProfileTagChipRenderer.DrawTagChips(_editableTags, "rp-profile-editor-tags");
        if (removed >= 0)
        {
            _editableTags.RemoveAt(removed);
            MarkDirty();
        }
    }

    private void DrawImageControls()
    {
        CharacterProfileUiShared.DrawSectionTitle("Public Portrait");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Optional PNG portrait. Upload up to 4 MiB; the server resizes it to fit 512x512.");
        if (_profileImageTexture != null)
        {
            var max = 128f * ImGuiHelpers.GlobalScale;
            var scale = max / MathF.Max(_profileImageTexture.Width, _profileImageTexture.Height);
            ImGui.Image(_profileImageTexture.Handle, new Vector2(_profileImageTexture.Width * scale, _profileImageTexture.Height * scale));
        }
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileUpload, "Choose portrait"))
            OpenPortraitDialog();
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Remove portrait"))
        {
            _profileImageBase64 = string.Empty;
            ReplacePortraitTexture([]);
            MarkDirty();
        }
    }

    private void DrawLocalBackups()
    {
        CharacterProfileUiShared.DrawSectionTitle("Local Recovery");
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "Drafts autosave while you edit. Successful publishes are also backed up locally for rename or home-world transfer recovery.");

        var draft = _backupService.GetDraft(_loadedIdent);
        if (draft != null)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"Autosaved draft available ({draft.UpdatedAtUtc:u}).");
            if (ImGui.Button("Restore autosaved draft"))
            {
                LoadDocument(draft.Document);
                MarkDirty();
                SetStatus("Loaded the autosaved draft into the editor.");
            }
            ImGui.SameLine();
            if (ImGui.Button("Discard autosaved draft"))
            {
                _backupService.ClearDraft(_loadedIdent);
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
                var label = string.IsNullOrWhiteSpace(backup.CharacterLabel) ? backup.UpdatedAtUtc.ToString("u") : backup.CharacterLabel;
                if (ImGui.Selectable($"{label} ({backup.UpdatedAtUtc:u})", backup.Id == _selectedBackupId))
                    _selectedBackupId = backup.Id;
            }
            ImGui.EndCombo();
        }
        ImGui.BeginDisabled(!_selectedBackupId.HasValue);
        if (ImGui.Button("Restore selected backup into draft") && _selectedBackupId.HasValue)
        {
            var document = _backupService.GetDocument(_selectedBackupId.Value);
            if (document != null)
            {
                LoadDocument(document);
                MarkDirty();
                SetStatus("Loaded the local backup into the editor. Publish to attach it to this character ident.");
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawProfilePreview()
    {
        CharacterProfileUiShared.DrawSectionTitle("Preview");
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "Preview shows how the current draft is derived for Frostbrand public discovery and pair-only viewing.");

        if (ImGui.RadioButton("Frostbrand public view", _previewMode == 0))
            _previewMode = 0;
        ImGui.SameLine();
        if (ImGui.RadioButton("Pair-only full view", _previewMode == 1))
            _previewMode = 1;

        var document = _previewMode == 0 ? BuildPublicPreviewDocument(BuildDocument()) : BuildDocument();
        using var preview = ImRaii.Child("rp-profile-preview", new Vector2(0f, 360f * ImGuiHelpers.GlobalScale), true);
        if (!preview)
            return;

        CharacterProfileUiShared.DrawHeader(document, "Current character", headerImageTexture: _headerImageTexture);
        CharacterProfileUiShared.DrawProfileBadges(document, "rp-profile-preview-badges");
        ImGui.Spacing();

        using (var table = ImRaii.Table("rp-profile-preview-summary", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 110f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
                DrawPreviewLabelValue("Pronouns:", document.Pronouns);
                DrawPreviewLabelValue("RP status:", document.RpStatus);
                DrawPreviewLabelValue("Approach:", document.Approachability);
            }
        }

        DrawPreviewBullets("At A Glance", document.AtAGlance);
        DrawPreviewHooks(document.Hooks);
        if (_previewMode == 1)
        {
            DrawPreviewText("Overview", document.Overview);
            DrawPreviewText("OOC Notes", document.OocNotes);
            if (document.ContentRating == ProfileContentRating.Adult)
                DrawPreviewText("Adult Preferences", document.AdultPreferences);
        }

        DrawPreviewTags(document.Tags);
    }

    private void DrawSaveControls()
    {
        CharacterProfileUiShared.DrawSectionTitle("Publish");
        if (!string.IsNullOrWhiteSpace(_status))
            ImGui.TextColored(_statusIsError ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen, _status);
        if (_dirty)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "This draft has unpublished changes.");

        ImGui.BeginDisabled(_busy);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, _busy ? "Publishing..." : "Publish profile"))
            PublishDraft();
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete published profile"))
            DeletePublishedProfile();
        ImGui.EndDisabled();
    }

    private void LoadDraft(SnowProfileData profile)
    {
        _loadedIdent = profile.Ident;
        _loadedRevision = profile.Revision;
        LoadDocument(profile.Document);
        _dirty = false;
    }

    private void LoadDocument(CharacterProfileDocumentDto document)
    {
        _characterName = document.CharacterName;
        _title = document.Title;
        _pronouns = document.Pronouns;
        _tagline = document.Tagline;
        _rpStatus = document.RpStatus;
        _approachability = document.Approachability;
        _headerAccentColor = ToVector3(CharacterProfileUiShared.ParseAccentColor(document.HeaderAccentColorHex));
        _headerImageBase64 = document.HeaderImageBase64 ?? string.Empty;
        _atAGlanceText = string.Join(Environment.NewLine, document.AtAGlance);
        _overview = document.Overview;
        _hooks = document.Hooks.Select(hook => new EditableHook
        {
            Title = hook.Title,
            Description = hook.Description,
        }).ToList();
        _oocNotes = document.OocNotes;
        _adultPreferences = document.AdultPreferences;
        _publicContentRating = document.PublicContentRating;
        _contentRating = document.ContentRating;
        _profileImageBase64 = document.ProfilePictureBase64 ?? string.Empty;
        _editableTags = ProfileTagUtilities.NormalizeForStorage(document.Tags);
        ReplaceHeaderTexture(DecodeImage(_headerImageBase64));
        ReplacePortraitTexture(DecodeImage(_profileImageBase64));
    }

    private CharacterProfileDocumentDto BuildDocument()
    {
        return new CharacterProfileDocumentDto
        {
            CharacterName = _characterName.Trim(),
            Title = _title.Trim(),
            Pronouns = _pronouns.Trim(),
            Tagline = _tagline.Trim(),
            RpStatus = _rpStatus.Trim(),
            Approachability = _approachability.Trim(),
            HeaderAccentColorHex = CharacterProfileUiShared.ToAccentColorHex(_headerAccentColor),
            HeaderImageBase64 = string.IsNullOrWhiteSpace(_headerImageBase64) ? null : _headerImageBase64,
            AtAGlance = ParseLines(_atAGlanceText),
            Overview = _overview.Trim(),
            Hooks = _hooks
                .Select(hook => new CharacterProfileHookDto(hook.Title.Trim(), hook.Description.Trim()))
                .Where(hook => !string.IsNullOrWhiteSpace(hook.Title) || !string.IsNullOrWhiteSpace(hook.Description))
                .ToList(),
            OocNotes = _oocNotes.Trim(),
            AdultPreferences = _adultPreferences.Trim(),
            PublicContentRating = _publicContentRating,
            ContentRating = MaxRating(_contentRating, _publicContentRating),
            ProfilePictureBase64 = string.IsNullOrWhiteSpace(_profileImageBase64) ? null : _profileImageBase64,
            Tags = ProfileTagUtilities.NormalizeForStorage(_editableTags),
        };
    }

    private static CharacterProfileDocumentDto BuildPublicPreviewDocument(CharacterProfileDocumentDto document)
    {
        return document with
        {
            Overview = string.Empty,
            OocNotes = string.Empty,
            AdultPreferences = string.Empty,
            ContentRating = document.PublicContentRating,
        };
    }

    private static void DrawPreviewLabelValue(string label, string? value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }

    private static void DrawPreviewBullets(string title, IReadOnlyList<string> entries)
    {
        if (entries.Count == 0)
            return;

        CharacterProfileUiShared.DrawSectionTitle(title);
        foreach (var entry in entries)
            ImGui.BulletText(entry);
    }

    private static void DrawPreviewHooks(IReadOnlyList<CharacterProfileHookDto> hooks)
    {
        if (hooks.Count == 0)
            return;

        CharacterProfileUiShared.DrawSectionTitle("RP Hooks");
        foreach (var hook in hooks)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, string.IsNullOrWhiteSpace(hook.Title) ? "Hook" : hook.Title);
            if (!string.IsNullOrWhiteSpace(hook.Description))
                ImGui.TextWrapped(hook.Description);
            ImGui.Spacing();
        }
    }

    private static void DrawPreviewText(string title, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        CharacterProfileUiShared.DrawSectionTitle(title);
        ImGui.TextWrapped(value);
    }

    private static void DrawPreviewTags(IReadOnlyList<UserProfileTagDto> tags)
    {
        if (tags.Count == 0)
            return;

        CharacterProfileUiShared.DrawSectionTitle("Tags");
        foreach (var tag in tags)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, ProfileTagChipRenderer.GetTypeLabel(tag.Type));
            ImGui.SameLine();
            ImGui.TextUnformatted(tag.Value);
        }
    }

    private void PublishDraft()
    {
        var document = BuildDocument();
        if (document.AtAGlance.Count > 32 || document.Hooks.Count > 32)
        {
            SetStatus("Profiles can contain at most 32 at-a-glance details and 32 hooks.", true);
            return;
        }

        _busy = true;
        SetStatus("Publishing...");
        long? expectedRevision = _loadedRevision > 0 ? _loadedRevision : null;
        _ = Task.Run(async () =>
        {
            try
            {
                var saved = await _apiController.CharacterProfileSet(new CharacterProfileUpdateDto(
                    expectedRevision, document)).ConfigureAwait(false);
                var label = string.IsNullOrWhiteSpace(document.CharacterName)
                    ? await _dalamudUtilService.GetPlayerNameAsync().ConfigureAwait(false)
                    : document.CharacterName;
                _backupService.Save(saved.Ident, label, saved.Document);
                _loadedIdent = saved.Ident;
                _loadedRevision = saved.Revision;
                _dirty = false;
                _backupService.ClearDraft(saved.Ident);
                Mediator.Publish(new ClearCharacterProfileDataMessage(saved.Ident));
                var imagesOptimized = ImagesDiffer(document.ProfilePictureBase64, saved.Document.ProfilePictureBase64)
                    || ImagesDiffer(document.HeaderImageBase64, saved.Document.HeaderImageBase64);
                _pendingPublishedDocument = saved.Document;
                SetStatus(imagesOptimized
                    ? $"Published profile revision {saved.Revision}. The server optimized one or more images and the editor refreshed to the stored version."
                    : $"Published profile revision {saved.Revision}.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not publish RP profile");
                SetStatus(ex.Message, true);
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private void DeletePublishedProfile()
    {
        _busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.CharacterProfileDelete().ConfigureAwait(false);
                Mediator.Publish(new ClearCharacterProfileDataMessage(_loadedIdent));
                _backupService.ClearDraft(_loadedIdent);
                _loadedRevision = 0;
                LoadDocument(new());
                _dirty = false;
                SetStatus("Deleted the published profile.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete RP profile");
                SetStatus(ex.Message, true);
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private void OpenPortraitDialog()
    {
        _fileDialogManager.OpenFileDialog("Select RP profile portrait", ".png", (success, file) =>
        {
            if (!success) return;
            try
            {
                var bytes = File.ReadAllBytes(file);
                using var stream = new MemoryStream(bytes);
                var dimensions = PngHdr.TryExtractDimensions(stream);
                if (dimensions == PngHdr.InvalidSize || (long)dimensions.Width * dimensions.Height > MaxUploadSourcePixels)
                {
                    SetStatus($"Portrait source resolution must be a valid PNG at {FormatMegapixels(MaxUploadSourcePixels)} megapixels or lower. The server will resize it to fit 512x512.", true);
                    return;
                }
                if (bytes.Length > MaxPortraitUploadBytes)
                {
                    SetStatus("Portrait files must be 4 MiB or smaller.", true);
                    return;
                }
                _profileImageBase64 = Convert.ToBase64String(bytes);
                ReplacePortraitTexture(bytes);
                MarkDirty();
                SetStatus("Portrait added to the draft.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load RP profile portrait");
                SetStatus("Could not load that portrait.", true);
            }
        });
    }

    private void OpenHeaderImageDialog()
    {
        _fileDialogManager.OpenFileDialog("Select RP profile header image", ".png", (success, file) =>
        {
            if (!success) return;
            try
            {
                var bytes = File.ReadAllBytes(file);
                using var stream = new MemoryStream(bytes);
                var dimensions = PngHdr.TryExtractDimensions(stream);
                if (dimensions == PngHdr.InvalidSize || (long)dimensions.Width * dimensions.Height > MaxUploadSourcePixels)
                {
                    SetStatus($"Header source resolution must be a valid PNG at {FormatMegapixels(MaxUploadSourcePixels)} megapixels or lower. The server will resize it to fit 1600x400.", true);
                    return;
                }
                if (bytes.Length > MaxHeaderUploadBytes)
                {
                    SetStatus("Header image files must be 8 MiB or smaller.", true);
                    return;
                }
                _headerImageBase64 = Convert.ToBase64String(bytes);
                ReplaceHeaderTexture(bytes);
                MarkDirty();
                SetStatus("Header image added to the draft.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load RP profile header image");
                SetStatus("Could not load that header image.", true);
            }
        });
    }

    private void ReplacePortraitTexture(byte[] bytes)
    {
        _profileImageTexture?.Dispose();
        _profileImageTexture = bytes.Length == 0 ? null : _uiSharedService.LoadImage(bytes);
    }

    private void ReplaceHeaderTexture(byte[] bytes)
    {
        _headerImageTexture?.Dispose();
        _headerImageTexture = bytes.Length == 0 ? null : _uiSharedService.LoadImage(bytes);
    }

    private void ApplyPendingPublishedDocument()
    {
        if (_pendingPublishedDocument == null)
            return;

        LoadDocument(_pendingPublishedDocument);
        _pendingPublishedDocument = null;
        _dirty = false;
    }

    private void AddTag()
    {
        var value = _newTagValue.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            _tagEditorError = "Tag value cannot be empty.";
            return;
        }
        var before = _editableTags.Count;
        _editableTags = ProfileTagUtilities.NormalizeForStorage(_editableTags.Append(new UserProfileTagDto(_newTagType, value)));
        if (_editableTags.Count == before)
        {
            _tagEditorError = "That tag already exists or the profile has reached its tag limit.";
            return;
        }
        _newTagValue = string.Empty;
        _tagEditorError = string.Empty;
        MarkDirty();
    }

    private void DrawShortInput(string label, ref string value)
    {
        DrawFieldLabel(label);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText($"##{label}", ref value, MaxShortTextLength))
            MarkDirty();
    }

    private void DrawMultiline(string label, ref string value, int maxLength, float height)
    {
        DrawFieldLabel(label);
        if (ImGui.InputTextMultiline($"##{label}", ref value, maxLength, new Vector2(ImGui.GetContentRegionAvail().X, height)))
            MarkDirty();
    }

    private static void DrawFieldLabel(string label, string? helper = null)
    {
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        if (!string.IsNullOrWhiteSpace(helper))
            ImGui.TextWrapped(helper);
    }

    private void DrawThemePresets()
    {
        DrawFieldLabel("Theme presets", "Quick accent presets. Header images remain unchanged.");
        for (var i = 0; i < ThemePresets.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine();

            var preset = ThemePresets[i];
            if (ImGui.Button(preset.Name))
            {
                _headerAccentColor = ToVector3(CharacterProfileUiShared.ParseAccentColor(preset.AccentHex));
                MarkDirty();
            }
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        _status = message;
        _statusIsError = isError;
    }

    private void MarkDirty()
    {
        _dirty = true;
    }

    private void TryAutosaveDraft()
    {
        if (!_dirty || _busy || string.IsNullOrWhiteSpace(_loadedIdent))
            return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastDraftAutosaveUtc < TimeSpan.FromSeconds(5))
            return;

        var label = string.IsNullOrWhiteSpace(_characterName) ? _loadedIdent : _characterName;
        _backupService.SaveDraft(_loadedIdent, label, BuildDocument());
        _lastDraftAutosaveUtc = now;
    }

    private void DrawRatingCombo(string label, ref ProfileContentRating value)
    {
        DrawFieldLabel(label);
        if (!ImGui.BeginCombo($"##{label}", value.ToString())) return;
        foreach (var rating in Enum.GetValues<ProfileContentRating>())
        {
            if (ImGui.Selectable(rating.ToString(), rating == value))
            {
                value = rating;
                MarkDirty();
            }
        }
        ImGui.EndCombo();
    }

    private static ProfileContentRating MaxRating(ProfileContentRating left, ProfileContentRating right)
        => (ProfileContentRating)Math.Max((int)left, (int)right);

    private static ProfileChecklistItem[] GetCompletenessChecks(CharacterProfileDocumentDto document)
        =>
        [
            new("Character name", !string.IsNullOrWhiteSpace(document.CharacterName), "Give viewers a name to remember."),
            new("Pronouns", !string.IsNullOrWhiteSpace(document.Pronouns), "Helps nearby players address you cleanly."),
            new("Tagline or at-a-glance detail", !string.IsNullOrWhiteSpace(document.Tagline) || document.AtAGlance.Count > 0, "Add a fast hook for Frostbrand cards."),
            new("RP status or approachability", !string.IsNullOrWhiteSpace(document.RpStatus) || !string.IsNullOrWhiteSpace(document.Approachability), "Use this for IC/OOC and approach badges."),
            new("At least one RP hook", document.Hooks.Count > 0, "Give other roleplayers something specific to respond to."),
            new("At least one tag", document.Tags.Count > 0, "Tags help compatible players find you."),
            new("Portrait", !string.IsNullOrWhiteSpace(document.ProfilePictureBase64), "A portrait makes public cards easier to scan."),
        ];

    private static bool ImagesDiffer(string? left, string? right)
    {
        var normalizedLeft = string.IsNullOrWhiteSpace(left) ? string.Empty : left;
        var normalizedRight = string.IsNullOrWhiteSpace(right) ? string.Empty : right;
        return !string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static List<string> ParseLines(string value)
        => value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string FormatMegapixels(int pixels)
        => (pixels / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture);

    private static Vector3 ToVector3(Vector4 color) => new(color.X, color.Y, color.Z);

    private static byte[] DecodeImage(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return [];
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    protected override void Dispose(bool disposing)
    {
        _headerImageTexture?.Dispose();
        _profileImageTexture?.Dispose();
        base.Dispose(disposing);
    }

    private sealed class EditableHook
    {
        public string Title = string.Empty;
        public string Description = string.Empty;
    }

    private readonly record struct ThemePreset(string Name, string AccentHex);
    private readonly record struct ProfileChecklistItem(string Label, bool Complete, string Hint);
}
