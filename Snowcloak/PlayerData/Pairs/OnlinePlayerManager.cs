using System;
using System.Collections.Generic;
using System.Linq;
using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.API.Data.Comparer;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI;

namespace Snowcloak.PlayerData.Pairs;

public class OnlinePlayerManager : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileUploadManager _fileTransferManager;
    private readonly HashSet<PairHandler> _newVisiblePlayers = [];
    private readonly HashSet<UserData> _pendingVisibleUsers = new(UserDataComparer.Instance);
    private readonly PairManager _pairManager;
    private CharacterData? _lastSentData;

    public OnlinePlayerManager(ILogger<OnlinePlayerManager> logger, ApiController apiController, DalamudUtilService dalamudUtil,
        PairManager pairManager, SnowMediator mediator, FileUploadManager fileTransferManager) : base(logger, mediator)
    {
        _apiController = apiController;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _fileTransferManager = fileTransferManager;
        Mediator.Subscribe<PlayerChangedMessage>(this, (_) => PlayerManagerOnPlayerHasChanged());
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => FrameworkOnUpdate());
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            var newData = msg.CharacterData;
            if (_lastSentData == null || (!string.Equals(newData.DataHash.Value, _lastSentData.DataHash.Value, StringComparison.Ordinal)))
            {
                var uploadableReplacementCount = newData.FileReplacements.Sum(kvp => kvp.Value.Count(v => string.IsNullOrEmpty(v.FileSwapPath)));
                var fileSwapCount = newData.FileReplacements.Sum(kvp => kvp.Value.Count(v => !string.IsNullOrEmpty(v.FileSwapPath)));
                Logger.LogInformation(
                    "Built local character data {hash}: objects={objectCount}, uploadableFiles={uploadableReplacementCount}, fileSwaps={fileSwapCount}, glamourerEntries={glamourerCount}",
                    newData.DataHash.Value,
                    newData.FileReplacements.Count,
                    uploadableReplacementCount,
                    fileSwapCount,
                    newData.GlamourerData.Count);
                Logger.LogDebug("Pushing updated character data");
                _lastSentData = newData;
                var visibleUsers = _pairManager.GetVisibleUsers();
                var pendingVisibleUsers = ConsumePendingVisibleUsers();
                if (pendingVisibleUsers.Any())
                {
                    visibleUsers = visibleUsers.Union(pendingVisibleUsers, UserDataComparer.Instance).ToList();
                }
                PushCharacterData(visibleUsers);
                
            }
            else
            {
                Logger.LogDebug("Not sending data for {hash}", newData.DataHash.Value);
            }
        });
        Mediator.Subscribe<PairHandlerVisibleMessage>(this, (msg) =>
        {
            if (_lastSentData == null)
            {
                _pendingVisibleUsers.Add(msg.Player.Pair.UserData);
            }

            _newVisiblePlayers.Add(msg.Player);
        });
        Mediator.Subscribe<ConnectedMessage>(this, (_) =>
        {
            var visibleUsers = _pairManager.GetVisibleUsers();
            if (_lastSentData == null)
            {
                Logger.LogInformation(
                    "Connected to server but no cached local character data is available yet; skipping initial push. Visible users={visibleUserCount}, pending visible users={pendingVisibleUserCount}",
                    visibleUsers.Count,
                    _pendingVisibleUsers.Count);
                return;
            }

            Logger.LogInformation("Connected to server, pushing cached local character data {hash} to {visibleUserCount} visible users",
                _lastSentData.DataHash.Value, visibleUsers.Count);
            PushCharacterData(visibleUsers);
        });
    }

    private void FrameworkOnUpdate()
    {
        if (!_dalamudUtil.GetIsPlayerPresent() || !_apiController.IsConnected) return;

        if (!_newVisiblePlayers.Any()) return;
        var newVisiblePlayers = _newVisiblePlayers.ToList();
        _newVisiblePlayers.Clear();
        Logger.LogTrace("Has new visible players, requesting cached character data and pushing local character data");

        var visiblePlayerIdents = newVisiblePlayers
            .Select(player => player.Pair.Ident)
            .Where(ident => !string.IsNullOrWhiteSpace(ident))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var visibleUsers = newVisiblePlayers
            .Select(player => player.Pair.UserData)
            .Distinct(UserDataComparer.Instance)
            .ToList();

        RequestCharacterData(visiblePlayerIdents);
        PushCharacterData(visibleUsers);
    }

    private void PlayerManagerOnPlayerHasChanged()
    {
        PushCharacterData(_pairManager.GetVisibleUsers());
    }

    private void PushCharacterData(List<UserData> visiblePlayers)
    {
        if (_lastSentData == null)
            return;

        _ = Task.Run(() => PushCharacterDataInternal(_lastSentData.DeepClone(), visiblePlayers));
    }

    private async Task PushCharacterDataInternal(CharacterData data, List<UserData> visiblePlayers)
    {
        try
        {
            var dataToSend = await _fileTransferManager.UploadFiles(data, visiblePlayers).ConfigureAwait(false);
            await _apiController.PushCharacterData(dataToSend, visiblePlayers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Character data upload was cancelled");
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "FileTransferManager is not initialized", StringComparison.Ordinal))
        {
            Logger.LogDebug("Skipping character data upload because file transfers are not initialized yet");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unexpected exception while pushing character data");
        }

    }

    private void RequestCharacterData(List<string> visiblePlayerIdents)
    {
        if (!visiblePlayerIdents.Any())
            return;

        _ = Task.Run(() => RequestCharacterDataInternal(visiblePlayerIdents));
    }

    private async Task RequestCharacterDataInternal(List<string> visiblePlayerIdents)
    {
        try
        {
            await _apiController.UserGetPairsInRange(visiblePlayerIdents).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unexpected exception while requesting cached character data");
        }
    }

    private List<UserData> ConsumePendingVisibleUsers()
    {
        if (!_pendingVisibleUsers.Any())
            return [];
        var pending = _pendingVisibleUsers.ToList();
        _pendingVisibleUsers.Clear();
        return pending;
    }
}
