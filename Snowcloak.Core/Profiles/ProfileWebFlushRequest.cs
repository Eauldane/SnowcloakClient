using System.Text.Json;

namespace Snowcloak.Core.Profiles;

public sealed record ProfileWebFlushRequest(string Id, string Ident, long Revision, DateTime DeadlineUtc)
{
    public static ProfileWebFlushRequest Begin(string ident, long revision, DateTime nowUtc)
        => new(Guid.NewGuid().ToString("N"), ident, revision, nowUtc.AddSeconds(10));

    public bool Expired(DateTime nowUtc) => nowUtc >= DeadlineUtc;

    public bool Matches(JsonElement message) =>
        message.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == Id &&
        message.TryGetProperty("ident", out var ident) && ident.ValueKind == JsonValueKind.String && ident.GetString() == Ident &&
        message.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number && revision.TryGetInt64(out var value) && value == Revision;
}
