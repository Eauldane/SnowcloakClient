namespace Snowcloak.API.Data.Comparer;

public class GroupDataComparer : IEqualityComparer<GroupData>
{
    public static GroupDataComparer Instance => _instance;
    private static GroupDataComparer _instance = new GroupDataComparer();

    private GroupDataComparer() { }
    public bool Equals(GroupData? x, GroupData? y)
    {
        if (x == null || y == null) return false;
        return x.GID.Equals(y.GID, StringComparison.Ordinal);
    }

    public int GetHashCode(GroupData obj)
    {
        return obj.GID.GetHashCode();
    }
}
