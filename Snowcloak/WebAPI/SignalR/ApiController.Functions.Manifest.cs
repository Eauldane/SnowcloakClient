using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.Manifest;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Protocol;
using Snowcloak.Core.Appearance;
using Snowcloak.Services.Mediator;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    private static readonly TimeSpan ManifestResolveRetryDelay = TimeSpan.FromSeconds(5);

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
        return await _snowHub!.InvokeAsync<List<ManifestPointerDto>>(nameof(UserGetCurrentManifests), uids).ConfigureAwait(false);
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
        return await _snowHub!.InvokeAsync<byte[]?>(nameof(UserGetManifest), hash).ConfigureAwait(false);
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
        => ResolveManifestsInternal([user.UID], isRetry: false);

    public Task ResolveManifestsForVisiblePairs(IReadOnlyList<OnlineUserIdentDto> visiblePairs)
    {
        var uids = (visiblePairs ?? [])
            .Select(p => p.User.UID)
            .Where(uid => !string.IsNullOrEmpty(uid))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return ResolveManifestsInternal(uids, isRetry: false);
    }

    private async Task ResolveManifestsInternal(List<string> uids, bool isRetry)
    {
        if (!IsConnected || uids.Count == 0)
        {
            return;
        }
        
        List<string> retryUids = [];

        try
        {
            var pointers = await UserGetCurrentManifests(uids).ConfigureAwait(false);
            HashSet<string> pointerUids = new(StringComparer.Ordinal);
            foreach (var pointer in pointers)
            {
                if (pointer is null || string.IsNullOrEmpty(pointer.ManifestHash))
                {
                    continue;
                }

                pointerUids.Add(pointer.User.UID);

                var pair = _pairManager.GetPairByUID(pointer.User.UID);
                if (pair is null)
                {
                    continue;
                }

                if (pair.LastReceivedCharacterData != null
                    && string.Equals(pair.LastReceivedManifestHash, pointer.ManifestHash, StringComparison.Ordinal))
                {
                    continue;
                }

                var bytes = await UserGetManifest(pointer.ManifestHash).ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0)
                {
                    retryUids.Add(pointer.User.UID);
                    continue;
                }

                ApplyManifestBytes(pointer.User, bytes, pointer.Version, pointer.ReportedTriangles, pointer.ReportedVramBytes, pointer.ManifestHash);
            }

            if (!isRetry)
            {
                retryUids.AddRange(uids.Where(uid => !pointerUids.Contains(uid)));
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ResolveManifests failed (isRetry={isRetry})", isRetry);
            if (!isRetry)
            {
                retryUids = uids;
            }
        }

        if (!isRetry && retryUids.Count > 0)
        {
            Logger.LogDebug("Retrying manifest resolve for {count} pairs in {delay}s", retryUids.Count, ManifestResolveRetryDelay.TotalSeconds);
            _ = _backgroundTasks.Run(async () =>
            {
                await Task.Delay(ManifestResolveRetryDelay).ConfigureAwait(false);
                await ResolveManifestsInternal(retryUids, isRetry: true).ConfigureAwait(false);
            }, nameof(ResolveManifestsInternal));
        }
    }

    public async Task Client_UserReceiveManifest(ManifestNotificationDto dto)
    {
        try
        {
            var bytes = dto.InlineManifest ?? await UserGetManifest(dto.ManifestHash).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                Logger.LogWarning("Client_UserReceiveManifest: no manifest bytes for {user} hash {hash}", dto.User, dto.ManifestHash);
                return;
            }

            ApplyManifestBytes(dto.User, bytes, dto.Version, dto.ReportedTriangles, dto.ReportedVramBytes, dto.ManifestHash);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Client_UserReceiveManifest failed for {user}", dto.User);
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
}
