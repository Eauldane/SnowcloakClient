namespace Snowcloak.Core.Pairing;

public readonly record struct PairRequestFilterCandidate(
    bool IsAvailable,
    bool IsFriend,
    short? Level,
    ushort? HomeWorldId,
    string? HomeWorldName,
    PairRequestAppearance? Appearance)
{
    public static PairRequestFilterCandidate Unavailable { get; } = new(
        false,
        false,
        null,
        null,
        null,
        null);
}
