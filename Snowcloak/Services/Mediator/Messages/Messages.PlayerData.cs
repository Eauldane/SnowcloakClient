using Dalamud.Game.ClientState.Objects.SubKinds;
using Snowcloak.API.Data;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;

namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
public record ClassJobChangedMessage(GameObjectHandler GameObjectHandler) : MessageBase;
public record PlayerChangedMessage(CharacterData Data) : MessageBase;
public record CharacterChangedMessage(GameObjectHandler GameObjectHandler) : MessageBase;
public record TransientResourceChangedMessage(IntPtr Address) : MessageBase;
public record HaltScanMessage(string Source) : MessageBase;
public record ResumeScanMessage(string Source) : MessageBase;
public record CreateCacheForObjectMessage(GameObjectHandler ObjectToCreateFor) : MessageBase;
public record ClearCacheForObjectMessage(GameObjectHandler ObjectToCreateFor) : MessageBase;

public record CharacterDataCreatedMessage(CharacterData CharacterData) : SameThreadMessage;

public record CharacterDataAnalyzedMessage : MessageBase;
public record GameObjectHandlerCreatedMessage(GameObjectHandler GameObjectHandler, bool OwnedObject) : MessageBase;
public record GameObjectHandlerDestroyedMessage(GameObjectHandler GameObjectHandler, bool OwnedObject) : MessageBase;
public record TargetPlayerChangedMessage(IPlayerCharacter? Character) : MessageBase;
public record NameplateRedrawMessage : MessageBase;

public record PlayerVisibilityMessage(string Ident, bool IsVisible, bool Invalidate = false) : KeyedMessage(Ident, SameThread: true);

public record PairHandlerVisibleMessage(PairHandler Player) : MessageBase;
public record RecalculatePerformanceMessage(string? UID) : MessageBase;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
