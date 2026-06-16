using Dalamud.Game.ClientState.Objects.Types;
using Snowcloak.Interop.Ipc;

namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
public record PenumbraModSettingChangedMessage : MessageBase;
public record PenumbraInitializedMessage : MessageBase;
public record PenumbraDisposedMessage : MessageBase;

public record PenumbraRedrawMessage(IntPtr Address, int ObjTblIdx, bool WasRequested) : SameThreadMessage;

public record GlamourerChangedMessage(IntPtr Address) : MessageBase;
public record HeelsOffsetMessage : MessageBase;

public record PenumbraResourceLoadMessage(IntPtr GameObject, string GamePath, string FilePath) : SameThreadMessage;

public record CustomizePlusMessage(nint? Address) : MessageBase;
public record HonorificMessage(string NewHonorificTitle) : MessageBase;
public record PetNamesReadyMessage : MessageBase;
public record PetNamesMessage(string PetNicknamesData) : MessageBase;
public record MoodlesMessage(IntPtr Address) : MessageBase;
public record HonorificReadyMessage : MessageBase;
public record IpcStatusChangedMessage(IpcStatus Status) : MessageBase;
public record OptionalIpcAvailabilityChangedMessage(string IpcName, bool IsAvailable) : MessageBase;
public record PenumbraStartRedrawMessage(IntPtr Address) : MessageBase;
public record PenumbraEndRedrawMessage(IntPtr Address) : MessageBase;
public record PenumbraDirectoryChangedMessage(string? ModDirectory) : MessageBase;

public record PenumbraRedrawCharacterMessage(ICharacter Character) : SameThreadMessage;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
