namespace Snowcloak.Core.Pairing;

public readonly record struct PairRequestFilterResult(bool ShouldReject, string Reason, bool WasDeferred)
{
    public static PairRequestFilterResult Accept { get; } = new(false, string.Empty, false);

    public static PairRequestFilterResult Defer { get; } = new(false, string.Empty, true);

    public static PairRequestFilterResult Reject(string reason) => new(true, reason, false);
}
