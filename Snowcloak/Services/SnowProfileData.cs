using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;

namespace Snowcloak.Services;

public record SnowProfileData(
    string Ident,
    UserData? User,
    ProfileVisibility Visibility,
    long Revision,
    bool Disabled,
    string DisabledReason,
    bool IsOwnProfile,
    DateTimeOffset? UpdatedAtUtc,
    CharacterProfileDocumentDto Document)
{
    public bool IsFlagged => Disabled;
    public bool IsNSFW => Document.ContentRating == ProfileContentRating.Adult;
    public string Description => Disabled ? DisabledReason : Document.Overview;
    public IReadOnlyList<UserProfileTagDto> Tags => Document.Tags;
}
