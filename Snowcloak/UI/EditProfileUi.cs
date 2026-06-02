using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class EditProfileUi : WindowMediatorSubscriberBase
{
    private const int MaxLongTextLength = 8000;
    private const int MaxShortTextLength = 160;
    private readonly ApiController _apiController;
    private readonly CharacterProfileBackupService _backupService;
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
    private ProfileVisibility _editingVisibility = ProfileVisibility.Public;
    private string _hooksText = string.Empty;
    private string _loadedIdent = string.Empty;
    private long _loadedRevision;
    private ProfileVisibility _loadedVisibility;
    private string _newTagValue = string.Empty;
    private ProfileTagType _newTagType = ProfileTagType.ChatStyle;
    private string _oocNotes = string.Empty;
    private string _overview = string.Empty;
    private string _profileImageBase64 = string.Empty;
    private IDalamudTextureWrap? _profileImageTexture;
    private string _pronouns = string.Empty;
    private string _rpStatus = string.Empty;
    private string _status = string.Empty;
    private bool _statusIsError;
    private string _tagEditorError = string.Empty;
    private string _tagline = string.Empty;
    private string _title = string.Empty;
    private string _availability = string.Empty;
    private Guid? _selectedBackupId;
    private bool _wasOpen;

    public EditProfileUi(ILogger<EditProfileUi> logger, SnowMediator mediator,
        ApiController apiController, UiSharedService uiSharedService, FileDialogManager fileDialogManager,
        SnowProfileManager snowProfileManager, CharacterProfileBackupService backupService,
        DalamudUtilService dalamudUtilService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak RP Profile Editor###SnowcloakSyncEditProfileUI", performanceCollectorService)
    {
        _apiController = apiController;
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
            }
        });
    }

    protected override void DrawInternal()
    {
        DrawVariantSelector();
        var profile = _snowProfileManager.GetOwnProfile(_editingVisibility);
        if (string.IsNullOrWhiteSpace(profile.Ident))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Loading your current character profile...");
            return;
        }

        if (!string.Equals(_loadedIdent, profile.Ident, StringComparison.Ordinal)
            || _loadedVisibility != _editingVisibility
            || (!_dirty && _loadedRevision != profile.Revision))
        {
            LoadDraft(profile);
        }

        CharacterProfileUiShared.DrawHeader(BuildDocument(), "Current character");
        DrawContentControls();
        DrawIdentityFields();
        DrawStoryFields();
        DrawTags();
        DrawImageControls();
        DrawLocalBackups();
        DrawSaveControls();
    }

    private void DrawVariantSelector()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Publish a character-specific RP profile. Public profiles appear in Frostbrand while you are opted in; private profiles are for active pairs.");
        if (ImGui.RadioButton("Public", _editingVisibility == ProfileVisibility.Public))
            SwitchVisibility(ProfileVisibility.Public);
        ImGui.SameLine();
        if (ImGui.RadioButton("Private", _editingVisibility == ProfileVisibility.Private))
            SwitchVisibility(ProfileVisibility.Private);
        ImGui.SameLine();
        if (ImGui.Button("Copy other variant into draft"))
        {
            var other = _editingVisibility == ProfileVisibility.Public ? ProfileVisibility.Private : ProfileVisibility.Public;
            var otherProfile = _snowProfileManager.GetOwnProfile(other);
            LoadDocument(otherProfile.Document);
            _dirty = true;
            SetStatus($"Copied the {other.ToString().ToLowerInvariant()} variant into this draft.");
        }
        ImGui.Separator();
    }

    private void DrawContentControls()
    {
        CharacterProfileUiShared.DrawSectionTitle("Publishing");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Frostbrand opt-in controls nearby visibility and pair requests. Publishing a profile does not opt you into Frostbrand.");
        if (ImGui.BeginCombo("Content rating", _contentRating.ToString()))
        {
            foreach (var rating in Enum.GetValues<ProfileContentRating>())
            {
                if (ImGui.Selectable(rating.ToString(), rating == _contentRating))
                {
                    _contentRating = rating;
                    _dirty = true;
                }
            }
            ImGui.EndCombo();
        }
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Adult profiles are hidden locally for viewers who disabled adult profile content.");
    }

    private void DrawIdentityFields()
    {
        CharacterProfileUiShared.DrawSectionTitle("Character Card");
        DrawShortInput("Character name", ref _characterName);
        DrawShortInput("Title or epithet", ref _title);
        DrawShortInput("Pronouns", ref _pronouns);
        DrawShortInput("Tagline", ref _tagline);
        DrawShortInput("RP status", ref _rpStatus);
        DrawShortInput("Approachability", ref _approachability);
        DrawMultiline("At a glance", ref _atAGlanceText, 500, 86f);
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Use one short at-a-glance detail per line.");
    }

    private void DrawStoryFields()
    {
        CharacterProfileUiShared.DrawSectionTitle("RP Details");
        DrawMultiline("Overview", ref _overview, MaxLongTextLength, 180f);
        DrawMultiline("Hooks", ref _hooksText, MaxLongTextLength, 120f);
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Use one hook per line in the form: Hook title | Description");
        DrawMultiline("Availability", ref _availability, MaxLongTextLength, 80f);
        DrawMultiline("OOC notes", ref _oocNotes, MaxLongTextLength, 100f);
        if (_contentRating == ProfileContentRating.Adult)
            DrawMultiline("Adult preferences", ref _adultPreferences, MaxLongTextLength, 100f);
    }

    private void DrawTags()
    {
        CharacterProfileUiShared.DrawSectionTitle("Tags");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Tags help nearby players scan for compatible RP styles. Click an existing chip to remove it.");
        if (ImGui.BeginCombo("Tag type", ProfileTagChipRenderer.GetTypeLabel(_newTagType)))
        {
            foreach (var tagType in Enum.GetValues<ProfileTagType>())
            {
                if (ImGui.Selectable(ProfileTagChipRenderer.GetTypeLabel(tagType), tagType == _newTagType))
                    _newTagType = tagType;
            }
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputTextWithHint("##rp-profile-tag-input", "Type a tag value...", ref _newTagValue,
                ProfileTagUtilities.MaxTagLength, ImGuiInputTextFlags.EnterReturnsTrue))
            AddTag();
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add tag"))
            AddTag();

        var suggestions = ProfileTagUtilities.GetDefaultSuggestions(_newTagType, _newTagValue, _editableTags, 8);
        if (suggestions.Count > 0 && ImGui.BeginCombo("Suggested defaults", "Choose a suggestion..."))
        {
            foreach (var suggestion in suggestions)
            {
                if (!ImGui.Selectable(suggestion)) continue;
                _newTagValue = suggestion;
                AddTag();
            }
            ImGui.EndCombo();
        }
        if (!string.IsNullOrWhiteSpace(_tagEditorError))
            ImGui.TextColored(ImGuiColors.DalamudRed, _tagEditorError);
        var removed = ProfileTagChipRenderer.DrawTagChips(_editableTags, "rp-profile-editor-tags");
        if (removed >= 0)
        {
            _editableTags.RemoveAt(removed);
            _dirty = true;
        }
    }

    private void DrawImageControls()
    {
        CharacterProfileUiShared.DrawSectionTitle("Portrait");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Optional PNG portrait. Maximum dimensions: 256x256. Maximum file size: 250 KiB.");
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
            _dirty = true;
        }
    }

    private void DrawLocalBackups()
    {
        CharacterProfileUiShared.DrawSectionTitle("Local Recovery");
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "Successful publishes are backed up locally. Restore a draft to reapply it after a rename or home-world transfer.");
        var backups = _backupService.GetBackups()
            .Where(backup => _editingVisibility == ProfileVisibility.Public
                ? backup.PublicProfile != null
                : backup.PrivateProfile != null)
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
            var document = _backupService.GetDocument(_selectedBackupId.Value, _editingVisibility);
            if (document != null)
            {
                LoadDocument(document);
                _dirty = true;
                SetStatus("Loaded the local backup into the editor. Publish to attach it to this character ident.");
            }
        }
        ImGui.EndDisabled();
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
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete published variant"))
            DeletePublishedVariant();
        ImGui.EndDisabled();
    }

    private void LoadDraft(SnowProfileData profile)
    {
        _loadedIdent = profile.Ident;
        _loadedVisibility = _editingVisibility;
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
        _atAGlanceText = string.Join(Environment.NewLine, document.AtAGlance);
        _overview = document.Overview;
        _hooksText = string.Join(Environment.NewLine, document.Hooks.Select(hook => $"{hook.Title} | {hook.Description}"));
        _oocNotes = document.OocNotes;
        _availability = document.Availability;
        _adultPreferences = document.AdultPreferences;
        _contentRating = document.ContentRating;
        _profileImageBase64 = document.ProfilePictureBase64 ?? string.Empty;
        _editableTags = ProfileTagUtilities.NormalizeForStorage(document.Tags);
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
            AtAGlance = ParseLines(_atAGlanceText),
            Overview = _overview.Trim(),
            Hooks = ParseHooks(_hooksText),
            OocNotes = _oocNotes.Trim(),
            Availability = _availability.Trim(),
            AdultPreferences = _adultPreferences.Trim(),
            ContentRating = _contentRating,
            ProfilePictureBase64 = string.IsNullOrWhiteSpace(_profileImageBase64) ? null : _profileImageBase64,
            Tags = ProfileTagUtilities.NormalizeForStorage(_editableTags),
        };
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
        var visibility = _editingVisibility;
        long? expectedRevision = _loadedRevision > 0 ? _loadedRevision : null;
        _ = Task.Run(async () =>
        {
            try
            {
                var saved = await _apiController.CharacterProfileSet(new CharacterProfileUpdateDto(
                    visibility, expectedRevision, document)).ConfigureAwait(false);
                var label = string.IsNullOrWhiteSpace(document.CharacterName)
                    ? await _dalamudUtilService.GetPlayerNameAsync().ConfigureAwait(false)
                    : document.CharacterName;
                _backupService.Save(saved.Ident, label, saved.Visibility, saved.Document);
                _loadedIdent = saved.Ident;
                _loadedRevision = saved.Revision;
                _loadedVisibility = saved.Visibility;
                _dirty = false;
                Mediator.Publish(new ClearCharacterProfileDataMessage(saved.Ident, saved.Visibility));
                SetStatus($"Published {saved.Visibility.ToString().ToLowerInvariant()} profile revision {saved.Revision}.");
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

    private void DeletePublishedVariant()
    {
        _busy = true;
        var visibility = _editingVisibility;
        _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.CharacterProfileDelete(visibility).ConfigureAwait(false);
                Mediator.Publish(new ClearCharacterProfileDataMessage(_loadedIdent, visibility));
                _loadedRevision = 0;
                LoadDocument(new());
                _dirty = false;
                SetStatus($"Deleted the published {visibility.ToString().ToLowerInvariant()} variant.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete RP profile variant");
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
                if (dimensions == PngHdr.InvalidSize || dimensions.Width > 256 || dimensions.Height > 256)
                {
                    SetStatus("Portraits must be valid PNG files no larger than 256x256.", true);
                    return;
                }
                if (bytes.Length > 250 * 1024)
                {
                    SetStatus("Portrait files must be 250 KiB or smaller.", true);
                    return;
                }
                _profileImageBase64 = Convert.ToBase64String(bytes);
                ReplacePortraitTexture(bytes);
                _dirty = true;
                SetStatus("Portrait added to the draft.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load RP profile portrait");
                SetStatus("Could not load that portrait.", true);
            }
        });
    }

    private void ReplacePortraitTexture(byte[] bytes)
    {
        _profileImageTexture?.Dispose();
        _profileImageTexture = bytes.Length == 0 ? null : _uiSharedService.LoadImage(bytes);
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
        _dirty = true;
    }

    private void SwitchVisibility(ProfileVisibility visibility)
    {
        if (_editingVisibility == visibility) return;
        _editingVisibility = visibility;
        _loadedIdent = string.Empty;
        _loadedRevision = 0;
        _dirty = false;
        _status = string.Empty;
    }

    private void DrawShortInput(string label, ref string value)
    {
        if (ImGui.InputText(label, ref value, MaxShortTextLength))
            _dirty = true;
    }

    private void DrawMultiline(string label, ref string value, int maxLength, float height)
    {
        if (ImGui.InputTextMultiline(label, ref value, maxLength, new Vector2(ImGui.GetContentRegionAvail().X, height)))
            _dirty = true;
    }

    private void SetStatus(string message, bool isError = false)
    {
        _status = message;
        _statusIsError = isError;
    }

    private static List<string> ParseLines(string value)
        => value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<CharacterProfileHookDto> ParseHooks(string value)
    {
        return ParseLines(value)
            .Select(line => line.Split('|', 2, StringSplitOptions.TrimEntries))
            .Select(parts => new CharacterProfileHookDto(parts[0], parts.Length > 1 ? parts[1] : string.Empty))
            .ToList();
    }

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
        _profileImageTexture?.Dispose();
        base.Dispose(disposing);
    }
}
