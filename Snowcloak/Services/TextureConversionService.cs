using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Snowcloak.API.Dto.Files;
using Snowcloak.FileCache;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using System.Text;

namespace Snowcloak.Services;

public sealed class TextureConversionService : DisposableMediatorSubscriberBase
{
    private readonly FileUploadManager _fileUploadManager;
    private readonly ApiController _apiController;
    private readonly FileCacheManager _fileCacheManager;
    private readonly IpcManager _ipcManager;

    public TextureConversionService(ILogger<TextureConversionService> logger, SnowMediator mediator,
        IpcManager ipcManager, FileCacheManager fileCacheManager, FileUploadManager fileUploadManager, ApiController apiController) : base(logger, mediator)
    {
        _ipcManager = ipcManager;
        _fileCacheManager = fileCacheManager;
        _fileUploadManager = fileUploadManager;
        _apiController = apiController;
    }

    public async Task ConvertTextureFiles(ILogger logger, Dictionary<string, (TextureType TextureType, string[] Duplicates)> textures,
        IProgress<(string, int)> progress, CancellationToken token)
    {
        if (!_ipcManager.Penumbra.APIAvailable || textures.Count == 0)
        {
            return;
        }

        var filePaths = textures.Keys.ToArray();
        var originalEntries = _fileCacheManager.GetFileCachesByPaths(filePaths);
        var originalHashesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in originalEntries)
        {
            if (entry.Value == null)
            {
                Logger.LogWarning("Skipping original upload check for texture {path} because it could not be resolved", entry.Key);
                continue;
            }

            originalHashesByPath[entry.Key] = entry.Value.Hash;
        }

        await OfferOriginalUploads(originalHashesByPath.Values, token).ConfigureAwait(false);

        await _ipcManager.Penumbra.ConvertTextureFiles(logger, textures, progress, token).ConfigureAwait(false);

        var updatedEntries = _fileCacheManager.GetFileCachesByPaths(filePaths);
        var replacementsByNewHash = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in updatedEntries)
        {
            if (entry.Value == null) continue;
            if (!originalHashesByPath.TryGetValue(entry.Key, out var originalHash)) continue;

            var newHash = entry.Value.Hash;
            if (string.Equals(newHash, originalHash, StringComparison.OrdinalIgnoreCase)) continue;

            if (replacementsByNewHash.TryGetValue(newHash, out var existingOriginal))
            {
                if (!string.Equals(existingOriginal, originalHash, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogWarning("Converted texture {path} produced hash {newHash} already mapped to {existingOriginal}; keeping original mapping.",
                        entry.Key, newHash, existingOriginal);
                }
                continue;
            }

            replacementsByNewHash[newHash] = originalHash;
        }

        if (replacementsByNewHash.Count == 0)
        {
            return;
        }

        var metadataByHash = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal);
        foreach (var replacement in replacementsByNewHash)
        {
            metadataByHash[replacement.Key] = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["replacesHash"] = Encoding.ASCII.GetBytes(replacement.Value)
            };
        }

        await _fileUploadManager.UploadFilesWithMetadata(metadataByHash, token).ConfigureAwait(false);

        var mappings = replacementsByNewHash
            .Select(entry => new TextureCompressionMappingDto
            {
                OriginalHash = entry.Value,
                CompressedHash = entry.Key
            })
            .ToList();

        if (mappings.Count > 0)
        {
            await _apiController.UserSetTextureCompressionMappings(new TextureCompressionMappingBatchDto { Mappings = mappings })
                .ConfigureAwait(false);
        }
    }

    private async Task OfferOriginalUploads(IEnumerable<string> hashes, CancellationToken token)
    {
        var originalHashes = hashes.Distinct(StringComparer.Ordinal).ToList();
        if (originalHashes.Count == 0)
        {
            return;
        }

        try
        {
            var progress = new Progress<string>(message => Logger.LogDebug("{message}", message));
            var missing = await _fileUploadManager.UploadFiles(originalHashes, progress, token).ConfigureAwait(false);
            if (missing.Count > 0)
            {
                Logger.LogDebug("Original texture upload skipped for {count} file(s): {hashes}", missing.Count, string.Join(", ", missing));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogDebug(ex, "Skipping original texture upload because file transfer is unavailable");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to offer original texture upload");
        }
    }
}
