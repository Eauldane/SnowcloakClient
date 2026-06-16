namespace Snowcloak.Core.Replay;

public sealed class SymbolTable
{
    private readonly Dictionary<(string Scope, string Raw), string> _symbols = new();
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    public string Collection(Guid id) => Symbol("coll", id);

    public string Application(Guid id) => Symbol("app", id);

    public string CustomizeId(Guid id) => Symbol("cust", id);

    public string Handle(nint address) => Symbol("handle", address.ToString());

    public string Symbol(string scope, Guid id) => Symbol(scope, id == Guid.Empty ? "empty" : id.ToString("N"));

    public string Symbol(string scope, string raw)
    {
        var key = (scope, raw);
        if (_symbols.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var next = _counters.TryGetValue(scope, out var n) ? n + 1 : 1;
        _counters[scope] = next;
        var symbol = scope + "#" + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _symbols[key] = symbol;
        return symbol;
    }
}
