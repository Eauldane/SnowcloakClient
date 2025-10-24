using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using MareSynchronos.API.Data;
using MareSynchronos.API.Data.Comparer;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.API.Dto.Group;
using MareSynchronos.API.Dto.User;
using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Factories;
using MareSynchronos.PlayerData.Handlers;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Numerics;

namespace MareSynchronos.PlayerData.Pairs;

public class Pair : DisposableMediatorSubscriberBase
{
    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly SemaphoreSlim _creationSemaphore = new(1);
    private readonly ILogger<Pair> _logger;
    private readonly MareConfigService _mareConfig;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private CancellationTokenSource _applicationCts = new();
    private OnlineUserIdentDto? _onlineUserIdentDto = null;
    public Vector4 PairColour;

    public Pair(ILogger<Pair> logger, UserData userData, PairHandlerFactory cachedPlayerFactory,
        MareMediator mediator, MareConfigService mareConfig, ServerConfigurationManager serverConfigurationManager)
        : base(logger, mediator)
    {
        _logger = logger;
        _cachedPlayerFactory = cachedPlayerFactory;
        _mareConfig = mareConfig;
        _serverConfigurationManager = serverConfigurationManager;

        UserData = userData;
        PairColour = SnowcloakSync.Utils.Colours.Hex2Vector4(UserData.DisplayColour);

        Mediator.SubscribeKeyed<HoldPairApplicationMessage>(this, UserData.UID, (msg) => HoldApplication(msg.Source));
        Mediator.SubscribeKeyed<UnholdPairApplicationMessage>(this, UserData.UID, (msg) => UnholdApplication(msg.Source));
    }

    public Dictionary<GroupFullInfoDto, GroupPairFullInfoDto> GroupPair { get; set; } = new(GroupDtoComparer.Instance);
    public bool HasCachedPlayer => CachedPlayer != null && !string.IsNullOrEmpty(CachedPlayer.PlayerName) && _onlineUserIdentDto != null;
    public bool IsOnline => CachedPlayer != null;

    public bool IsPaused => UserPair != null && UserPair.OtherPermissions.IsPaired() ? UserPair.OtherPermissions.IsPaused() || UserPair.OwnPermissions.IsPaused()
            : GroupPair.All(p => p.Key.GroupUserPermissions.IsPaused() || p.Value.GroupUserPermissions.IsPaused());

    // Download locks apply earlier in the process than Application locks
    private ConcurrentDictionary<string, int> HoldDownloadLocks { get; set; } = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, int> HoldApplicationLocks { get; set; } = new(StringComparer.Ordinal);

    public bool IsDownloadBlocked => HoldDownloadLocks.Any(f => f.Value > 0);
    public bool IsApplicationBlocked => HoldApplicationLocks.Any(f => f.Value > 0) || IsDownloadBlocked;

    public IEnumerable<string> HoldDownloadReasons => HoldDownloadLocks.Keys;
    public IEnumerable<string> HoldApplicationReasons => Enumerable.Concat(HoldDownloadLocks.Keys, HoldApplicationLocks.Keys);

    public bool IsVisible => CachedPlayer?.IsVisible ?? false;
    public CharacterData? LastReceivedCharacterData { get; set; }
    public string? PlayerName => GetPlayerName();
    public uint PlayerCharacterId => GetPlayerCharacterId();
    public long LastAppliedDataBytes => CachedPlayer?.LastAppliedDataBytes ?? -1;
    public long LastAppliedDataTris { get; set; } = -1;
    public long LastAppliedApproximateVRAMBytes { get; set; } = -1;
    public string Ident => _onlineUserIdentDto?.Ident ?? string.Empty;
    public PairAnalyzer? PairAnalyzer => CachedPlayer?.PairAnalyzer;

    public UserData UserData { get; init; }

    public UserPairDto? UserPair { get; set; }

    private PairHandler? CachedPlayer { get; set; }

    public void AddContextMenu(IMenuOpenedArgs args)
    {
        if (CachedPlayer == null || (args.Target is not MenuTargetDefault target) || target.TargetObjectId != CachedPlayer.PlayerCharacterId || IsPaused) return;

        void Add(string name, Action<IMenuItemClickedArgs>? action)
        {
            args.AddMenuItem(new MenuItem()
            {
                Name = name,
                OnClicked = action,
                PrefixColor = 526,
                PrefixChar = 'S'
            });
        }

        bool isBlocked = IsApplicationBlocked;
        bool isBlacklisted = _serverConfigurationManager.IsUidBlacklisted(UserData.UID);
        bool isWhitelisted = _serverConfigurationManager.IsUidWhitelisted(UserData.UID);

        Add("Open Profile", _ => Mediator.Publish(new ProfileOpenStandaloneMessage(this)));

        if (!isBlocked && !isBlacklisted)
            Add("Always Block Modded Appearance", _ => {
                    _serverConfigurationManager.AddBlacklistUid(UserData.UID);
                    HoldApplication("Blacklist", maxValue: 1);
                    ApplyLastReceivedData(forced: true);
                });
        else if (isBlocked && !isWhitelisted)
            Add("Always Allow Modded Appearance", _ => {
                    _serverConfigurationManager.AddWhitelistUid(UserData.UID);
                    UnholdApplication("Blacklist", skipApplication: true);
                    ApplyLastReceivedData(forced: true);
                });

        if (isWhitelisted)
            Add("Remove from Whitelist", _ => {
                _serverConfigurationManager.RemoveWhitelistUid(UserData.UID);
                ApplyLastReceivedData(forced: true);
            });
        else if (isBlacklisted)
            Add("Remove from Blacklist", _ => {
                _serverConfigurationManager.RemoveBlacklistUid(UserData.UID);
                UnholdApplication("Blacklist", skipApplication: true);
                ApplyLastReceivedData(forced: true);
            });

        Add("Reapply last data", _ => ApplyLastReceivedData(forced: true));

        if (UserPair != null)
        {
            Add("Change Permissions", _ => Mediator.Publish(new OpenPermissionWindow(this)));
            Add("Cycle pause state", _ => Mediator.Publish(new CyclePauseMessage(UserData)));
        }
    }

    public void ApplyData(OnlineUserCharaDataDto data)
    {
        _applicationCts = _applicationCts.CancelRecreate();
        LastReceivedCharacterData = data.CharaData;

        if (CachedPlayer == null)
        {
            _logger.LogDebug("Received Data for {uid} but CachedPlayer does not exist, waiting", data.User.UID);
            _ = Task.Run(async () =>
            {
                using var timeoutCts = new CancellationTokenSource();
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
                var appToken = _applicationCts.Token;
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, appToken);
                while (CachedPlayer == null && !combined.Token.IsCancellationRequested)
                {
                    await Task.Delay(250, combined.Token).ConfigureAwait(false);
                }

                if (!combined.IsCancellationRequested)
                {
                    _logger.LogDebug("Applying delayed data for {uid}", data.User.UID);
                    ApplyLastReceivedData();
                }
            });
            return;
        }

        ApplyLastReceivedData();
    }

    public void ApplyLastReceivedData(bool forced = false)
    {
        if (CachedPlayer == null) return;
        if (LastReceivedCharacterData == null) return;
        if (IsDownloadBlocked) return;

        if (_serverConfigurationManager.IsUidBlacklisted(UserData.UID))
            HoldApplication("Blacklist", maxValue: 1);

        CachedPlayer.ApplyCharacterData(Guid.NewGuid(), RemoveNotSyncedFiles(LastReceivedCharacterData.DeepClone())!, forced);
    }

    public void CreateCachedPlayer(OnlineUserIdentDto? dto = null)
    {
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
        }
        finally
        {
            _creationSemaphore.Release();
        }
    }

    public string? GetNote()
    {
        return _serverConfigurationManager.GetNoteForUid(UserData.UID);
    }

    public string? GetPlayerName()
    {
        if (CachedPlayer != null && CachedPlayer.PlayerName != null)
            return CachedPlayer.PlayerName;
        else
            return _serverConfigurationManager.GetNameForUid(UserData.UID);
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
        if (_mareConfig.Current.ShowCharacterNames || IsVisible)
            return note ?? GetPlayerName();
        else
            return note;
    }

    public string GetPairSortKey()
    {
        string? noteOrName = GetNoteOrName();

        if (_mareConfig.Current.SortSyncshellsByVRAM)
        { 
            return($"0{LastAppliedApproximateVRAMBytes}");
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
        }
        finally
        {
            if (wait)
                _creationSemaphore.Release();
        }
    }

    public void SetNote(string note)
    {
        _serverConfigurationManager.SetNoteForUid(UserData.UID, note);
    }

    internal void SetIsUploading()
    {
        CachedPlayer?.SetUploading();
    }

    public void HoldApplication(string source, int maxValue = int.MaxValue)
    {
        _logger.LogDebug($"Holding {UserData.UID} for reason: {source}");
        bool wasHeld = IsApplicationBlocked;
        HoldApplicationLocks.AddOrUpdate(source, 1, (k, v) => Math.Min(maxValue, v + 1));
        if (!wasHeld)
            CachedPlayer?.UndoApplication();
    }

    public void UnholdApplication(string source, bool skipApplication = false)
    {
        _logger.LogDebug($"Un-holding {UserData.UID} for reason: {source}");
        bool wasHeld = IsApplicationBlocked;
        HoldApplicationLocks.AddOrUpdate(source, 0, (k, v) => Math.Max(0, v - 1));
        HoldApplicationLocks.TryRemove(new(source, 0));
        if (!skipApplication && wasHeld && !IsApplicationBlocked)
            ApplyLastReceivedData(forced: true);
    }

    public void HoldDownloads(string source, int maxValue = int.MaxValue)
    {
        _logger.LogDebug($"Holding {UserData.UID} for reason: {source}");
        bool wasHeld = IsApplicationBlocked;
        HoldDownloadLocks.AddOrUpdate(source, 1, (k, v) => Math.Min(maxValue, v + 1));
        if (!wasHeld)
            CachedPlayer?.UndoApplication();
    }

    public void UnholdDownloads(string source, bool skipApplication = false)
    {
        _logger.LogDebug($"Un-holding {UserData.UID} for reason: {source}");
        bool wasHeld = IsApplicationBlocked;
        HoldDownloadLocks.AddOrUpdate(source, 0, (k, v) => Math.Max(0, v - 1));
        HoldDownloadLocks.TryRemove(new(source, 0));
        if (!skipApplication && wasHeld && !IsApplicationBlocked)
            ApplyLastReceivedData(forced: true);
    }

    private CharacterData? RemoveNotSyncedFiles(CharacterData? data)
    {
        _logger.LogTrace("Removing not synced files");
        if (data == null)
        {
            _logger.LogTrace("Nothing to remove");
            return data;
        }
        if (IsPaused)
        {
            _logger.LogTrace("Skipping data removal for paused user {uid}", UserData.UID);
            return data;
        }

        var ActiveGroupPairs = GroupPair.Where(p => !p.Value.GroupUserPermissions.IsPaused() && !p.Key.GroupUserPermissions.IsPaused()).ToList();

        bool disableIndividualAnimations = UserPair != null && (UserPair.OtherPermissions.IsDisableAnimations() || UserPair.OwnPermissions.IsDisableAnimations());
        bool disableIndividualVFX = UserPair != null && (UserPair.OtherPermissions.IsDisableVFX() || UserPair.OwnPermissions.IsDisableVFX());
        bool disableGroupAnimations = ActiveGroupPairs.All(pair => pair.Value.GroupUserPermissions.IsDisableAnimations() || pair.Key.GroupPermissions.IsDisableAnimations() || pair.Key.GroupUserPermissions.IsDisableAnimations());

        bool disableAnimations = (UserPair != null && disableIndividualAnimations) || (UserPair == null && disableGroupAnimations);

        bool disableIndividualSounds = UserPair != null && (UserPair.OtherPermissions.IsDisableSounds() || UserPair.OwnPermissions.IsDisableSounds());
        bool disableGroupSounds = ActiveGroupPairs.All(pair => pair.Value.GroupUserPermissions.IsDisableSounds() || pair.Key.GroupPermissions.IsDisableSounds() || pair.Key.GroupUserPermissions.IsDisableSounds());
        bool disableGroupVFX = ActiveGroupPairs.All(pair => pair.Value.GroupUserPermissions.IsDisableVFX() || pair.Key.GroupPermissions.IsDisableVFX() || pair.Key.GroupUserPermissions.IsDisableVFX());

        bool disableSounds = (UserPair != null && disableIndividualSounds) || (UserPair == null && disableGroupSounds);
        bool disableVFX = (UserPair != null && disableIndividualVFX) || (UserPair == null && disableGroupVFX);

        _logger.LogTrace("Disable: Sounds: {disableSounds}, Anims: {disableAnimations}, VFX: {disableVFX}",
            disableSounds, disableAnimations, disableVFX);

        if (disableAnimations || disableSounds || disableVFX)
        {
            _logger.LogTrace("Data cleaned up: Animations disabled: {disableAnimations}, Sounds disabled: {disableSounds}, VFX disabled: {disableVFX}",
                disableAnimations, disableSounds, disableVFX);
            foreach (var objectKind in data.FileReplacements.Select(k => k.Key))
            {
                if (disableSounds)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("scd", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                if (disableAnimations)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("tmb", StringComparison.OrdinalIgnoreCase) || p.EndsWith("pap", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                if (disableVFX)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("atex", StringComparison.OrdinalIgnoreCase) || p.EndsWith("avfx", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
            }
        }

        return data;
    }
}