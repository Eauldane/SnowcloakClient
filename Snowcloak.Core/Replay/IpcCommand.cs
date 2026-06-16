using System.Text.Json.Serialization;

namespace Snowcloak.Core.Replay;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(PenumbraCreateTemporaryCollection), "penumbra.createTempCollection")]
[JsonDerivedType(typeof(PenumbraAssignTemporaryCollection), "penumbra.assignTempCollection")]
[JsonDerivedType(typeof(PenumbraSetTemporaryMods), "penumbra.setTempMods")]
[JsonDerivedType(typeof(PenumbraSetManipulationData), "penumbra.setManip")]
[JsonDerivedType(typeof(PenumbraRemoveTemporaryCollection), "penumbra.removeTempCollection")]
[JsonDerivedType(typeof(PenumbraRedraw), "penumbra.redraw")]
[JsonDerivedType(typeof(GlamourerApplyAll), "glamourer.applyAll")]
[JsonDerivedType(typeof(GlamourerRevert), "glamourer.revert")]
[JsonDerivedType(typeof(GlamourerRevertByName), "glamourer.revertByName")]
[JsonDerivedType(typeof(CustomizeSetBodyScale), "customize.setBodyScale")]
[JsonDerivedType(typeof(CustomizeRevertById), "customize.revertById")]
[JsonDerivedType(typeof(HeelsSetOffset), "heels.setOffset")]
[JsonDerivedType(typeof(HonorificSetTitle), "honorific.setTitle")]
[JsonDerivedType(typeof(MoodlesSetStatus), "moodles.setStatus")]
[JsonDerivedType(typeof(PetNamesSetPlayerData), "petNames.setPlayerData")]
public abstract record IpcCommand;

public sealed record ModPath(string GamePath, string FilePath);

public sealed record PenumbraCreateTemporaryCollection(string Collection) : IpcCommand;

public sealed record PenumbraAssignTemporaryCollection(string Collection, string Handle) : IpcCommand;

public sealed record PenumbraSetTemporaryMods(string Application, string Collection, IReadOnlyList<ModPath> Paths) : IpcCommand
{
    public static PenumbraSetTemporaryMods Create(string application, string collection, IEnumerable<KeyValuePair<string, string>> paths)
        => new(application, collection,
            paths.Select(p => new ModPath(p.Key, p.Value))
                 .OrderBy(m => m.GamePath, StringComparer.Ordinal)
                 .ThenBy(m => m.FilePath, StringComparer.Ordinal)
                 .ToList());
}

public sealed record PenumbraSetManipulationData(string Application, string Collection, string ManipulationData) : IpcCommand;

public sealed record PenumbraRemoveTemporaryCollection(string Application, string Collection) : IpcCommand;

public sealed record PenumbraRedraw(string Application, string Handle) : IpcCommand;

public sealed record GlamourerApplyAll(string Application, string Handle, string Customization) : IpcCommand;

public sealed record GlamourerRevert(string Application, string Handle) : IpcCommand;

public sealed record GlamourerRevertByName(string Application, string Name) : IpcCommand;

public sealed record CustomizeSetBodyScale(string Handle, string Data) : IpcCommand;

public sealed record CustomizeRevertById(string CustomizeId) : IpcCommand;

public sealed record HeelsSetOffset(string Handle, string Data) : IpcCommand;

public sealed record HonorificSetTitle(string Handle, string Data) : IpcCommand;

public sealed record MoodlesSetStatus(string Handle, string Data) : IpcCommand;

public sealed record PetNamesSetPlayerData(string Handle, string Data) : IpcCommand;
