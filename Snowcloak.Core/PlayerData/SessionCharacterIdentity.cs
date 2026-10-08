namespace Snowcloak.Core.PlayerData;

public sealed class SessionCharacterIdentity
{
    private readonly Lock _gate = new();
    private CharacterIdentity _lastVerified;

    public CharacterIdentity Resolve(CharacterIdentity current, bool loggedIn)
    {
        lock (_gate)
        {
            if (!loggedIn)
                _lastVerified = default;
            else if (current.IsValid)
                _lastVerified = current;
            return _lastVerified;
        }
    }

    public void Clear()
    {
        lock (_gate)
            _lastVerified = default;
    }
}
