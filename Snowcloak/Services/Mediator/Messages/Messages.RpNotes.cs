namespace Snowcloak.Services.Mediator;

public record OpenRpNotesMessage(string TargetUid, string DisplayName, string? CharacterIdent = null,
    string? CharacterName = null, string? World = null) : SameThreadMessage;
