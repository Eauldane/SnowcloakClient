namespace Snowcloak.Core.Pairing;

public readonly record struct PairRequestFilterSettings(
    bool PairingEnabled,
    bool FriendsOnly,
    int MinimumLevel,
    IReadOnlySet<ushort> RejectedHomeworlds,
    IReadOnlySet<PairRequestAppearanceFilter> RejectedAppearances)
{
    public bool HasAnyFilter =>
        FriendsOnly
        || Math.Max(0, MinimumLevel) > 0
        || RejectedHomeworlds.Count > 0
        || RejectedAppearances.Count > 0;

    public bool HasAppearanceFilters => RejectedAppearances.Count > 0;
}
