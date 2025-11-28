namespace Snowcloak.API.Data.Comparer;

public class UserDataComparer : IEqualityComparer<UserData>
{
    public static UserDataComparer Instance => _instance;
    private static UserDataComparer _instance = new();

    private UserDataComparer() { }

    public bool Equals(UserData? x, UserData? y)
    {
        if (x == null || y == null) return false;
        return x.UID.Equals(y.UID, StringComparison.Ordinal);
    }

    public int GetHashCode(UserData obj)
    {
        return obj.UID.GetHashCode();
    }
}
