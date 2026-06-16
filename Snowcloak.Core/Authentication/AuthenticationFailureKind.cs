namespace Snowcloak.Core.Authentication;

public enum AuthenticationFailureKind
{
    Unknown,
    TransientDuplicateSession,
    InvalidSecretKey,
    TemporaryBan,
    PermanentBan,
    CharacterBan,
    ClientOutdated
}
