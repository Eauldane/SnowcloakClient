using System.Collections.ObjectModel;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;

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
    public ProfileContentRating PublicContentRating { get; set; } = ProfileContentRating.General;
    public ProfileContentRating ContentRating { get; set; } = ProfileContentRating.General;
    public string ProfileImageHash { get; set; } = string.Empty;
    public IReadOnlyList<UserProfileTagDto> Tags => _tags;

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
                Title = hook.Title,
                Description = hook.Description,
            });
        }

        OocNotes = document.OocNotes;
        AdultPreferences = document.AdultPreferences;
        PublicContentRating = document.PublicContentRating;
        ContentRating = document.ContentRating;
        ProfileImageHash = document.ProfilePictureHash ?? string.Empty;
        _tags = ProfileTagPolicy.NormalizeForStorage(document.Tags);
        Dirty = markDirty;
    }

    public void SetPublished(string ident, long revision)
    {
        LoadedIdent = ident;
        LoadedRevision = revision;
        Dirty = false;
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
                .Select(hook => new CharacterProfileHookDto(hook.Title.Trim(), hook.Description.Trim()))
                .Where(hook => !string.IsNullOrWhiteSpace(hook.Title) || !string.IsNullOrWhiteSpace(hook.Description))
                .ToList(),
            OocNotes = OocNotes.Trim(),
            AdultPreferences = AdultPreferences.Trim(),
            PublicContentRating = PublicContentRating,
            ContentRating = MaxRating(ContentRating, PublicContentRating),
            ProfilePictureHash = string.IsNullOrWhiteSpace(ProfileImageHash) ? null : ProfileImageHash,
            Tags = ProfileTagPolicy.NormalizeForStorage(_tags),
        };

    public CharacterProfileDocumentDto ToPublicPreviewDocument()
        => BuildPublicPreviewDocument(ToDocument());

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

        Hooks.Add(new ProfileEditableHook());
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
    {
        ArgumentNullException.ThrowIfNull(document);

        return document with
        {
            Overview = string.Empty,
            OocNotes = string.Empty,
            AdultPreferences = string.Empty,
            ContentRating = document.PublicContentRating,
        };
    }

    public static List<string> ParseLines(string value)
        => value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static ProfileContentRating MaxRating(ProfileContentRating left, ProfileContentRating right)
        => (ProfileContentRating)Math.Max((int)left, (int)right);

    private static void AddTextIssue(List<ProfileEditValidationIssue> issues, string field, string value, int limit)
    {
        if (value.Length > limit)
        {
            issues.Add(new ProfileEditValidationIssue(ProfileEditValidationIssueKind.TextTooLong, field, limit));
        }
    }
}
