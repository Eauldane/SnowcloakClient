using System.Text.Json;

namespace Snowcloak.Core.Replay;

public static class TraceJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
    };

    public static string Serialize(Trace trace) => JsonSerializer.Serialize(trace, Pretty);

    public static Trace Deserialize(string json)
        => JsonSerializer.Deserialize<Trace>(json, Options)
           ?? throw new FormatException("Trace payload deserialised to null.");

    public static string Canonical(IpcCommand command) => JsonSerializer.Serialize(command, Options);

    public static string Canonical(TraceEvent traceEvent) => JsonSerializer.Serialize(traceEvent, Options);
}
