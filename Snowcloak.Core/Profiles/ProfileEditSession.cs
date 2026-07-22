using System.Collections.ObjectModel;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Dto.Roleplay;

namespace Snowcloak.Core.Profiles;

public enum ProfileEditValidationIssueKind
{
    TextTooLong,
    TooManyAtAGlanceEntries,
    TooManyHooks,
    TooManyTags,
}

public sealed record ProfileEditValidationIssue(ProfileEditValidationIssueKind Kind, string Field, int Limit);

public sealed record ProfileEditValidationResult(IReadOnlyList<ProfileEditValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed class ProfileEditableHook
{
    public string HookId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class ProfileEditSession
{
    public const int MaxShortTextLength = 160;
    public const int MaxLongTextLength = 8000;
    public const int MaxAtAGlanceTextLength = 500;
    public const int MaxAtAGlanceEntries = 32;
    public const int MaxHooks = 32;
    public const string DefaultHeaderAccentColorHex = "#2E94D1";

    private List<UserProfileTagDto> _tags = [];

    public string LoadedIdent { get; private set; } = string.Empty;
    public long LoadedRevision { get; private set; }
    public bool Dirty { get; private set; }
    public string CharacterName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Pronouns { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string RpStatus { get; set; } = string.Empty;
    public string Approachability { get; set; } = string.Empty;
    public string HeaderAccentColorHex { get; set; } = DefaultHeaderAccentColorHex;
    public string HeaderImageHash { get; set; } = string.Empty;
    public string AtAGlanceText { get; set; } = string.Empty;
    public string Overview { get; set; } = string.Empty;
    public Collection<ProfileEditableHook> Hooks { get; } = [];
    public string OocNotes { get; set; } = string.Empty;
    public string AdultPreferences { get; set; } = string.Empty;
    public RpBoundariesDto? Boundaries { get; set; }
    public ProfileContentRating PublicContentRating { get; set; } = ProfileContentRating.General;
    public ProfileContentRating ContentRating { get; set; } = ProfileContentRating.General;
    public string ProfileImageHash { get; set; } = string.Empty;
    public IReadOnlyList<UserProfileTagDto> Tags => _tags;
    public CharacterProfileFieldVisibilityDto FieldVisibility { get; private set; } = new();

    public long? ExpectedRevision => LoadedRevision > 0 ? LoadedRevision : null;

    public void Load(string ident, long revision, CharacterProfileDocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);

        LoadedIdent = ident;
        LoadedRevision = revision;
        ReplaceDocument(document);
        Dirty = false;
    }

    public void ReplaceDocument(CharacterProfileDocumentDto document, bool markDirty = false)
    {
        ArgumentNullException.ThrowIfNull(document);

        CharacterName = document.CharacterName;
        Title = document.Title;
        Pronouns = document.Pronouns;
        Tagline = document.Tagline;
        RpStatus = document.RpStatus;
        Approachability = document.Approachability;
        HeaderAccentColorHex = string.IsNullOrWhiteSpace(document.HeaderAccentColorHex)
            ? DefaultHeaderAccentColorHex
            : document.HeaderAccentColorHex;
        HeaderImageHash = document.HeaderImageHash ?? string.Empty;
        AtAGlanceText = string.Join(Environment.NewLine, document.AtAGlance);
        Overview = document.Overview;
        Hooks.Clear();
        foreach (var hook in document.Hooks)
        {
            Hooks.Add(new ProfileEditableHook
            {
                HookId = hook.HookId,
                Title = hook.Title,
                Description = hook.Description,
            });
        }

        OocNotes = document.OocNotes;
        AdultPreferences = document.AdultPreferences;
        Boundaries = document.Boundaries is null
            ? null
            : new RpBoundariesDto
            {
                Entries = document.Boundaries.Entries.Select(entry => new RpBoundaryEntryDto
                {
                    Key = entry.Key,
                    Rating = entry.Rating,
                }).ToList(),
                Note = document.Boundaries.Note,
                RequireAcknowledgement = document.Boundaries.RequireAcknowledgement,
            };
        PublicContentRating = document.PublicContentRating;
        ContentRating = document.ContentRating;
        ProfileImageHash = document.ProfilePictureHash ?? string.Empty;
        FieldVisibility = document.FieldVisibility is null ? new() : document.FieldVisibility with { };
        _tags = ProfileTagPolicy.NormalizeForStorage(document.Tags);
        Dirty = markDirty;
    }

    public void SetPublished(string ident, long revision)
    {
        LoadedIdent = ident;
        LoadedRevision = revision;
        Dirty = false;
    }

    public void ApplyExternalRpStatus(long revision, string status)
    {
        LoadedRevision = revision;
        RpStatus = status;
    }

    public void MarkDirty() => Dirty = true;

    public void ClearDirty() => Dirty = false;

    public CharacterProfileUpdateDto ToUpdateDto()
        => new(ExpectedRevision, ToDocument());

    public CharacterProfileDocumentDto ToDocument()
        => new()
        {
            CharacterName = CharacterName.Trim(),
            Title = Title.Trim(),
            Pronouns = Pronouns.Trim(),
            Tagline = Tagline.Trim(),
            RpStatus = RpStatus.Trim(),
            Approachability = Approachability.Trim(),
            HeaderAccentColorHex = string.IsNullOrWhiteSpace(HeaderAccentColorHex)
                ? DefaultHeaderAccentColorHex
                : HeaderAccentColorHex.Trim(),
            HeaderImageHash = string.IsNullOrWhiteSpace(HeaderImageHash) ? null : HeaderImageHash,
            AtAGlance = ParseLines(AtAGlanceText),
            Overview = Overview.Trim(),
            Hooks = Hooks
                .Select(hook => new CharacterProfileHookDto(hook.Title.Trim(), hook.Description.Trim())
                {
                    HookId = hook.HookId,
                })
                .Where(hook => !string.IsNullOrWhiteSpace(hook.Title) || !string.IsNullOrWhiteSpace(hook.Description))
                .ToList(),
            OocNotes = OocNotes.Trim(),
            AdultPreferences = AdultPreferences.Trim(),
            Boundaries = NormalizeBoundaries(Boundaries),
            PublicContentRating = PublicContentRating,
            ContentRating = MaxRating(ContentRating, PublicContentRating),
            ProfilePictureHash = string.IsNullOrWhiteSpace(ProfileImageHash) ? null : ProfileImageHash,
            Tags = ProfileTagPolicy.NormalizeForStorage(_tags),
            FieldVisibility = FieldVisibility with { },
        };

    public CharacterProfileDocumentDto ToPublicPreviewDocument()
        => BuildPreviewDocument(ToDocument(), ProfileAudience.Public);

    public CharacterProfileDocumentDto ToPairedPreviewDocument()
        => BuildPreviewDocument(ToDocument(), ProfileAudience.Paired);

    public ProfileEditValidationResult Validate()
    {
        List<ProfileEditValidationIssue> issues = [];
        AddTextIssue(issues, nameof(CharacterName), CharacterName, MaxShortTextLength);
        AddTextIssue(issues, nameof(Title), Title, MaxShortTextLength);
        AddTextIssue(issues, nameof(Pronouns), Pronouns, MaxShortTextLength);
        AddTextIssue(issues, nameof(Tagline), Tagline, MaxShortTextLength);
        AddTextIssue(issues, nameof(RpStatus), RpStatus, MaxShortTextLength);
        AddTextIssue(issues, nameof(Approachability), Approachability, MaxShortTextLength);
        AddTextIssue(issues, nameof(AtAGlanceText), AtAGlanceText, MaxAtAGlanceTextLength);
        AddTextIssue(issues, nameof(Overview), Overview, MaxLongTextLength);
        AddTextIssue(issues, nameof(OocNotes), OocNotes, MaxLongTextLength);
        AddTextIssue(issues, nameof(AdultPreferences), AdultPreferences, MaxLongTextLength);
        if (Boundaries != null)
            AddTextIssue(issues, nameof(Boundaries), Boundaries.Note, MaxLongTextLength);

        for (var i = 0; i < Hooks.Count; i++)
        {
            AddTextIssue(issues, $"Hooks[{i}].Title", Hooks[i].Title, MaxShortTextLength);
            AddTextIssue(issues, $"Hooks[{i}].Description", Hooks[i].Description, MaxLongTextLength);
        }

        var document = ToDocument();
        if (document.AtAGlance.Count > MaxAtAGlanceEntries)
        {
            issues.Add(new ProfileEditValidationIssue(
                ProfileEditValidationIssueKind.TooManyAtAGlanceEntries,
                nameof(AtAGlanceText),
                MaxAtAGlanceEntries));
        }

        if (document.Hooks.Count > MaxHooks)
        {
            issues.Add(new ProfileEditValidationIssue(
                ProfileEditValidationIssueKind.TooManyHooks,
                nameof(Hooks),
                MaxHooks));
        }

        if (document.Tags.Count > ProfileTagPolicy.MaxTagCount)
        {
            issues.Add(new ProfileEditValidationIssue(
                ProfileEditValidationIssueKind.TooManyTags,
                nameof(Tags),
                ProfileTagPolicy.MaxTagCount));
        }

        return new ProfileEditValidationResult(issues);
    }

    public bool TryAddTag(ProfileTagType type, string value)
    {
        var before = _tags.Count;
        _tags = ProfileTagPolicy.NormalizeForStorage(_tags.Append(new UserProfileTagDto(type, value)));
        if (_tags.Count == before)
        {
            return false;
        }

        Dirty = true;
        return true;
    }

    public void RemoveTagAt(int index)
    {
        if ((uint)index >= (uint)_tags.Count)
        {
            return;
        }

        _tags.RemoveAt(index);
        Dirty = true;
    }

    public void AddHook()
    {
        if (Hooks.Count >= MaxHooks)
        {
            return;
        }

        Hooks.Add(new ProfileEditableHook { HookId = Guid.NewGuid().ToString("N") });
        Dirty = true;
    }

    public void RemoveHookAt(int index)
    {
        if ((uint)index >= (uint)Hooks.Count)
        {
            return;
        }

        Hooks.RemoveAt(index);
        Dirty = true;
    }

    public void MoveHook(int index, int direction)
    {
        var target = index + direction;
        if ((uint)index >= (uint)Hooks.Count || (uint)target >= (uint)Hooks.Count)
        {
            return;
        }

        (Hooks[target], Hooks[index]) = (Hooks[index], Hooks[target]);
        Dirty = true;
    }

    public static CharacterProfileDocumentDto BuildPublicPreviewDocument(CharacterProfileDocumentDto document)
        => BuildPreviewDocument(document, ProfileAudience.Public);

    public static CharacterProfileDocumentDto BuildPreviewDocument(CharacterProfileDocumentDto document, ProfileAudience audience)
    {
        ArgumentNullException.ThrowIfNull(document);

        var fields = document.FieldVisibility ?? new();
        return document with
        {
            CharacterName = fields.CharacterName <= audience ? document.CharacterName : string.Empty,
            Title = fields.Title <= audience ? document.Title : string.Empty,
            Pronouns = fields.Pronouns <= audience ? document.Pronouns : string.Empty,
            Tagline = fields.Tagline <= audience ? document.Tagline : string.Empty,
            RpStatus = fields.RpStatus <= audience ? document.RpStatus : string.Empty,
            Approachability = fields.Approachability <= audience ? document.Approachability : string.Empty,
            HeaderImageHash = fields.Header <= audience ? document.HeaderImageHash : null,
            ProfilePictureHash = fields.Portrait <= audience ? document.ProfilePictureHash : null,
            AtAGlance = fields.AtAGlance <= audience ? document.AtAGlance : [],
            Overview = fields.Overview <= audience ? document.Overview : string.Empty,
            Hooks = fields.Hooks <= audience ? document.Hooks : [],
            OocNotes = fields.OocNotes <= audience ? document.OocNotes : string.Empty,
            AdultPreferences = fields.AdultPreferences <= audience ? document.AdultPreferences : string.Empty,
            Boundaries = fields.Boundaries <= audience ? document.Boundaries : null,
            Tags = fields.Tags <= audience ? document.Tags : [],
            ContentRating = audience == ProfileAudience.Public ? document.PublicContentRating : document.ContentRating,
        };
    }

    public static List<string> ParseLines(string value)
        => value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static ProfileContentRating MaxRating(ProfileContentRating left, ProfileContentRating right)
        => (ProfileContentRating)Math.Max((int)left, (int)right);

    private static RpBoundariesDto? NormalizeBoundaries(RpBoundariesDto? boundaries)
    {
        if (boundaries == null)
            return null;

        var entries = boundaries.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .GroupBy(entry => entry.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new RpBoundaryEntryDto
            {
                Key = group.Key,
                Rating = group.Last().Rating,
            })
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var note = boundaries.Note.Trim();
        return entries.Count == 0 && string.IsNullOrEmpty(note)
            ? null
            : new RpBoundariesDto
            {
                Entries = entries,
                Note = note,
                RequireAcknowledgement = boundaries.RequireAcknowledgement,
            };
    }

    private static void AddTextIssue(List<ProfileEditValidationIssue> issues, string field, string value, int limit)
    {
        if (value.Length > limit)
        {
            issues.Add(new ProfileEditValidationIssue(ProfileEditValidationIssueKind.TextTooLong, field, limit));
        }
    }
}
