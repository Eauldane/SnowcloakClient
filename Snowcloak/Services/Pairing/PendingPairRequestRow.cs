using Snowcloak.Services;

namespace Snowcloak.Services.Pairing;

public sealed record PendingPairRequestRow(
    Guid RequestId,
    DateTimeOffset RequestedAt,
    string DisplayName,
    string AliasOrUid,
    bool ShowAlias,
    PairRequesterCharacterSnapshot? CharacterSnapshot,
    string MetadataText);
