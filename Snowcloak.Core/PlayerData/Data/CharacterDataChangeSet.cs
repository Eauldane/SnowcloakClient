using System.Collections;
using System.Text;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Data;

public sealed class CharacterDataChangeSet : IEnumerable<KeyValuePair<ObjectKind, IReadOnlyList<PlayerChanges>>>
{
    private static readonly ObjectKind[] ObjectKindOrder = Enum.GetValues<ObjectKind>();
    private static readonly PlayerChanges[] OrderedChanges =
    [
        PlayerChanges.ModFiles,
        PlayerChanges.ModManip,
        PlayerChanges.Glamourer,
        PlayerChanges.Customize,
        PlayerChanges.Heels,
        PlayerChanges.Honorific,
        PlayerChanges.ForcedRedraw,
        PlayerChanges.Moodles,
        PlayerChanges.PetNames,
    ];

    private readonly Dictionary<ObjectKind, HashSet<PlayerChanges>> _changes = [];

    public static IReadOnlyList<PlayerChanges> ApplyOrder => OrderedChanges;

    public int Count => _changes.Count;

    public IEnumerable<IReadOnlyList<PlayerChanges>> Values => _changes.Values.Select(GetOrderedChanges);

    public void Add(ObjectKind kind, PlayerChanges change)
    {
        if (!_changes.TryGetValue(kind, out var changes))
        {
            changes = [];
            _changes[kind] = changes;
        }

        changes.Add(change);
    }

    public bool Contains(ObjectKind kind, PlayerChanges change)
        => _changes.TryGetValue(kind, out var changes) && changes.Contains(change);

    public bool ContainsAny(params PlayerChanges[] requestedChanges)
    {
        ArgumentNullException.ThrowIfNull(requestedChanges);

        foreach (var changes in _changes.Values)
        {
            foreach (var change in requestedChanges)
            {
                if (changes.Contains(change))
                    return true;
            }
        }

        return false;
    }

    public bool TryGetValue(ObjectKind kind, out IReadOnlyList<PlayerChanges> changes)
    {
        if (_changes.TryGetValue(kind, out var existing))
        {
            changes = GetOrderedChanges(existing);
            return true;
        }

        changes = [];
        return false;
    }

    public IEnumerator<KeyValuePair<ObjectKind, IReadOnlyList<PlayerChanges>>> GetEnumerator()
    {
        foreach (var kind in ObjectKindOrder)
        {
            if (_changes.TryGetValue(kind, out var changes))
                yield return new KeyValuePair<ObjectKind, IReadOnlyList<PlayerChanges>>(kind, GetOrderedChanges(changes));
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    public override string ToString()
    {
        if (_changes.Count == 0)
            return string.Empty;

        StringBuilder builder = new();
        foreach (var item in this)
        {
            if (builder.Length > 0)
                builder.Append("; ");

            builder.Append(item.Key);
            builder.Append(": ");
            builder.AppendJoin(", ", item.Value);
        }

        return builder.ToString();
    }

    public static IReadOnlyList<PlayerChanges> GetOrderedChanges(IReadOnlyCollection<PlayerChanges> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
            return [];

        List<PlayerChanges> ordered = new(changes.Count);
        foreach (var change in OrderedChanges)
        {
            if (changes.Contains(change))
                ordered.Add(change);
        }

        foreach (var change in changes.OrderBy(change => (int)change))
        {
            if (!OrderedChanges.Contains(change))
                ordered.Add(change);
        }

        return ordered;
    }
}
