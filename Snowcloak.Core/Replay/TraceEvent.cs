using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snowcloak.Core.Replay;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InboundEvent), "inbound")]
[JsonDerivedType(typeof(IpcOutEvent), "ipcOut")]
[JsonDerivedType(typeof(GameStateEvent), "gameState")]
public abstract record TraceEvent;

public sealed record InboundEvent(string Method, JsonElement Payload) : TraceEvent;

public sealed record IpcOutEvent(IpcCommand Command) : TraceEvent;

public sealed record GameStateEvent(string Key, JsonElement Value) : TraceEvent;

public sealed record Trace(int Version, IReadOnlyList<TraceEvent> Events)
{
    public const int CurrentVersion = 1;

    public static Trace Empty { get; } = new(CurrentVersion, []);
}
