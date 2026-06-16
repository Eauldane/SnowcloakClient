using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Snowcloak.PlayerData.Handlers;

namespace Snowcloak.Interop.Ipc;

public interface IPenumbraIpc : IPenumbraApply, IIpcCaller
{
    string? ModDirectory { get; }

    void CheckModDirectory();

    Task ConvertTextureFiles(ILogger logger, Dictionary<string, (TextureType TextureType, string[] Duplicates)> textures, IProgress<(string, int)> progress, CancellationToken token);

    Task<Dictionary<string, HashSet<string>>?> GetCharacterData(ILogger logger, IGameObjectHandle handler);

    string GetMetaManipulations();

    Task RemoveTemporaryCollectionAsync(ILogger logger, Guid applicationId, Guid collId);

    Task<(string[] forward, string[][] reverse)> ResolvePathsAsync(string[] forward, string[] reverse);
}
