namespace Snowcloak.Core.Pairing;

public sealed class PairingAvailabilitySet
{
    private readonly HashSet<string> _idents = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> ToSnapshot() => _idents.ToArray();

    public int Count => _idents.Count;

    public bool Contains(string ident) => _idents.Contains(ident);

    public bool ApplyDelta(IEnumerable<string>? availableIdents, IEnumerable<string>? unavailableIdents, string? localIdent)
    {
        var additions = Normalize(availableIdents, localIdent);
        var removals = Normalize(unavailableIdents, localIdent);
        var beforeRemovalCount = _idents.Count;
        _idents.ExceptWith(removals);
        var changed = _idents.Count != beforeRemovalCount;

        var beforeAdditionCount = _idents.Count;
        _idents.UnionWith(additions);

        return changed || _idents.Count != beforeAdditionCount;
    }

    public bool Clear()
    {
        if (_idents.Count == 0)
            return false;

        _idents.Clear();
        return true;
    }

    private static HashSet<string> Normalize(IEnumerable<string>? idents, string? localIdent)
    {
        var result = idents?
            .Where(ident => !string.IsNullOrWhiteSpace(ident))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(localIdent))
            result.Remove(localIdent);

        return result;
    }
}
