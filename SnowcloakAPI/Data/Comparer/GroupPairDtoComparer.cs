using Snowcloak.API.Dto.Group;

namespace Snowcloak.API.Data.Comparer;

public class GroupPairDtoComparer : IEqualityComparer<GroupPairDto>
{
    public static GroupPairDtoComparer Instance => _instance;
    private static GroupPairDtoComparer _instance = new();
    private GroupPairDtoComparer() { }
    public bool Equals(GroupPairDto? x, GroupPairDto? y)
    {
        if (x == null || y == null) return false;
        return x.GID.Equals(y.GID, StringComparison.Ordinal) && x.UID.Equals(y.UID, StringComparison.Ordinal);
    }

    public int GetHashCode(GroupPairDto obj)
    {
        return HashCode.Combine(obj.Group.GID.GetHashCode(), obj.User.UID.GetHashCode());
    }
}