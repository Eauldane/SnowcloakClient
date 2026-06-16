using ElezenTools.UI.Mvu;
using Snowcloak.Core.Pairing;

namespace Snowcloak.Services.Pairing;

public sealed record AvailabilityViewState(
    IReadOnlyList<AvailabilityRow> VisibleRows,
    int TotalCount,
    int AutoRejectedCount,
    bool Locked,
    bool PairingEnabled,
    bool AvailabilityChannelActive,
    bool UseProfileCards,
    bool OnlyWithProfiles,
    string SearchQuery,
    string TagQuery,
    IReadOnlyList<PendingPairRequestRow> PendingRequests) : IViewState
{
    public int VisibleCount => VisibleRows.Count;
    public int PendingRequestCount => PendingRequests.Count;

    public static AvailabilityViewState Empty { get; } =
        new([], 0, 0, Locked: false, PairingEnabled: false, AvailabilityChannelActive: false,
            UseProfileCards: false, OnlyWithProfiles: false, SearchQuery: string.Empty,
            TagQuery: string.Empty, PendingRequests: []);
}
