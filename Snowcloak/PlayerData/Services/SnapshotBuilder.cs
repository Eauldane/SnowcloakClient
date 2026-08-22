using ElezenTools.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.Configuration.Models;
using Snowcloak.FileCache;
using Snowcloak.Interop.Ipc;
using Snowcloak.PlayerData.Data;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using System.Diagnostics;

namespace Snowcloak.PlayerData.Services;

public sealed class SnapshotBuilder
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileCacheManager _fileCacheManager;
    private readonly IpcManager _ipcManager;
    private readonly ILogger<SnapshotBuilder> _logger;
    private readonly XivDataAnalyzer _modelAnalyzer;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly SnowMediator _snowMediator;
    private readonly TransientResourceManager _transientResourceManager;

    public SnapshotBuilder(
        ILogger<SnapshotBuilder> logger,
        DalamudUtilService dalamudUtil,
        IpcManager ipcManager,
        TransientResourceManager transientResourceManager,
        FileCacheManager fileCacheManager,
        PerformanceCollectorService performanceCollector,
        XivDataAnalyzer modelAnalyzer,
        SnowMediator snowMediator)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _ipcManager = ipcManager;
        _transientResourceManager = transientResourceManager;
        _fileCacheManager = fileCacheManager;
        _performanceCollector = performanceCollector;
        _modelAnalyzer = modelAnalyzer;
        _snowMediator = snowMediator;
    }

    public ValueProgress<SnapshotBuildProgress> Progress { get; } = new();

    public async Task<CharacterDataFragment?> BuildCharacterData(GameObjectHandler playerRelatedObject, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(playerRelatedObject);

        if (!_ipcManager.Initialized)
        {
            throw new InvalidOperationException("Penumbra or Glamourer is not connected");
        }

        if (await HasNullDrawObject(playerRelatedObject).ConfigureAwait(false))
        {
            _logger.LogTrace("Pointer was zero for {objectKind}", playerRelatedObject.ObjectKind);
            return null;
        }

        try
        {
            return await _performanceCollector.LogPerformance(this, $"CreateCharacterData>{playerRelatedObject.ObjectKind}", async () =>
            {
                return await BuildSnapshot(playerRelatedObject, token).ConfigureAwait(false);
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Cancelled creating Character data for {object}", playerRelatedObject);
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to create {object} data", playerRelatedObject);
        }

        return null;
    }

    private async Task<bool> HasNullDrawObject(GameObjectHandler playerRelatedObject)
    {
        try
        {
            if (playerRelatedObject.Address == IntPtr.Zero)
            {
                return true;
            }

            return await CheckForNullDrawObject(playerRelatedObject.Address).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create data for {object}", playerRelatedObject);
            return true;
        }
    }

    private async Task<CharacterDataFragment> BuildSnapshot(GameObjectHandler playerRelatedObject, CancellationToken token)
    {
        var objectKind = playerRelatedObject.ObjectKind;
        var stopwatch = Stopwatch.StartNew();

        _logger.LogDebug("Building character data for {obj}", playerRelatedObject);

        var state = await RunStage(objectKind, SnapshotBuildStage.Collect, ct => Collect(playerRelatedObject, ct), token).ConfigureAwait(false);
        await RunStage(objectKind, SnapshotBuildStage.Resolve, ct => Resolve(state, ct), token).ConfigureAwait(false);
        await RunStage(objectKind, SnapshotBuildStage.MergeTransients, ct => MergeTransients(state, ct), token).ConfigureAwait(false);
        await RunStage(objectKind, SnapshotBuildStage.Fragment, ct => PopulateFragment(state, ct), token).ConfigureAwait(false);

        _logger.LogInformation("Building character data for {obj} took {time}ms", objectKind, stopwatch.Elapsed.TotalMilliseconds);
        Progress.Report(new(objectKind, SnapshotBuildStage.Idle, stopwatch.Elapsed));

        return state.Fragment;
    }

    private async Task<SnapshotBuildState> Collect(GameObjectHandler playerRelatedObject, CancellationToken ct)
    {
        await ObjectTableCache.WaitWhileCharacterIsDrawing(_logger, playerRelatedObject, Guid.NewGuid(), 30000, ct: ct).ConfigureAwait(false);

        var totalWaitTime = TimeSpan.FromSeconds(10);
        while (!await ObjectTableCache.IsObjectPresentAsync(await _dalamudUtil.CreateGameObjectAsync(playerRelatedObject.Address).ConfigureAwait(false)).ConfigureAwait(false)
               && totalWaitTime > TimeSpan.Zero)
        {
            _logger.LogTrace("Character is null but it shouldn't be, waiting");
            await Task.Delay(50, ct).ConfigureAwait(false);
            totalWaitTime -= TimeSpan.FromMilliseconds(50);
        }

        ct.ThrowIfCancellationRequested();

        Dictionary<string, List<ushort>>? boneIndices =
            playerRelatedObject.ObjectKind != ObjectKind.Player
                ? null
                : await Service.RunOnFrameworkAsync(() => _modelAnalyzer.GetSkeletonBoneIndices(playerRelatedObject)).ConfigureAwait(false);

        return new(playerRelatedObject, CreateFragment(playerRelatedObject.ObjectKind), boneIndices);
    }

    private async Task Resolve(SnapshotBuildState state, CancellationToken ct)
    {
        var resolvedPaths = await _ipcManager.Penumbra.GetCharacterData(_logger, state.Handler).ConfigureAwait(false);
        if (resolvedPaths == null)
        {
            throw new InvalidOperationException("Penumbra returned null data");
        }

        ct.ThrowIfCancellationRequested();

        state.Fragment.FileReplacements =
            new HashSet<FileReplacement>(resolvedPaths.Select(c => new FileReplacement([.. c.Value], c.Key)), FileReplacementComparer.Instance)
                .Where(p => p.HasFileReplacement)
                .ToHashSet(FileReplacementComparer.Instance);
        state.Fragment.FileReplacements.RemoveWhere(c => c.GamePaths.Any(g => !SupportedFileTypes.IsAllowedPath(g)));

        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("== Static Replacements ==");
        foreach (var replacement in state.Fragment.FileReplacements.Where(i => i.HasFileReplacement).OrderBy(i => i.GamePaths.First(), StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug("=> {repl}", replacement);
            ct.ThrowIfCancellationRequested();
        }
    }

    private async Task MergeTransients(SnapshotBuildState state, CancellationToken ct)
    {
        await _transientResourceManager.WaitForRecording(ct).ConfigureAwait(false);

        if (state.ObjectKind == ObjectKind.Pet)
        {
            foreach (var item in state.Fragment.FileReplacements.Where(i => i.HasFileReplacement).SelectMany(p => p.GamePaths))
            {
                if (_transientResourceManager.AddTransientResource(state.ObjectKind, item))
                {
                    _logger.LogDebug("Marking static {item} for Pet as transient", item);
                }
            }

            _logger.LogTrace("Clearing {count} Static Replacements for Pet", state.Fragment.FileReplacements.Count);
            state.Fragment.FileReplacements.Clear();
        }

        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Handling transient update for {obj}", state.Handler);

        _transientResourceManager.ClearTransientPaths(state.ObjectKind, state.Fragment.FileReplacements.SelectMany(c => c.GamePaths).ToList());

        var transientPaths = ManageSemiTransientData(state.ObjectKind);
        var resolvedTransientPaths = await GetFileReplacementsFromPaths(transientPaths, new HashSet<string>(StringComparer.Ordinal)).ConfigureAwait(false);

        _logger.LogDebug("== Transient Replacements ==");
        foreach (var replacement in resolvedTransientPaths.Select(c => new FileReplacement([.. c.Value], c.Key)).OrderBy(f => f.ResolvedPath, StringComparer.Ordinal))
        {
            _logger.LogDebug("=> {repl}", replacement);
            state.Fragment.FileReplacements.Add(replacement);
        }

        _transientResourceManager.CleanUpSemiTransientResources(state.ObjectKind, [.. state.Fragment.FileReplacements]);

        ct.ThrowIfCancellationRequested();

        state.Fragment.FileReplacements =
            new HashSet<FileReplacement>(
                state.Fragment.FileReplacements.Where(v => v.HasFileReplacement).OrderBy(v => v.ResolvedPath, StringComparer.Ordinal),
                FileReplacementComparer.Instance);
    }

    private async Task PopulateFragment(SnapshotBuildState state, CancellationToken ct)
    {
        Task<string> getHeelsOffset = _ipcManager.Heels.GetOffsetAsync();
        Task<string> getGlamourerData = _ipcManager.Glamourer.GetCharacterCustomizationAsync(state.Handler.Address);
        Task<string?> getCustomizeData = _ipcManager.CustomizePlus.GetScaleAsync(state.Handler.Address);
        Task<string> getHonorificTitle = _ipcManager.Honorific.GetTitle();

        state.Fragment.GlamourerString = await getGlamourerData.ConfigureAwait(false);
        _logger.LogDebug("Glamourer is now: {data}", state.Fragment.GlamourerString);
        var customizeScale = await getCustomizeData.ConfigureAwait(false);
        state.Fragment.CustomizePlusScale = customizeScale ?? string.Empty;
        _logger.LogDebug("Customize is now: {data}", state.Fragment.CustomizePlusScale);

        if (state.ObjectKind == ObjectKind.Player)
        {
            var playerFragment = (CharacterDataFragmentPlayer)state.Fragment;
            playerFragment.ManipulationString = _ipcManager.Penumbra.GetMetaManipulations();

            playerFragment.HonorificData = await getHonorificTitle.ConfigureAwait(false);
            _logger.LogDebug("Honorific is now: {data}", playerFragment.HonorificData);

            playerFragment.HeelsData = await getHeelsOffset.ConfigureAwait(false);
            _logger.LogDebug("Heels is now: {heels}", playerFragment.HeelsData);

            playerFragment.MoodlesData = await _ipcManager.Moodles.GetStatusAsync(state.Handler.Address).ConfigureAwait(false) ?? string.Empty;
            _logger.LogDebug("Moodles is now: {moodles}", playerFragment.MoodlesData);

            playerFragment.PetNamesData = _ipcManager.PetNames.GetLocalNames();
            _logger.LogDebug("Pet Nicknames is now: {petnames}", playerFragment.PetNamesData);
        }

        ct.ThrowIfCancellationRequested();

        var toCompute = state.Fragment.FileReplacements.Where(f => !f.IsFileSwap).ToArray();
        _logger.LogDebug("Getting Hashes for {amount} Files", toCompute.Length);
        var computedPaths = _fileCacheManager.GetFileCachesByPaths(toCompute.Select(c => c.ResolvedPath).ToArray());
        foreach (var file in toCompute)
        {
            ct.ThrowIfCancellationRequested();
            file.Hash = computedPaths[file.ResolvedPath]?.Hash ?? string.Empty;
        }

        var removed = state.Fragment.FileReplacements.RemoveWhere(f => !f.IsFileSwap && string.IsNullOrEmpty(f.Hash));
        if (removed > 0)
        {
            _logger.LogDebug("Removed {amount} of invalid files", removed);
        }

        ct.ThrowIfCancellationRequested();

        if (state.ObjectKind != ObjectKind.Player)
        {
            return;
        }

        try
        {
            await VerifyPlayerAnimationBones(state.BoneIndices, (CharacterDataFragmentPlayer)state.Fragment, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Cancelled during player animation verification");
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to verify player animations, continuing without further verification");
        }
    }

    private async Task RunStage(ObjectKind objectKind, SnapshotBuildStage stage, Func<CancellationToken, Task> action, CancellationToken token)
    {
        await RunStage<object?>(objectKind, stage, async ct =>
        {
            await action(ct).ConfigureAwait(false);
            return null;
        }, token).ConfigureAwait(false);
    }

    private async Task<T> RunStage<T>(ObjectKind objectKind, SnapshotBuildStage stage, Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        Progress.Report(new(objectKind, stage, TimeSpan.Zero));
        _logger.LogDebug("Starting own-data snapshot stage {stage} for {objectKind}", stage, objectKind);

        try
        {
            return await action(token).ConfigureAwait(false);
        }
        finally
        {
            _logger.LogDebug("Finished own-data snapshot stage {stage} for {objectKind} in {elapsed}ms", stage, objectKind, stopwatch.Elapsed.TotalMilliseconds);
            Progress.Report(new(objectKind, stage, stopwatch.Elapsed));
        }
    }

    private static async Task<bool> CheckForNullDrawObject(IntPtr playerPointer)
    {
        return await Service.RunOnFrameworkAsync(() => CheckForNullDrawObjectUnsafe(playerPointer)).ConfigureAwait(false);
    }

    private static unsafe bool CheckForNullDrawObjectUnsafe(IntPtr playerPointer)
    {
        return ((Character*)playerPointer)->GameObject.DrawObject == null;
    }

    private async Task VerifyPlayerAnimationBones(Dictionary<string, List<ushort>>? boneIndices, CharacterDataFragmentPlayer fragment, CancellationToken ct)
    {
        if (boneIndices == null)
        {
            return;
        }

        foreach (var kvp in boneIndices)
        {
            _logger.LogDebug("Found {skellyname} ({idx} bone indices) on player: {bones}", kvp.Key, kvp.Value.Any() ? kvp.Value.Max() : 0, string.Join(',', kvp.Value));
        }

        if (boneIndices.All(u => u.Value.Count == 0))
        {
            return;
        }

        int noValidationFailed = 0;
        foreach (var file in fragment.FileReplacements.Where(f => !f.IsFileSwap && f.GamePaths.First().EndsWith("pap", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            ct.ThrowIfCancellationRequested();

            var skeletonIndices = await Service.RunOnFrameworkAsync(() => _modelAnalyzer.GetBoneIndicesFromPap(file.Hash)).ConfigureAwait(false);
            bool validationFailed = false;
            if (skeletonIndices != null)
            {
                if (skeletonIndices.All(k => k.Value.Max() <= 105))
                {
                    _logger.LogTrace("All indices of {path} are <= 105, ignoring", file.ResolvedPath);
                    continue;
                }

                _logger.LogDebug("Verifying bone indices for {path}, found {x} skeletons", file.ResolvedPath, skeletonIndices.Count);

                foreach (var boneCount in skeletonIndices.Select(k => k).ToList())
                {
                    if (boneCount.Value.Max() > boneIndices.SelectMany(b => b.Value).Max())
                    {
                        _logger.LogWarning("Found more bone indices on the animation {path} skeleton {skl} (max indice {idx}) than on any player related skeleton (max indice {idx2})",
                            file.ResolvedPath, boneCount.Key, boneCount.Value.Max(), boneIndices.SelectMany(b => b.Value).Max());
                        validationFailed = true;
                        break;
                    }
                }
            }

            if (validationFailed)
            {
                noValidationFailed++;
                _logger.LogDebug("Removing {file} from sent file replacements and transient data", file.ResolvedPath);
                fragment.FileReplacements.Remove(file);
                foreach (var gamePath in file.GamePaths)
                {
                    _transientResourceManager.RemoveTransientResource(ObjectKind.Player, gamePath);
                }
            }
        }

        if (noValidationFailed > 0)
        {
            _snowMediator.Publish(new NotificationMessage("Invalid Skeleton Setup",
                $"Your client is attempting to send {noValidationFailed} animation files with invalid bone data. Those animation files have been removed from your sent data. " +
                $"Verify that you are using the correct skeleton for those animation files (Check /xllog for more information).",
                NotificationType.Warning, TimeSpan.FromSeconds(10)));
        }
    }

    private async Task<IReadOnlyDictionary<string, string[]>> GetFileReplacementsFromPaths(HashSet<string> forwardResolve, HashSet<string> reverseResolve)
    {
        var forwardPaths = forwardResolve.ToArray();
        var reversePaths = reverseResolve.ToArray();
        Dictionary<string, List<string>> resolvedPaths = new(StringComparer.Ordinal);
        var (forward, reverse) = await _ipcManager.Penumbra.ResolvePathsAsync(forwardPaths, reversePaths).ConfigureAwait(false);
        for (int i = 0; i < forwardPaths.Length; i++)
        {
            var filePath = forward[i].ToLowerInvariant();
            if (resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.Add(forwardPaths[i].ToLowerInvariant());
            }
            else
            {
                resolvedPaths[filePath] = [forwardPaths[i].ToLowerInvariant()];
            }
        }

        for (int i = 0; i < reversePaths.Length; i++)
        {
            var filePath = reversePaths[i].ToLowerInvariant();
            if (resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.AddRange(reverse[i].Select(c => c.ToLowerInvariant()));
            }
            else
            {
                resolvedPaths[filePath] = new List<string>(reverse[i].Select(c => c.ToLowerInvariant()).ToList());
            }
        }

        return resolvedPaths.ToDictionary(k => k.Key, k => k.Value.ToArray(), StringComparer.OrdinalIgnoreCase).AsReadOnly();
    }

    private HashSet<string> ManageSemiTransientData(ObjectKind objectKind)
    {
        _transientResourceManager.PersistTransientResources(objectKind);

        HashSet<string> pathsToResolve = new(StringComparer.Ordinal);
        foreach (var path in _transientResourceManager.GetSemiTransientResources(objectKind).Where(path => !string.IsNullOrEmpty(path)))
        {
            pathsToResolve.Add(path);
        }

        return pathsToResolve;
    }

    private static CharacterDataFragment CreateFragment(ObjectKind objectKind)
    {
        return objectKind == ObjectKind.Player ? new CharacterDataFragmentPlayer() : new CharacterDataFragment();
    }

    private sealed record SnapshotBuildState(GameObjectHandler Handler, CharacterDataFragment Fragment, Dictionary<string, List<ushort>>? BoneIndices)
    {
        public ObjectKind ObjectKind => Handler.ObjectKind;
    }
}

public enum SnapshotBuildStage
{
    Idle,
    Collect,
    Resolve,
    MergeTransients,
    Fragment,
}

public readonly record struct SnapshotBuildProgress(ObjectKind ObjectKind, SnapshotBuildStage Stage, TimeSpan Elapsed);
