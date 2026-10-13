namespace Snowcloak.Core.PlayerData;

public sealed class SessionLoginState
{
    public bool IsLoggedIn { get; private set; }

    public bool Update(bool gameLoggedIn, bool characterReady)
    {
        var next = gameLoggedIn && (IsLoggedIn || characterReady);
        if (next == IsLoggedIn)
            return false;

        IsLoggedIn = next;
        return true;
    }
}
