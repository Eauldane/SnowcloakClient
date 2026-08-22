using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.Manifest;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Protocol;
using Snowcloak.Core.Appearance;
using Snowcloak.Services.Mediator;
using System.Collections.Concurrent;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    private const int MaxConcurrentManifestFetches = 4;
    private static readonly TimeSpan[] ManifestResolveRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    ];
    private static readonly TimeSpan ManifestSteadyStateRetryDelay = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _manifestFetchGate = new(MaxConcurrentManifestFetches, MaxConcurrentManifestFetches);
    private readonly ConcurrentDictionary<string, byte> _manifestResolutionsInFlight = new(StringComparer.Ordinal);

    public async Task UserPushManifest(ManifestPushDto dto)
    {
        if (!IsConnected) return;
        try
        {
            await _snowHub!.InvokeAsync(nameof(UserPushManifest), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to push appearance manifest");
        }
    }

    public async Task<List<ManifestPointerDto>> UserGetCurrentManifests(List<string> uids)
    {
        if (!IsConnected) return [];
        return await _snowHub!.InvokeAsync<List<ManifestPointerDto>>(
            nameof(UserGetCurrentManifests), uids, _connectionLifecycle.ConnectionToken).ConfigureAwait(false);
    }

    public async Task<List<ExtensionDataSnapshotDto>> UserGetCurrentExtensionData(
        List<string> uids,
        List<string> keys,
        Dictionary<string, string> knownManifestHashes)
    {
        if (!IsConnected || _connectionContext.Dto?.ServerCapabilities.HasFlag(HubCapability.ExtensionDataSnapshots) is not true) return [];
        return await _snowHub!.InvokeAsync<List<ExtensionDataSnapshotDto>>(
            nameof(UserGetCurrentExtensionData), uids, keys, knownManifestHashes).ConfigureAwait(false);
    }

    public async Task<byte[]?> UserGetManifest(string hash)
    {
        if (!IsConnected) return null;
        return await _snowHub!.InvokeAsync<byte[]?>(
            nameof(UserGetManifest), hash, _connectionLifecycle.ConnectionToken).ConfigureAwait(false);
    }

    private async Task PushManifestInternal(CharacterData character, List<UserData> visibleCharacters)
    {
        var manifest = AppearanceManifestCodec.ToManifest(character);
        var bytes = ManifestCanonical.Serialize(manifest);
        var hash = ManifestCanonical.ComputeHash(manifest);
        var extensionData = character.ExtensionData.ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);

        if (!IsConnected)
        {
            Mediator.Publish(new LocalCharacterDataPushFailedMessage(extensionData, "Snowcloak is not connected."));
            return;
        }

        var dto = new ManifestPushDto
        {
            Recipients = visibleCharacters,
            ManifestHash = hash,
            InlineManifest = bytes,
            FileHashes = character.FileReplacements
                .SelectMany(kv => kv.Value)
                .Select(f => f.Hash)
                .Where(h => !string.IsNullOrEmpty(h))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };

        try
        {
            await _snowHub!.InvokeAsync(nameof(UserPushManifest), dto).ConfigureAwait(false);
            Mediator.Publish(new LocalCharacterDataPushedMessage(
                visibleCharacters,
                hash,
                extensionData));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to push appearance manifest for {hash}", hash);
            Mediator.Publish(new LocalCharacterDataPushFailedMessage(extensionData, "Snowcloak could not send the current manifest."));
        }
    }

    private Task RequestPairManifest(UserData user)
        => ResolveManifestsWithRetries([user.UID]);

    public Task ResolveManifestsForVisiblePairs(IReadOnlyList<OnlineUserIdentDto> visiblePairs)
    {
        var uids = (visiblePairs ?? [])
            .Select(p => p.User.UID)
            .Where(uid => !string.IsNullOrEmpty(uid))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return ResolveManifestsWithRetries(uids);
    }

    private async Task ResolveManifestsWithRetries(List<string> uids)
    {
        var ownedUids = uids
            .Where(uid => _manifestResolutionsInFlight.TryAdd(uid, 0))
            .ToList();
        if (ownedUids.Count == 0)
        {
            return;
        }

        var connectionToken = _connectionLifecycle.ConnectionToken;
        try
        {
            var unresolved = ownedUids;
            var attempt = 0;
            while (unresolved.Count > 0)
            {
                var result = await ResolveManifestsAttempt(unresolved, attempt, connectionToken).ConfigureAwait(false);
                unresolved = result.UnresolvedUids;
                if (unresolved.Count == 0)
                {
                    return;
                }
                unresolved = unresolved
                    .Where(uid => _pairManager.GetPairByUID(uid)?.IsVisible == true)
                    .ToList();
                if (unresolved.Count == 0)
                {
                    return;
                }

                var isSteadyStateRetry = attempt >= ManifestResolveRetryDelays.Length;
                if (attempt == ManifestResolveRetryDelays.Length)
                {
                    Logger.LogWarning(
                        "Manifest resolution remained incomplete for {count} visible pairs after {attempts} attempts " +
                        "(missingPointers={missingPointers}, unavailablePairs={unavailablePairs}, missingBlobs={missingBlobs}, failures={failures}); " +
                        "continuing reconciliation every {delay}s while visible",
                        unresolved.Count, attempt + 1, result.MissingPointerCount, result.PairUnavailableCount,
                        result.ManifestUnavailableCount, result.FailureCount, ManifestSteadyStateRetryDelay.TotalSeconds);
                }

                var delay = isSteadyStateRetry
                    ? ManifestSteadyStateRetryDelay
                    : ManifestResolveRetryDelays[attempt];
                Logger.LogDebug("Retrying manifest resolution for {count} pairs in {delay}s (attempt {attempt})",
                    unresolved.Count, delay.TotalSeconds, attempt + 2);
                await Task.Delay(delay, connectionToken).ConfigureAwait(false);
                attempt++;
            }
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
        {
            Logger.LogDebug("Manifest reconciliation canceled with its connection");
        }
        finally
        {
            foreach (var uid in ownedUids)
            {
                _manifestResolutionsInFlight.TryRemove(uid, out _);
            }
        }
    }

    private async Task<ManifestResolutionAttemptResult> ResolveManifestsAttempt(
        List<string> uids, int attempt, CancellationToken connectionToken)
    {
        if (!IsConnected)
        {
            return ManifestResolutionAttemptResult.AllUnresolved(uids);
        }

        List<ManifestPointerDto> pointers;
        try
        {
            pointers = await UserGetCurrentManifests(uids).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
        {
            Logger.LogDebug("Manifest resolution attempt {attempt} was canceled with its connection", attempt + 1);
            return ManifestResolutionAttemptResult.AllUnresolved(uids);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Manifest resolution attempt {attempt} failed", attempt + 1);
            return ManifestResolutionAttemptResult.AllFailed(uids);
        }

        var requestedUids = uids.ToHashSet(StringComparer.Ordinal);
        var pointerByUid = pointers
            .Where(pointer => pointer is not null
                && !string.IsNullOrEmpty(pointer.User.UID)
                && !string.IsNullOrEmpty(pointer.ManifestHash)
                && requestedUids.Contains(pointer.User.UID))
            .GroupBy(pointer => pointer.User.UID, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        var orderedPointers = pointerByUid.Values
            .OrderBy(pointer => _pairManager.GetPairByUID(pointer.User.UID)?.UserPair is null ? 1 : 0)
            .ToList();
        var tasks = orderedPointers
            .Select(pointer => ResolveManifestPointer(pointer, connectionToken))
            .ToArray();
        var pointerResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        var resolvedUids = pointerResults
            .Where(result => result.Outcome == ManifestResolutionOutcome.Resolved)
            .Select(result => result.Uid)
            .ToHashSet(StringComparer.Ordinal);
        var missingPointerCount = uids.Count(uid => !pointerByUid.ContainsKey(uid));
        var pairUnavailableCount = pointerResults.Count(result => result.Outcome == ManifestResolutionOutcome.PairUnavailable);
        var manifestUnavailableCount = pointerResults.Count(result => result.Outcome == ManifestResolutionOutcome.ManifestUnavailable);
        var failures = pointerResults.Where(result => result.Outcome == ManifestResolutionOutcome.Failed).ToList();
        if (failures.Count > 0)
        {
            Logger.LogWarning(failures[0].Exception,
                "{count} manifest fetches failed during resolution attempt {attempt}", failures.Count, attempt + 1);
        }

        return new ManifestResolutionAttemptResult(
            uids.Where(uid => !resolvedUids.Contains(uid)).ToList(),
            missingPointerCount,
            pairUnavailableCount,
            manifestUnavailableCount,
            failures.Count);
    }

    private async Task<ManifestResolutionResult> ResolveManifestPointer(
        ManifestPointerDto pointer, CancellationToken connectionToken)
    {
        await _manifestFetchGate.WaitAsync(connectionToken).ConfigureAwait(false);
        try
        {
            var pair = _pairManager.GetPairByUID(pointer.User.UID);
            if (pair is null)
            {
                return new(pointer.User.UID, ManifestResolutionOutcome.PairUnavailable);
            }

            if (pair.LastReceivedCharacterData != null
                && string.Equals(pair.LastReceivedManifestHash, pointer.ManifestHash, StringComparison.Ordinal))
            {
                return new(pointer.User.UID, ManifestResolutionOutcome.Resolved);
            }

            var manifestHashBeforeFetch = pair.LastReceivedManifestHash;
            var bytes = await UserGetManifest(pointer.ManifestHash).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                return new(pointer.User.UID, ManifestResolutionOutcome.ManifestUnavailable);
            }
            if (!string.Equals(pair.LastReceivedManifestHash, manifestHashBeforeFetch, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(pair.LastReceivedManifestHash))
            {
                return new(pointer.User.UID, ManifestResolutionOutcome.Resolved);
            }

            ApplyManifestBytes(pointer.User, bytes, pointer.Version, pointer.ReportedTriangles,
                pointer.ReportedVramBytes, pointer.ManifestHash);
            return new(pointer.User.UID, ManifestResolutionOutcome.Resolved);
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(pointer.User.UID, ManifestResolutionOutcome.Failed, ex);
        }
        finally
        {
            _manifestFetchGate.Release();
        }
    }

    public async Task Client_UserReceiveManifest(ManifestNotificationDto dto)
    {
        try
        {
            var bytes = dto.InlineManifest ?? await UserGetManifest(dto.ManifestHash).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                Logger.LogWarning("Received a manifest notification without retrievable bytes; scheduling reconciliation");
                _ = _backgroundTasks.Run(() => RequestPairManifest(dto.User), nameof(RequestPairManifest));
                return;
            }

            ApplyManifestBytes(dto.User, bytes, dto.Version, dto.ReportedTriangles, dto.ReportedVramBytes, dto.ManifestHash);
        }
        catch (OperationCanceledException) when (_connectionLifecycle.ConnectionToken.IsCancellationRequested)
        {
            Logger.LogDebug("Received manifest was canceled with its connection");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Received manifest could not be applied; scheduling reconciliation");
            _ = _backgroundTasks.Run(() => RequestPairManifest(dto.User), nameof(RequestPairManifest));
        }
    }

    private void ApplyManifestBytes(UserData user, byte[] bytes, long version, long? reportedTriangles, long? reportedVramBytes, string manifestHash)
    {
        var charaData = AppearanceManifestCodec.ToCharacterData(ManifestCanonical.Deserialize(bytes));
        var charaDto = new OnlineUserCharaDataDto(user, charaData)
        {
            DataVersion = version,
            ReportedTriangles = reportedTriangles,
            ReportedVramBytes = reportedVramBytes,
        };
        
        ExecuteSafely(() => _pairManager.ReceiveCharaData(charaDto, manifestHash));
    }

    private enum ManifestResolutionOutcome
    {
        Resolved,
        PairUnavailable,
        ManifestUnavailable,
        Failed,
    }

    private sealed record ManifestResolutionResult(
        string Uid, ManifestResolutionOutcome Outcome, Exception? Exception = null);

    private sealed record ManifestResolutionAttemptResult(
        List<string> UnresolvedUids,
        int MissingPointerCount,
        int PairUnavailableCount,
        int ManifestUnavailableCount,
        int FailureCount)
    {
        public static ManifestResolutionAttemptResult AllUnresolved(List<string> uids)
            => new(uids, uids.Count, 0, 0, 0);

        public static ManifestResolutionAttemptResult AllFailed(List<string> uids)
            => new(uids, 0, 0, 0, uids.Count);
    }
}
