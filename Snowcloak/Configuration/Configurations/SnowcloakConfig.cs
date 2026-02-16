using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.Configuration.Models;
using Snowcloak.FileCache;
using Snowcloak.UI;
using ElezenTools.UI;
namespace Snowcloak.Configuration.Configurations;

[Serializable]
public class SnowcloakConfig : ISnowcloakConfiguration
{
    public bool SortSyncshellsByVRAM { get; set; } = false;
    public int ExpectedTOSVersion = 2;
    public int AcceptedTOSVersion { get; set; } = 0;
    public bool AcceptedAgreement { get; set; } = false;
    public string CacheFolder { get; set; } = string.Empty;
    public bool DisableOptionalPluginWarnings { get; set; } = false;
    public bool EnableDtrEntry { get; set; } = true;
    public int DtrStyle { get; set; } = 0;
    public bool ShowUidInDtrTooltip { get; set; } = true;
    public bool PreferNoteInDtrTooltip { get; set; } = false;
    public bool UseColorsInDtr { get; set; } = true;
    public ElezenStrings.Colour DtrColorsDefault { get; set; } = default;
    public ElezenStrings.Colour DtrColorsNotConnected { get; set; } = new(Glow: 0x0428FFu);
    public ElezenStrings.Colour DtrColorsPairsInRange { get; set; } = new(Glow: 0xFFBA47u);
    public ElezenStrings.Colour DtrColorsPendingRequests { get; set; } = new(Foreground: 0xFFB790u, Glow: 0xFF6347u);
    public bool UseNameColors { get; set; } = false;
    public ElezenStrings.Colour NameColors { get; set; } = new(Foreground: 0x67EBF5u, Glow: 0x00303Cu);
    public ElezenStrings.Colour BlockedNameColors { get; set; } = new(Foreground: 0x8AADC7, Glow: 0x000080u);
    public bool EnableRightClickMenus { get; set; } = true;
    public NotificationLocation ErrorNotification { get; set; } = NotificationLocation.Both;
    public string ExportFolder { get; set; } = string.Empty;
    public bool FileScanPaused { get; set; } = false;
    public NotificationLocation InfoNotification { get; set; } = NotificationLocation.Toast;
    public bool InitialScanComplete { get; set; } = false;
    public LogLevel LogLevel { get; set; } = LogLevel.Information;
    public bool LogPerformance { get; set; } = false;
    public bool LogEvents { get; set; } = true;
    public bool HoldCombatApplication { get; set; } = false;
    public double MaxLocalCacheInGiB { get; set; } = 100;
    public bool OpenGposeImportOnGposeStart { get; set; } = false;
    public bool OpenPopupOnAdd { get; set; } = true;
    public int ParallelDownloads { get; set; } = 10;
    public int DownloadSpeedLimitInBytes { get; set; } = 0;
    public DownloadSpeeds DownloadSpeedType { get; set; } = DownloadSpeeds.MBps;
    public float ProfileDelay { get; set; } = 1.5f;
    public bool ProfilePopoutRight { get; set; } = false;
    public bool ProfilesAllowNsfw { get; set; } = false;
    public bool ProfilesShow { get; set; } = false;
    public bool ShowSyncshellUsersInVisible { get; set; } = true;
    public bool ShowCharacterNames { get; set; } = true;
    public bool ShowOfflineUsersSeparately { get; set; } = true;
    public bool ShowSyncshellOfflineUsersSeparately { get; set; } = true;
    public bool GroupUpSyncshells { get; set; } = true;
    public bool ShowOnlineNotifications { get; set; } = false;
    public bool ShowOnlineNotificationsOnlyForIndividualPairs { get; set; } = true;
    public bool ShowOnlineNotificationsOnlyForNamedPairs { get; set; } = false;
    public bool ShowTransferBars { get; set; } = true;
    public bool ShowTransferWindow { get; set; } = false;
    public bool ShowUploading { get; set; } = true;
    public bool ShowUploadingBigText { get; set; } = true;
    public bool ShowVisibleUsersSeparately { get; set; } = true;
    public int TimeSpanBetweenScansInSeconds { get; set; } = 30;
    public int TransferBarsHeight { get; set; } = 12;
    public bool TransferBarsShowText { get; set; } = true;
    public int TransferBarsWidth { get; set; } = 250;
    public bool UseAlternativeFileUpload { get; set; } = false;
    public bool UseCompactor { get; set; } = false;
    public int Version { get; set; } = 1;
    public NotificationLocation WarningNotification { get; set; } = NotificationLocation.Both;

    public bool DisableChat { get; set; } = false;
    public int ChatColor { get; set; } = 0; // 0 means "use plugin default"
    public int ChatLogKind { get; set; } = 1; // XivChatType.Debug
    public bool ExtraChatAPI { get; set; } = false;
    public bool ExtraChatTags { get; set; } = false;
    public bool DisableServerNewsInChat { get; set; } = false;

    public bool MareAPI { get; set; } = true;
    public bool AutofillEmptyNotesFromCharaName { get; set; } = true;
    public CacheEvictionMode CacheEvictionMode { get; set; } = CacheEvictionMode.LeastRecentlyUsed;
    public bool UseMultithreadedCompression { get; set; } = false;
    public int CompressionLevel { get; set; } = 3;
    public TextureCompressionPreference TextureCompressionPreference { get; set; } = TextureCompressionPreference.WhateverEquipped;
    public bool AutoJoinVenueSyncshells { get; set; } = true;
    public bool AllowBbCodeImages { get; set; } = true;
    public ElezenStrings.Colour PairRequestNameColors { get; set; } = new(Foreground: 0x87D745u, Glow: 0x7A6E0Eu);
    public bool PairingSystemEnabled { get; set; } = false;
    public bool FrostbrandWelcomeSeen { get; set; } = false;
    public HashSet<AutoRejectCombo> AutoRejectCombos { get; set; } = [];
    public int PairRequestMinimumLevel { get; set; } = 15;
    public HashSet<ushort> PairRequestRejectedHomeworlds { get; set; } = [];
    public string LastSeenPatchNotesVersion { get; set; } = "1.0.0";
    public bool PairRequestFriendsOnly { get; set; } = false;
    public bool HoldUploadsUntilInRange { get; set; } = false;
    public bool ShowChangelog { get; set; } = true;
}
