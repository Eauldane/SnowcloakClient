using Snowcloak.API.Dto.CharaData;

namespace Snowcloak.Core.CharaData;

public readonly record struct GposeLocationMatch(bool SameMap, bool SameServer, bool SameEverything);

public static class GposeLocationComparison
{
    public static GposeLocationMatch Compare(LocationInfo? peer, LocationInfo? own)
    {
        return new GposeLocationMatch(
            peer?.MapId == own?.MapId,
            peer?.ServerId == own?.ServerId,
            Nullable.Equals(peer, own));
    }
}
