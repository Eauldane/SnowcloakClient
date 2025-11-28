using Snowcloak.API.Dto.User;

namespace Snowcloak.API.Data.Comparer;

public class UserDtoComparer : IEqualityComparer<UserDto>
{
    public static UserDtoComparer Instance => _instance;
    private static UserDtoComparer _instance = new();
    private UserDtoComparer() { }
    public bool Equals(UserDto? x, UserDto? y)
    {
        if (x == null || y == null) return false;
        return x.User.UID.Equals(y.User.UID, StringComparison.Ordinal);
    }

    public int GetHashCode(UserDto obj)
    {
        return obj.User.UID.GetHashCode();
    }
}