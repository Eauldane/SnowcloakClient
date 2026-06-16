using System.Collections.ObjectModel;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.Core.Analysis;

public sealed class AnalysisSnapshot
{
    private static readonly IReadOnlyDictionary<ObjectKind, AnalysisObjectSnapshot> EmptyObjects =
        new ReadOnlyDictionary<ObjectKind, AnalysisObjectSnapshot>(new Dictionary<ObjectKind, AnalysisObjectSnapshot>());

    public static AnalysisSnapshot Empty { get; } = new(EmptyObjects);

    public AnalysisSnapshot(IEnumerable<AnalysisObjectSnapshot> objects)
        : this(new ReadOnlyDictionary<ObjectKind, AnalysisObjectSnapshot>(
            ValidateObjects(objects).ToDictionary(obj => obj.Kind, obj => obj)))
    {
    }

    private AnalysisSnapshot(IReadOnlyDictionary<ObjectKind, AnalysisObjectSnapshot> objects)
    {
        Objects = objects;
    }

    public IReadOnlyDictionary<ObjectKind, AnalysisObjectSnapshot> Objects { get; }
    public bool IsEmpty => Objects.Count == 0;
    public IEnumerable<AnalysisFileEntry> Files => Objects.Values.SelectMany(obj => obj.Files.Values);

    public AnalysisSnapshot UpdateFiles(IEnumerable<AnalysisFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var updates = new Dictionary<string, AnalysisFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            updates[file.Hash] = file;
        }
        if (updates.Count == 0) return this;

        return new AnalysisSnapshot(Objects.Values.Select(obj =>
            new AnalysisObjectSnapshot(obj.Kind, obj.Files.Values.Select(file =>
                updates.TryGetValue(file.Hash, out var updated) ? updated : file))));
    }

    private static IEnumerable<AnalysisObjectSnapshot> ValidateObjects(IEnumerable<AnalysisObjectSnapshot> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return objects;
    }
}
