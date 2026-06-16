using ElezenTools.UI;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Comparer;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using ElezenTools.Core.Async;
using Snowcloak.Core.PlayerData;
using Snowcloak.PlayerData.Factories;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using System.Collections.Concurrent;
using System.Numerics;
using System.Collections.Generic;

namespace Snowcloak.PlayerData.Pairs;

public class Pair : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly BlockListStore _blockListStore;
    private readonly SemaphoreSlim _creationSemaphore = new(1);
    private readonly ILogger<Pair> _logger;
    private readonly NotesStore _notesStore;
    private readonly PairAppearanceCacheService _pairAppearanceCache;
    private readonly SnowcloakConfigService _snowcloakConfig;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly PairHoldLedger _holdLedger = new();
    private readonly SingleFlightCts _applicationFlight = new();
    private volatile TaskCompletionSource _cachedPlayerReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OnlineUserIdentDto? _onlineUserIdentDto;
    private int _disposed;
    public Vector4 PairColour { get; private set; }

    public Pair(ILogger<Pair> logger, UserData userData, PairHandlerFactory cachedPlayerFactory,
        SnowMediator mediator, SnowcloakConfigService snowcloakConfig, NotesStore notesStore, BlockListStore blockListStore,
        PairAppearanceCacheService pairAppearanceCache)
        : base(logger, mediator)
    {
        _logger = logger;
        _cachedPlayerFactory = cachedPlayerFactory;
        _snowcloakConfig = snowcloakConfig;
        _notesStore = notesStore;
        _blockListStore = blockListStore;
        _pairAppearanceCache = pairAppearanceCache;
        _backgroundTasks = new BackgroundTaskTracker(logger);

        UserData = userData;
        PairColour = ElezenTools.UI.Colour.HexToVector4(UserData.DisplayColour);

        Mediator.SubscribeKeyed<HoldPairApplicationMessage>(this, UserData.UID, (msg) => HoldApplication(msg.Source));
        Mediator.SubscribeKeyed<UnholdPairApplicationMessage>(this, UserData.UID, (msg) => UnholdApplication(msg.Source));
    }
    
    public ConcurrentDictionary<GroupFullInfoDto, GroupPairFullInfoDto> GroupPair { get; } = new(GroupDtoComparer.Instance);
    public bool HasCachedPlayer => CachedPlayer != null && !string.IsNullOrEmpty(CachedPlayer.PlayerName) && _onlineUserIdentDto != null;
    public bool IsOnline => CachedPlayer != null;
    public bool IsChatOnly => _onlineUserIdentDto?.Mode == ConnectionMode.ChatOnly;

    public bool IsPaused => EffectivePermissionsResolver.IsPaused(BuildDirectPermissions(), BuildGroupPermissionViews());

    private DirectPermissions? BuildDirectPermissions()
        => UserPair == null ? null : new DirectPermissions(UserPair.OwnPermissions, UserPair.OtherPermissions);

    private List<GroupPermissionView> BuildGroupPermissionViews()
        => GroupPair.Select(kv => new GroupPermissionView(
            kv.Key.GroupUserPermissions, kv.Key.GroupPermissions,
            kv.Value.OwnGroupUserPermissions, kv.Value.OtherGroupUserPermissions)).ToList();

    public bool IsDownloadBlocked => _holdLedger.IsDownloadBlocked;
    public bool IsApplicationBlocked => _holdLedger.IsApplicationBlocked;
    public bool IsAutoPaused => _holdLedger.IsAutoPaused;
    public bool IsCrowdPriorityAutoPaused => _holdLedger.IsCrowdPriorityAutoPaused;
    public IEnumerable<string> AutoPauseReasons => _holdLedger.AutoPauseReasons;
    public bool AutoPauseNotificationShown => _holdLedger.AutoPauseNotificationShown;

    public IEnumerable<string> HoldDownloadReasons => _holdLedger.HoldDownloadReasons;
    public IEnumerable<string> HoldApplicationReasons => _holdLedger.HoldApplicationReasons;

    public bool IsVisible => CachedPlayer?.IsVisible ?? false;
    public CharacterData? LastReceivedCharacterData { get; set; }
    public string? PlayerName => GetPlayerName();
    public uint PlayerCharacterId => GetPlayerCharacterId();
    public long LastAppliedDataBytes => CachedPlayer?.LastAppliedDataBytes ?? -1;
    public long LastAppliedDataTris { get; set; } = -1;
    public long LastAppliedApproximateVRAMBytes { get; set; } = -1;
    public long? LastReportedTriangles { get; private set; }
    public long? LastReportedApproximateVRAMBytes { get; private set; }
    public long LastReceivedDataVersion { get; private set; }
    public string? LastPushedDataHash { get; private set; }
    private PairApplicationReceiptDto? LastApplicationReceipt { get; set; }
    public string? AutoPauseTooltip => _holdLedger.AutoPauseTooltip;
    public string Ident => _onlineUserIdentDto?.Ident ?? string.Empty;
    public PairAnalyzer? PairAnalyzer => CachedPlayer?.PairAnalyzer;

    public UserData UserData { get; private set; }

    public UserPairDto? UserPair { get; set; }

    private PairHandler? CachedPlayer { get; set; }
    
    public void UpdateUserData(UserData userData)
    {
        if (!string.Equals(UserData.UID, userData.UID, StringComparison.Ordinal))
        {
            var currentUid = UserData.UID;
            var requestedUid = userData.UID;
            _logger.LogWarning("Attempted to update user data for {currentUid} with mismatched UID {requestedUid}", currentUid, requestedUid);
            return;
        }

        UserData = userData;
        PairColour = Colour.HexToVector4(UserData.DisplayColour);
    }


    public void ApplyData(OnlineUserCharaDataDto data)
    {
        var scope = _applicationFlight.Begin();
        if (data.Delta != null)
        {
            if (!LastReceivedCharacterData.TryApplyDelta(data.Delta, LastReceivedDataVersion, out var reconstructedData))
            {
                scope.Dispose();
                _logger.LogDebug("Received delta for {uid} without base version {baseVersion}; requesting full data", data.User.UID, data.Delta.BaseVersion);
                Mediator.Publish(new RequestPairDataMessage(data.User));
                return;
            }

            LastReceivedCharacterData = reconstructedData;
            LastReceivedDataVersion = data.Delta.Version;
        }
        else
        {
            LastReceivedCharacterData = data.CharaData;
            LastReceivedDataVersion = data.DataVersion;
        }

        LastReportedApproximateVRAMBytes = data.ReportedVramBytes;
        LastReportedTriangles = data.ReportedTriangles;
        if (LastReceivedCharacterData != null)
        {
            _pairAppearanceCache.Store(UserData.UID, LastReceivedCharacterData, LastReceivedDataVersion);
        }

        ClearAutoPaused(AutoPauseReason.Vram);
        ClearAutoPaused(AutoPauseReason.Triangles);
        if (CachedPlayer == null)
        {
            _logger.LogDebug("Received Data for {uid} but CachedPlayer does not exist, waiting", data.User.UID);
            var readySignal = _cachedPlayerReadySignal;
            _ = _backgroundTasks.Run(async () =>
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, scope.Token);
                    await readySignal.Task.WaitAsync(combined.Token).ConfigureAwait(false);
                    _logger.LogDebug("Applying delayed data for {uid}", data.User.UID);
                    ApplyLastReceivedData();
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer apply (scope cancelled) or timed out waiting for the player.
                }
                finally
                {
                    scope.Dispose();
                }
            }, nameof(ApplyData));
            return;
        }

        scope.Dispose();
        ApplyLastReceivedData();
    }

    public void ApplyLastReceivedData(bool forced = false)
    {
        if (CachedPlayer == null) return;
        if (LastReceivedCharacterData == null) return;

        if (_blockListStore.IsUserBlacklisted(UserData))
            HoldApplication("Blacklist", maxValue: 1);

        if (IsApplicationBlocked) return;
        
        var perms = EffectivePermissionsResolver.Resolve(BuildDirectPermissions(), BuildGroupPermissionViews());
        var filter = new PairFilterContext(perms.Paused, perms.DisableAnimations, perms.DisableSounds, perms.DisableVFX,
            _blockListStore.IsUserWhitelisted(UserData));
        CachedPlayer.ApplyCharacterData(Guid.NewGuid(), LastReceivedCharacterData, forced, filter);
    }

    public void MarkLocalDataPushed(string dataHash)
    {
        if (string.IsNullOrWhiteSpace(dataHash))
        {
            return;
        }

        LastPushedDataHash = dataHash;
        if (LastApplicationReceipt != null
            && !string.Equals(LastApplicationReceipt.DataHash, dataHash, StringComparison.Ordinal))
        {
            LastApplicationReceipt = null;
        }
    }

    public void ApplyApplicationReceipt(PairApplicationReceiptDto receipt)
    {
        if (!string.Equals(receipt.Receiver.UID, UserData.UID, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrEmpty(LastPushedDataHash)
            && !string.Equals(receipt.DataHash, LastPushedDataHash, StringComparison.Ordinal))
        {
            return;
        }

        LastApplicationReceipt = receipt;
    }

    public void CreateCachedPlayer(OnlineUserIdentDto? dto = null)
    {
        var applyCachedData = false;
        var requestLiveData = false;
        try
        {
            _creationSemaphore.Wait();

            if (CachedPlayer != null) return;

            if (dto == null && _onlineUserIdentDto == null)
            {
                CachedPlayer?.Dispose();
                CachedPlayer = null;
                return;
            }
            if (dto != null)
            {
                _onlineUserIdentDto = dto;
            }

            CachedPlayer?.Dispose();
            CachedPlayer = _cachedPlayerFactory.Create(this);
            _cachedPlayerReadySignal.TrySetResult();

            if (LastReceivedCharacterData == null && _pairAppearanceCache.TryGet(UserData.UID, out var cachedAppearance))
            {
                LastReceivedCharacterData = cachedAppearance.CharacterData;
                LastReceivedDataVersion = cachedAppearance.DataVersion;
                applyCachedData = true;
            }

            requestLiveData = true;
        }
        finally
        {
            _creationSemaphore.Release();
        }

        if (applyCachedData)
        {
            _logger.LogDebug("Applying cached appearance for {uid} while requesting live data", UserData.UID);
            ApplyLastReceivedData(forced: true);
        }

        if (requestLiveData)
        {
            Mediator.Publish(new RequestPairDataMessage(UserData));
        }
    }

    public string? GetNote()
    {
        return _notesStore.GetNoteForUid(UserData.UID);
    }

    public string? GetPlayerName()
    {
        if (CachedPlayer != null && CachedPlayer.PlayerName != null)
            return CachedPlayer.PlayerName;
        else
            return _notesStore.GetNameForUid(UserData.UID);
    }

    public uint GetPlayerCharacterId()
    {
        if (CachedPlayer != null)
            return CachedPlayer.PlayerCharacterId;
        return uint.MaxValue;
    }

    public string? GetNoteOrName()
    {
        string? note = GetNote();
        if (_snowcloakConfig.Current.ShowCharacterNames || IsVisible)
            return note ?? GetPlayerName();
        else
            return note;
    }

    public string GetPairSortKey()
    {
        string? noteOrName = GetNoteOrName();

        if (_snowcloakConfig.Current.SortSyncshellsByVRAM)
        {
            var vramForSort = LastAppliedApproximateVRAMBytes < 0 ? 0 : LastAppliedApproximateVRAMBytes;
            return $"0{vramForSort:D20}";
        }
        else if (noteOrName != null) {
            return $"0{noteOrName}";
        }
        else {
            return $"9{UserData.AliasOrUID}";
        }
        }

    public string GetPlayerNameHash()
    {
        return CachedPlayer?.PlayerNameHash ?? string.Empty;
    }

    public bool HasAnyConnection()
    {
        return UserPair != null || GroupPair.Any();
    }

    public void MarkOffline(bool wait = true)
    {
        try
        {
            if (wait)
                _creationSemaphore.Wait();
            LastReceivedCharacterData = null;
            var player = CachedPlayer;
            CachedPlayer = null;
            player?.Dispose();
            _onlineUserIdentDto = null;
            if (player != null)
                _cachedPlayerReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        finally
        {
            if (wait)
                _creationSemaphore.Release();
        }
    }

    public async ValueTask MarkOfflineAsync()
    {
        PairHandler? player = null;

        try
        {
            await _creationSemaphore.WaitAsync().ConfigureAwait(false);
            LastReceivedCharacterData = null;
            player = CachedPlayer;
            CachedPlayer = null;
            _onlineUserIdentDto = null;
            if (player != null)
                _cachedPlayerReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        finally
        {
            _creationSemaphore.Release();
        }

        if (player != null)
        {
            await player.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void SetNote(string note)
    {
        _notesStore.SetNoteForUid(UserData.UID, note);
    }

    internal void SetIsUploading()
    {
        CachedPlayer?.SetUploading();
    }

    public bool HoldApplication(string source, int maxValue = int.MaxValue)
    {
        _logger.LogDebug("Holding {Uid} for reason: {Source}", UserData.UID, source);
        var becameBlocked = _holdLedger.HoldApplication(source, maxValue);
        if (becameBlocked)
            CachedPlayer?.UndoApplication();
        return becameBlocked;
    }

    public void UnholdApplication(string source, bool skipApplication = false)
    {
        _logger.LogDebug("Un-holding {Uid} for reason: {Source}", UserData.UID, source);
        if (_holdLedger.UnholdApplication(source) && !skipApplication)
            ApplyLastReceivedData(forced: true);
    }

    public void HoldDownloads(string source, int maxValue = int.MaxValue)
    {
        _logger.LogDebug("Holding {Uid} for reason: {Source}", UserData.UID, source);
        if (_holdLedger.HoldDownloads(source, maxValue))
            CachedPlayer?.UndoApplication();
    }

    public void UnholdDownloads(string source, bool skipApplication = false)
    {
        _logger.LogDebug("Un-holding {Uid} for reason: {Source}", UserData.UID, source);
        if (_holdLedger.UnholdDownloads(source) && !skipApplication)
            ApplyLastReceivedData(forced: true);
    }

    public void UndoApplication()
    {
        CachedPlayer?.UndoApplication();
    }

    public void MarkAutoPauseNotificationShown()
    {
        _holdLedger.MarkAutoPauseNotificationShown();
    }

    public bool HasBlockingReasonsOtherThanCrowdPriority()
    {
        return _holdLedger.HasBlockingReasonsOtherThanCrowdPriority();
    }

    public bool HasAutoPauseReason(AutoPauseReason reason)
    {
        return _holdLedger.HasAutoPauseReason(reason);
    }

    public void SetAutoPaused(AutoPauseReason reason, string tooltip)
    {
        var wasAutoPaused = _holdLedger.HasAutoPauseReason(reason);
        if (_holdLedger.SetAutoPause(reason, tooltip))
            CachedPlayer?.UndoApplication();

        if (!wasAutoPaused)
            _logger.LogDebug("Auto-paused {Uid} for {Reason}", UserData.UID, reason);
    }

    public void ClearAutoPaused(AutoPauseReason? reason = null)
    {
        _holdLedger.ClearAutoPause(reason);
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            base.Dispose(disposing);
            _backgroundTasks.StopAccepting();
            _applicationFlight.Cancel();
            MarkOffline();
            _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(2), nameof(Pair));
        }
        finally
        {
            DisposeOwnedResources();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            base.Dispose(disposing: true);
            _backgroundTasks.StopAccepting();
            _applicationFlight.Cancel();
            await MarkOfflineAsync().ConfigureAwait(false);
            await _backgroundTasks.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            DisposeOwnedResources();
            GC.SuppressFinalize(this);
        }
    }

    private void DisposeOwnedResources()
    {
        _applicationFlight.Dispose();
        _creationSemaphore.Dispose();
    }
}
