using System.Numerics;
using Snowcloak.API.Dto.User;

namespace Snowcloak.Core.Pairing;

public sealed record AvailabilityRow(
    string Ident,
    string DisplayName,
    string CharacterName,
    string Status,
    string GenderText,
    string TribeName,
    string RaceName,
    string ClassName,
    Vector4 ClassColor,
    short Level,
    string LevelText,
    ushort? HomeWorldId,
    string HomeWorldName,
    CharacterProfileSummaryDto? Profile,
    IReadOnlyList<UserProfileTagDto> VisibleTags);
