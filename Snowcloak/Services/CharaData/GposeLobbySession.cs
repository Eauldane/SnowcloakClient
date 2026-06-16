using ElezenTools.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.Core.CharaData;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.Interop;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Collections.Concurrent;

namespace Snowcloak.Services.CharaData;

public sealed class GposeLobbySession : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly CharaDataFileHandler _charaDataFileHandler;
    private readonly CharaDataManager _charaDataManager;
    private readonly GposePoseSync _poseSync;
    private readonly GposeWorldSync _worldSync;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly ConcurrentDictionary<string, GposeLobbyUserData> _usersInLobby = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _charaDataCreationSemaphore = new(1, 1);
    private readonly SemaphoreSlim _charaDataSpawnSemaphore = new(1, 1);
    private readonly IFrameTickHandle _tick;
    private readonly IFrameTickHandle _cutsceneTick;
    private (CharacterData ApiData, CharaDataDownloadDto Dto)? _lastCreatedCharaData;
    private CancellationTokenSource? _lobbyCts;
    private CancellationToken _lobbyToken = new(canceled: true);
    private int _disposed;

    public GposeLobbySession(ILogger<GposeLobbySession> logger, SnowMediator mediator,
        ApiController apiController, IpcCallerBrio brio, DalamudUtilService dalamudUtil, VfxSpawnManager vfxSpawnManager,
        CharaDataFileHandler charaDataFileHandler, CharaDataManager charaDataManager, IFrameScheduler frameScheduler) : base(logger, mediator)
    {
        _apiController = apiController;
        _dalamudUtil = dalamudUtil;
        _charaDataFileHandler = charaDataFileHandler;
        _charaDataManager = charaDataManager;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _poseSync = new GposePoseSync(logger, this, brio, dalamudUtil, apiController);
        _worldSync = new GposeWorldSync(logger, this, brio, dalamudUtil, apiController, vfxSpawnManager);

        _tick = frameScheduler.Register("GposeLobbySession", TickInterval.EveryFrame, TickPriority.Low, OnFrameworkUpdate,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
        _cutsceneTick = frameScheduler.RegisterGated("GposeLobbySessionCutscene", TickInterval.EveryFrame, TickPriority.Low, OnCutsceneFrameworkUpdate,
            [FrameGates.Dead], [FrameGates.Cutscene]);

        Mediator.Subscribe<GposeLobbyUserJoin>(this, msg => OnUserJoinLobby(msg.UserData));
        Mediator.Subscribe<GPoseLobbyUserLeave>(this, msg => OnUserLeaveLobby(msg.UserData));
        Mediator.Subscribe<GPoseLobbyReceiveCharaData>(this, msg => OnReceiveCharaData(msg.CharaDataDownloadDto));
        Mediator.Subscribe<GPoseLobbyReceivePoseData>(this, msg => _poseSync.OnReceivePose(msg.UserData, msg.PoseData));
        Mediator.Subscribe<GPoseLobbyReceiveWorldData>(this, msg => _worldSync.OnReceiveWorld(msg.UserData, msg.WorldData));
        Mediator.Subscribe<ConnectedMessage>(this, _ => OnConnected());
        Mediator.Subscribe<GposeStartMessage>(this, _ => OnEnterGpose());
        Mediator.Subscribe<GposeEndMessage>(this, _ => OnExitGpose());
    }

    public string? CurrentGPoseLobbyId { get; private set; }
    public string? LastGPoseLobbyId { get; private set; }

    public IEnumerable<GposeLobbyUserData> UsersInLobby => _usersInLobby.Values;

    internal IEnumerable<GposeLobbyUserData> Members => _usersInLobby.Values;
    internal bool IsInLobby => !string.IsNullOrEmpty(CurrentGPoseLobbyId);
    internal bool HasMembers => !_usersInLobby.IsEmpty;
    internal bool TryGetUser(string uid, out GposeLobbyUserData member) => _usersInLobby.TryGetValue(uid, out member!);

    public GposeLocationMatch IsOnSameMapAndServer(GposeLobbyUserData member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return GposeLocationComparison.Compare(member.WorldData?.LocationInfo, _worldSync.OwnWorldData?.LocationInfo);
    }

    public void CreateNewLobby()
    {
        _ = _backgroundTasks.Run(async () =>
        {
            ClearLobby();
            var id = await _apiController.GposeLobbyCreate().ConfigureAwait(false);
            CurrentGPoseLobbyId = id;
            if (!string.IsNullOrEmpty(id))
                BeginLobby();
        }, nameof(CreateNewLobby));
    }

    public void JoinGPoseLobby(string joinLobbyId)
    {
        _ = _backgroundTasks.Run(async () =>
        {
            var others = await _apiController.GposeLobbyJoin(joinLobbyId).ConfigureAwait(false);
            ClearLobby();
            if (others.Count > 0)
            {
                CurrentGPoseLobbyId = joinLobbyId;
                BeginLobby();
                LastGPoseLobbyId = string.Empty;
                foreach (var user in others)
                    AddOrReplaceMember(user);
                ForceResendOwnData();
                await PushCharacterDownloadDto().ConfigureAwait(false);
            }
            else
            {
                LastGPoseLobbyId = string.Empty;
            }
        }, nameof(JoinGPoseLobby));
    }

    public void LeaveGPoseLobby()
    {
        _ = _backgroundTasks.Run(LeaveGPoseLobbyAsync, nameof(LeaveGPoseLobby));
    }

    public async Task PushCharacterDownloadDto()
    {
        var dto = await BuildOwnCharaData().ConfigureAwait(false);
        if (dto == null) return;

        ForceResendOwnData();
        await _apiController.GposeLobbyPushCharacterData(dto).ConfigureAwait(false);
    }

    public async Task ApplyCharaData(GposeLobbyUserData member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.CharaData == null || member.Address == nint.Zero || string.IsNullOrEmpty(member.AssociatedCharaName))
            return;

        await _charaDataCreationSemaphore.WaitAsync(_lobbyToken).ConfigureAwait(false);
        try
        {
            await _charaDataManager.ApplyCharaData(member.CharaData!, member.AssociatedCharaName).ConfigureAwait(false);
            member.LastAppliedCharaDataDate = member.CharaData.UpdatedDate;
            member.HasPoseDataUpdate = true;
            member.HasWorldDataUpdate = true;
        }
        finally
        {
            _charaDataCreationSemaphore.Release();
        }
    }

    public async Task SpawnAndApplyData(GposeLobbyUserData member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var charaData = member.CharaData;
        if (charaData == null)
            return;

        await _charaDataSpawnSemaphore.WaitAsync(_lobbyToken).ConfigureAwait(false);
        try
        {
            member.HasPoseDataUpdate = false;
            member.HasWorldDataUpdate = false;
            var chara = await _charaDataManager.SpawnAndApplyData(charaData).ConfigureAwait(false);
            if (chara == null) return;
            member.HandledChara = chara;
            member.AssociatedCharaName = chara.Name;
            member.LastAppliedCharaDataDate = charaData.UpdatedDate;
            member.HasPoseDataUpdate = true;
            member.HasWorldDataUpdate = true;
        }
        finally
        {
            _charaDataSpawnSemaphore.Release();
        }
    }

    private async Task LeaveGPoseLobbyAsync()
    {
        var left = await _apiController.GposeLobbyLeave().ConfigureAwait(false);
        if (!left) return;

        if (!string.IsNullOrEmpty(CurrentGPoseLobbyId))
            LastGPoseLobbyId = CurrentGPoseLobbyId;

        ClearLobby(revertCharas: true);
    }

    private void OnConnected()
    {
        if (string.IsNullOrEmpty(CurrentGPoseLobbyId))
            return;

        RejoinGPoseLobby(CurrentGPoseLobbyId);
    }

    private void RejoinGPoseLobby(string lobbyId)
    {
        _ = _backgroundTasks.Run(async () =>
        {
            var members = await _apiController.GposeLobbyRejoin(lobbyId).ConfigureAwait(false);
            if (members.Count == 0)
            {
                JoinGPoseLobby(lobbyId);
                return;
            }

            BeginLobby();
            ReconcileMembers(members);
            ForceResendOwnData();
            await PushCharacterDownloadDto().ConfigureAwait(false);
        }, nameof(RejoinGPoseLobby));
    }

    private void ReconcileMembers(IReadOnlyList<UserData> members)
    {
        var incoming = new HashSet<string>(members.Select(u => u.UID), StringComparer.Ordinal);

        foreach (var uid in _usersInLobby.Keys.ToList())
        {
            if (incoming.Contains(uid)) continue;
            if (_usersInLobby.TryRemove(uid, out var gone))
            {
                _charaDataManager.RevertChara(gone.HandledChara);
                _worldSync.DespawnWisp(gone);
            }
        }

        foreach (var user in members)
        {
            if (string.Equals(user.UID, _apiController.UID, StringComparison.Ordinal)) continue;
            _usersInLobby.TryAdd(user.UID, new GposeLobbyUserData(user));
        }
    }

    private async Task<CharaDataDownloadDto?> BuildOwnCharaData()
    {
        var playerData = await _charaDataFileHandler.CreatePlayerData().ConfigureAwait(false);
        if (playerData == null) return null;

        if (!string.Equals(playerData.DataHash.Value, _lastCreatedCharaData?.ApiData.DataHash.Value, StringComparison.Ordinal))
        {
            var fileGamePaths = ProjectGamePaths(playerData, swaps: false);
            var fileSwapPaths = ProjectGamePaths(playerData, swaps: true);

            var (result, success) = await _charaDataManager.UploadFiles(fileGamePaths).ConfigureAwait(false);
            if (!success)
            {
                Logger.LogWarning("GPose appearance upload failed, not announcing: {result}", result);
                return null;
            }

            var dto = new CharaDataDownloadDto($"GPOSELOBBY:{CurrentGPoseLobbyId}", new UserData(_apiController.UID))
            {
                UpdatedDate = DateTime.UtcNow,
                ManipulationData = playerData.ManipulationData,
                CustomizeData = playerData.CustomizePlusData[Snowcloak.API.Data.Enum.ObjectKind.Player],
                FileGamePaths = fileGamePaths,
                FileSwaps = fileSwapPaths,
                GlamourerData = playerData.GlamourerData[Snowcloak.API.Data.Enum.ObjectKind.Player],
            };

            _lastCreatedCharaData = (playerData, dto);
        }

        return _lastCreatedCharaData?.Dto;
    }

    private static List<GamePathEntry> ProjectGamePaths(CharacterData playerData, bool swaps)
    {
        var replacements = playerData.FileReplacements[Snowcloak.API.Data.Enum.ObjectKind.Player];
        if (swaps)
        {
            return [.. replacements.Where(u => !string.IsNullOrEmpty(u.FileSwapPath))
                .SelectMany(u => u.GamePaths, (file, path) => new GamePathEntry(file.FileSwapPath, path))];
        }

        return [.. replacements.Where(u => string.IsNullOrEmpty(u.FileSwapPath))
            .SelectMany(u => u.GamePaths, (file, path) => new GamePathEntry(file.Hash, path))];
    }

    private void OnReceiveCharaData(CharaDataDownloadDto charaDataDownloadDto)
    {
        if (!_usersInLobby.TryGetValue(charaDataDownloadDto.Uploader.UID, out var member))
            return;

        member.CharaData = charaDataDownloadDto;
        if (member.Address != nint.Zero && !string.IsNullOrEmpty(member.AssociatedCharaName)
            && member.LastUpdatedCharaData > member.LastAppliedCharaDataDate)
            _ = ApplyCharaData(member);
    }

    private void OnUserJoinLobby(UserData userData)
    {
        AddOrReplaceMember(userData);
        _ = _backgroundTasks.Run(() => PushOwnDataToJoiner(userData.UID), nameof(PushOwnDataToJoiner));
    }

    private void OnUserLeaveLobby(UserData userData)
    {
        if (_usersInLobby.TryRemove(userData.UID, out var existing))
            _worldSync.DespawnWisp(existing);
    }

    private async Task PushOwnDataToJoiner(string targetUid)
    {
        var dto = await BuildOwnCharaData().ConfigureAwait(false);
        if (dto == null) return;

        ForceResendOwnData();
        await _apiController.GposeLobbyPushCharacterDataTo(targetUid, dto).ConfigureAwait(false);
    }

    private void AddOrReplaceMember(UserData userData)
    {
        if (_usersInLobby.TryRemove(userData.UID, out var existing))
            _worldSync.DespawnWisp(existing);

        _usersInLobby[userData.UID] = new GposeLobbyUserData(userData);
    }

    private void ForceResendOwnData()
    {
        _poseSync.ForceResend();
        _worldSync.ForceResend();
    }

    private void OnEnterGpose()
    {
        _lastCreatedCharaData = null;
        ForceResendOwnData();
        _worldSync.DespawnAllWisps();
        foreach (var member in Members)
            member.Reset();
    }

    private void OnExitGpose()
    {
        _lastCreatedCharaData = null;
        ForceResendOwnData();
        foreach (var member in Members)
            member.Reset();
    }

    private void OnFrameworkUpdate()
    {
        _worldSync.UpdateWisps(DateTime.UtcNow);
    }

    private void OnCutsceneFrameworkUpdate()
    {
        foreach (var member in Members)
        {
            if (!string.IsNullOrWhiteSpace(member.AssociatedCharaName))
            {
                member.Address = _dalamudUtil.GetGposeCharacterFromObjectTableByName(member.AssociatedCharaName, true)?.Address ?? nint.Zero;
                if (member.Address == nint.Zero)
                    member.AssociatedCharaName = string.Empty;
            }

            if (member.Address == nint.Zero || (!member.HasWorldDataUpdate && !member.HasPoseDataUpdate))
                continue;

            var hadPoseDataUpdate = member.HasPoseDataUpdate;
            var hadWorldDataUpdate = member.HasWorldDataUpdate;
            member.HasPoseDataUpdate = false;
            member.HasWorldDataUpdate = false;

            _ = _backgroundTasks.Run(async ct =>
            {
                ct.ThrowIfCancellationRequested();
                if (hadPoseDataUpdate)
                    await _poseSync.ApplyPose(member).ConfigureAwait(false);
                if (hadWorldDataUpdate)
                    await _worldSync.ApplyWorld(member).ConfigureAwait(false);
            }, nameof(OnCutsceneFrameworkUpdate), _lobbyToken);
        }
    }

    private void BeginLobby()
    {
        EndLobby();
        _lobbyCts = new CancellationTokenSource();
        _lobbyToken = _lobbyCts.Token;
    }

    private void EndLobby()
    {
        if (_lobbyCts != null)
        {
            _lobbyCts.Cancel();
            _lobbyCts.Dispose();
            _lobbyCts = null;
        }

        _lobbyToken = new CancellationToken(canceled: true);
    }

    private void ClearLobby(bool revertCharas = false)
    {
        EndLobby();
        ClearLobbyState(revertCharas);
    }

    private void ClearLobbyState(bool revertCharas)
    {
        CurrentGPoseLobbyId = string.Empty;
        foreach (var member in _usersInLobby.Values)
        {
            if (revertCharas)
                _charaDataManager.RevertChara(member.HandledChara);
            _worldSync.DespawnWisp(member);
        }

        _usersInLobby.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _tick.Dispose();
        _cutsceneTick.Dispose();
        base.Dispose(disposing);
        if (disposing)
        {
            EndLobby();
            ClearLobbyState(revertCharas: true);
            _backgroundTasks.StopAccepting();
            _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(2), nameof(GposeLobbySession));
            _poseSync.Dispose();
            _worldSync.Dispose();
            DisposeOwnedResources();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _tick.Dispose();
        _cutsceneTick.Dispose();
        base.Dispose(disposing: true);
        EndLobby();
        ClearLobbyState(revertCharas: true);
        await _backgroundTasks.StopAsync().ConfigureAwait(false);
        _poseSync.Dispose();
        _worldSync.Dispose();
        DisposeOwnedResources();
        GC.SuppressFinalize(this);
    }

    private void DisposeOwnedResources()
    {
        _charaDataCreationSemaphore.Dispose();
        _charaDataSpawnSemaphore.Dispose();
    }
}
