using Snowcloak.API.Dto.Group;

namespace Snowcloak.API.Data.Comparer;


public class GroupDtoComparer : IEqualityComparer<GroupDto>
{
    public static GroupDtoComparer Instance => _instance;
    private static GroupDtoComparer _instance = new GroupDtoComparer();

    private GroupDtoComparer() { }

    public bool Equals(GroupDto? x, GroupDto? y)
    {
        if (x == null || y == null) return false;
        return x.GID.Equals(y.GID, StringComparison.Ordinal);
    }

    public int GetHashCode(GroupDto obj)
    {
        return obj.Group.GID.GetHashCode();
    }
}