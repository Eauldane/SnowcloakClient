using System.Collections.ObjectModel;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.Core.Analysis;

public sealed class AnalysisObjectSnapshot
{
    public AnalysisObjectSnapshot(ObjectKind kind, IEnumerable<AnalysisFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        Kind = kind;
        var snapshot = new Dictionary<string, AnalysisFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            snapshot[file.Hash] = file;
        }
        Files = new ReadOnlyDictionary<string, AnalysisFileEntry>(snapshot);
    }

    public ObjectKind Kind { get; }
    public IReadOnlyDictionary<string, AnalysisFileEntry> Files { get; }
}
