using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using ElezenTools.UI;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Comparer;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Snowcloak.UI;

public class SettingsUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly IpcManager _ipcManager;
    private readonly IpcProvider _ipcProvider;
    private readonly CacheMonitor _cacheMonitor;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SnowcloakConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, IReadOnlyDictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly FileCompactor _fileCompactor;
    private readonly FileUploadManager _fileTransferManager;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly FileCacheManager _fileCacheManager;
    private readonly PairManager _pairManager;
    private readonly PairRequestService _pairRequestService;
    private readonly ChatService _chatService;
    private readonly GuiHookService _guiHookService;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly AccountRegistrationService _registerService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly UiSharedService _uiShared;
    private bool _deleteAccountPopupModalShown = false;
    private string _lastTab = string.Empty;
    private bool? _notesSuccessfullyApplied = null;
    private bool _overwriteExistingLabels = false;
    private bool _readClearCache = false;
    private CancellationTokenSource? _validationCts;
    private Task<List<FileCacheEntity>>? _validationTask;
    private bool _wasOpen = false;
    private readonly IProgress<(int, int, FileCacheEntity)> _validationProgress;
    private (int, int, FileCacheEntity) _currentProgress;

    private bool _registrationInProgress = false;
    private bool _registrationSuccess = false;
    private string? _registrationMessage;
    private bool _xivAuthRegistrationInProgress = false;
    private bool _xivAuthRegistrationSuccess = false;
    private string? _xivAuthRegistrationMessage;
    
    public SettingsUi(ILogger<SettingsUi> logger,
        UiSharedService uiShared, SnowcloakConfigService configService,
        PairManager pairManager, PairRequestService pairRequestService, ChatService chatService, GuiHookService guiHookService,
        ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService, PlayerPerformanceService playerPerformanceService,
        SnowMediator mediator, PerformanceCollectorService performanceCollector,
        FileUploadManager fileTransferManager,
        FileTransferOrchestrator fileTransferOrchestrator,
        FileCacheManager fileCacheManager,
        FileCompactor fileCompactor, ApiController apiController,
        IpcManager ipcManager, IpcProvider ipcProvider, CacheMonitor cacheMonitor,
        DalamudUtilService dalamudUtilService, AccountRegistrationService registerService) 
        : base(logger, mediator, "Snowcloak Settings", performanceCollector)
    {
        _configService = configService;
        _pairManager = pairManager;
        _pairRequestService = pairRequestService;
        _chatService = chatService;
        _guiHookService = guiHookService;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _playerPerformanceService = playerPerformanceService;
        _performanceCollector = performanceCollector;
        _fileTransferManager = fileTransferManager;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileCacheManager = fileCacheManager;
        _apiController = apiController;
        _ipcManager = ipcManager;
        _ipcProvider = ipcProvider;
        _cacheMonitor = cacheMonitor;
        _dalamudUtilService = dalamudUtilService;
        _registerService = registerService;
        _fileCompactor = fileCompactor;
        _uiShared = uiShared;
        AllowClickthrough = false;
        AllowPinning = false;
        _validationProgress = new Progress<(int, int, FileCacheEntity)>(v => _currentProgress = v);

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(650, 400),
            MaximumSize = new Vector2(600, 2000),
        };

        Mediator.Subscribe<OpenSettingsUiMessage>(this, (_) => Toggle());
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => UiSharedService_GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => UiSharedService_GposeEnd());
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) => LastCreatedCharacterData = msg.CharacterData);
        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));
    }
    

    public CharacterData? LastCreatedCharacterData { private get; set; }
    private ApiController ApiController => _uiShared.ApiController;
    
    protected override void DrawInternal()
    {
        _ = _uiShared.DrawOtherPluginState();

        DrawSettingsContent();
    }

    public override void OnClose()
    {
        _uiShared.EditTrackerPosition = false;

        base.OnClose();
    }

    private void DrawBlockedTransfers()
    {
        _lastTab = "BlockedTransfers";
        ElezenImgui.ColouredWrappedText("Files that you attempted to upload or download that were forbidden to be transferred by their creators will appear here. " +
                                        "If you see file paths from your drive here, then those files were not allowed to be uploaded. If you see hashes, those files were not allowed to be downloaded. " +
                                        "Ask your paired friend to send you the mod in question through other means or acquire the mod yourself.",
            ImGuiColors.DalamudGrey);

        if (ImGui.BeginTable("TransfersTable", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(
                "Hash/Filename");
            ImGui.TableSetupColumn("Forbidden by");

            ImGui.TableHeadersRow();

            foreach (var item in _fileTransferOrchestrator.ForbiddenTransfers)
            {
                ImGui.TableNextColumn();
                if (item is UploadFileTransfer transfer)
                {
                    ImGui.TextUnformatted(transfer.LocalFile);
                }
                else
                {
                    ImGui.TextUnformatted(item.Hash);
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ForbiddenBy);
            }
            ImGui.EndTable();
        }
    }

    private void DrawCurrentTransfers()
    {
        _lastTab = "Transfers";
        _uiShared.BigText("Transfer Settings");
        
        int maxParallelDownloads = _configService.Current.ParallelDownloads;
        int downloadSpeedLimit = _configService.Current.DownloadSpeedLimitInBytes;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Global Download Speed Limit");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("###speedlimit", ref downloadSpeedLimit))
        {
            _configService.Current.DownloadSpeedLimitInBytes = downloadSpeedLimit;
            _configService.Save();
            Mediator.Publish(new DownloadLimitChangedMessage());
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawCombo("###speed", [DownloadSpeeds.Bps, DownloadSpeeds.KBps, DownloadSpeeds.MBps],
            (s) => s switch
            {
                DownloadSpeeds.Bps => "Byte/s",
                DownloadSpeeds.KBps => "KB/s",
                DownloadSpeeds.MBps => "MB/s",
                _ => throw new NotSupportedException()
            }, (s) =>
            {
                _configService.Current.DownloadSpeedType = s;
                _configService.Save();
                Mediator.Publish(new DownloadLimitChangedMessage());
            }, _configService.Current.DownloadSpeedType);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("0 = No limit/infinite");
        
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Maximum Parallel Downloads", ref maxParallelDownloads, 1, 10))
        {
            _configService.Current.ParallelDownloads = maxParallelDownloads;
            _configService.Save();
        }
        bool holdUploadsUntilInRange = _configService.Current.HoldUploadsUntilInRange;
        if (ImGui.Checkbox("Hold uploads until someone is in range", ref holdUploadsUntilInRange))
        {
            _configService.Current.HoldUploadsUntilInRange = holdUploadsUntilInRange;
            _configService.Save();
        }
        _uiShared.DrawHelpText("When enabled, Snowcloak will wait to upload your files until a paired player is nearby.");

        
        ImGui.Separator();
        _uiShared.BigText("Transfer UI");
        
        bool showTransferWindow = _configService.Current.ShowTransferWindow;
        if (ImGui.Checkbox("Show separate transfer window", ref showTransferWindow))
        {
            _configService.Current.ShowTransferWindow = showTransferWindow;
            _configService.Save();
        }
        _uiShared.DrawHelpText($"The download window will show the current progress of outstanding downloads.{Environment.NewLine}{Environment.NewLine}" +
                                                              $"What do W/Q/P/D stand for?{Environment.NewLine}W = Waiting for Slot (see Maximum Parallel Downloads){Environment.NewLine}" +
                                                              $"Q = Queued on Server, waiting for queue ready signal{Environment.NewLine}" +
                                                              $"P = Processing download (aka downloading){Environment.NewLine}" +
                                                              $"D = Decompressing download");
        if (!_configService.Current.ShowTransferWindow) ImGui.BeginDisabled();
        ImGui.Indent();
        bool editTransferWindowPosition = _uiShared.EditTrackerPosition;
        if (ImGui.Checkbox("Edit Transfer Window position", ref editTransferWindowPosition))
        {
            _uiShared.EditTrackerPosition = editTransferWindowPosition;
        }
        ImGui.Unindent();
        if (!_configService.Current.ShowTransferWindow) ImGui.EndDisabled();

        bool showTransferBars = _configService.Current.ShowTransferBars;
        if (ImGui.Checkbox("Show transfer bars rendered below players", ref showTransferBars))
        {
            _configService.Current.ShowTransferBars = showTransferBars;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will render a progress bar during the download at the feet of the player you are downloading from.");
        
        if (!showTransferBars) ImGui.BeginDisabled();
        ImGui.Indent();
        bool transferBarShowText = _configService.Current.TransferBarsShowText;
        if (ImGui.Checkbox("Show Download Text", ref transferBarShowText))
        {
            _configService.Current.TransferBarsShowText = transferBarShowText;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Shows download text (amount of MiB downloaded) in the transfer bars");
        int transferBarWidth = _configService.Current.TransferBarsWidth;
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Transfer Bar Width", ref transferBarWidth, 0, 500))
        {
            if (transferBarWidth < 10)
                transferBarWidth = 10;
            _configService.Current.TransferBarsWidth = transferBarWidth;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Width of the displayed transfer bars (will never be less wide than the displayed text)");
        int transferBarHeight = _configService.Current.TransferBarsHeight;
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Transfer Bar Height", ref transferBarHeight, 0, 50))
        {
            if (transferBarHeight < 2)
                transferBarHeight = 2;
            _configService.Current.TransferBarsHeight = transferBarHeight;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Height of the displayed transfer bars (will never be less tall than the displayed text)");
        bool showUploading = _configService.Current.ShowUploading;
        if (ImGui.Checkbox("Show 'Uploading' text below players that are currently uploading", ref showUploading))
        {
            _configService.Current.ShowUploading = showUploading;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will render an 'Uploading' text at the feet of the player that is in progress of uploading data.");
        
        ImGui.Unindent();
        if (!showUploading) ImGui.BeginDisabled();
        ImGui.Indent();
        bool showUploadingBigText = _configService.Current.ShowUploadingBigText;
        if (ImGui.Checkbox("Large font for 'Uploading' text", ref showUploadingBigText))
        {
            _configService.Current.ShowUploadingBigText = showUploadingBigText;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will render an 'Uploading' text in a larger font.");
        
        ImGui.Unindent();

        if (!showUploading) ImGui.EndDisabled();
        if (!showTransferBars) ImGui.EndDisabled();

        ImGui.Separator();
        _uiShared.BigText("Current Transfers");
        
        if (ImGui.BeginTabBar("TransfersTabBar"))
        {
            if (ApiController.ServerState is ServerState.Connected && ImGui.BeginTabItem("Transfers"))
            {
                ImGui.TextUnformatted("Uploads");
                if (ImGui.BeginTable("UploadsTable", 3))
                {
                    ImGui.TableSetupColumn("File");
                    ImGui.TableSetupColumn("Uploaded");
                    ImGui.TableSetupColumn("Size");
                    ImGui.TableHeadersRow();
                    foreach (var transfer in _fileTransferManager.CurrentUploads.ToArray())
                    {
                        var color = UiSharedService.UploadColor((transfer.Transferred, transfer.Total));
                        var col = ImRaii.PushColor(ImGuiCol.Text, color);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(transfer.Hash);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(UiSharedService.ByteToString(transfer.Transferred));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(UiSharedService.ByteToString(transfer.Total));
                        col.Dispose();
                        ImGui.TableNextRow();
                    }

                    ImGui.EndTable();
                }
                ImGui.Separator();
                ImGui.TextUnformatted("Downloads");
                if (ImGui.BeginTable("DownloadsTable", 4))
                {
                    ImGui.TableSetupColumn("User");
                    ImGui.TableSetupColumn("Server");
                    ImGui.TableSetupColumn("Files");
                    ImGui.TableSetupColumn("Download");
                    ImGui.TableHeadersRow();

                    foreach (var transfer in _currentDownloads.ToArray())
                    {
                        var userName = transfer.Key.Name;
                        foreach (var entry in transfer.Value)
                        {
                            var color = UiSharedService.UploadColor((entry.Value.TransferredBytes, entry.Value.TotalBytes));
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(userName);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(entry.Key);
                            var col = ImRaii.PushColor(ImGuiCol.Text, color);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(entry.Value.TransferredFiles + "/" + entry.Value.TotalFiles);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(UiSharedService.ByteToString(entry.Value.TransferredBytes) + "/" + UiSharedService.ByteToString(entry.Value.TotalBytes));
                            ImGui.TableNextColumn();
                            col.Dispose();
                            ImGui.TableNextRow();
                        }
                    }

                    ImGui.EndTable();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Blocked Transfers"))
            {
                DrawBlockedTransfers();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private List<(XivChatType, string)> GetSyncshellChatTypes() =>
    [
        (XivChatType.None,"(use global setting)"),
        (XivChatType.Debug, "Debug"),
        (XivChatType.Echo, "Echo"),
        (XivChatType.StandardEmote, "Standard Emote"),
        (XivChatType.CustomEmote, "Custom Emote"),
        (XivChatType.SystemMessage, "System Message"),
        (XivChatType.SystemError, "System Error"),
        (XivChatType.GatheringSystemMessage, "Gathering Message"),
        (XivChatType.ErrorMessage, "Error message"),
    ];

    private void DrawChatConfig()
    {
        _lastTab = "Chat [BETA]";

        _uiShared.BigText("Chat Settings");
        
        var disableChat = _configService.Current.DisableChat;

        if (ImGui.Checkbox("Disable chat globally", ref disableChat))
        {
            _configService.Current.DisableChat = disableChat;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Global setting to disable chat.");
        ImGui.TextWrapped("The chat system is currently under active development. If you use it, you're encouraged to check back here often" +
                          "to see if there's any new settings to play with!");
        
    }

    private void DrawAdvanced()
    {
        _lastTab = "Advanced";

        _uiShared.BigText("Advanced");
        
        bool logEvents = _configService.Current.LogEvents;
        if (ImGui.Checkbox("Log Event Viewer data to disk", ref logEvents))
        {
            _configService.Current.LogEvents = logEvents;
            _configService.Save();
        }

        ImGui.SameLine(300.0f * ImGuiHelpers.GlobalScale);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.NotesMedical, "Open Event Viewer"))
        {
            Mediator.Publish(new UiToggleMessage(typeof(EventViewerUI)));
        }

        bool holdCombatApplication = _configService.Current.HoldCombatApplication;
        if (ImGui.Checkbox("Hold application during combat", ref holdCombatApplication))
        {
            if (!holdCombatApplication)
                Mediator.Publish(new CombatOrPerformanceEndMessage());
            _configService.Current.HoldCombatApplication = holdCombatApplication;
            _configService.Save();
        }
        
        ImGui.Separator();
        _uiShared.BigText("Debug");
#if DEBUG
        if (LastCreatedCharacterData != null && ImGui.TreeNode("Last created character data"))
        {
            foreach (var l in JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true }).Split('\n'))
            {
                ImGui.TextUnformatted($"{l}");
            }

            ImGui.TreePop();
        }
#endif
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Copy, "[DEBUG] Copy Last created Character Data to clipboard"))
        {
            if (LastCreatedCharacterData != null)
            {
                ImGui.SetClipboardText(JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true }));
            }
            else
            {
                ImGui.SetClipboardText("ERROR: No created character data, cannot copy.");
            }
        }
        UiSharedService.AttachToolTip("Use this when reporting mods being rejected from the server.");
        
        _uiShared.DrawCombo("Log Level", Enum.GetValues<LogLevel>(), (l) => l.ToString(), (l) =>
        {
            _configService.Current.LogLevel = l;
            _configService.Save();
        }, _configService.Current.LogLevel);

        bool logPerformance = _configService.Current.LogPerformance;
        if (ImGui.Checkbox("Log Performance Counters", ref logPerformance))
        {
            _configService.Current.LogPerformance = logPerformance;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Enabling this can incur a (slight) performance impact. Enabling this for extended periods of time is not recommended.");
        
        using (ImRaii.Disabled(!logPerformance))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.StickyNote, "Print Performance Stats to /xllog"))
            {
                _performanceCollector.PrintPerformanceStats();
            }
            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.StickyNote, "Print Performance Stats (last 60s) to /xllog"))
            {
                _performanceCollector.PrintPerformanceStats(60);
            }
        }

        if (ImGui.TreeNode("Active Character Blocks"))
        {
            var onlinePairs = _pairManager.GetOnlineUserPairs();
            foreach (var pair in onlinePairs)
            {
                if (pair.IsApplicationBlocked)
                {
                    ImGui.TextUnformatted(pair.PlayerName);
                    ImGui.SameLine();
                    ImGui.TextUnformatted(string.Join(", ", pair.HoldApplicationReasons));
                }
            }
        }
    }

    private void DrawFileStorageSettings()
    {
        _lastTab = "FileCache";

        _uiShared.BigText("Storage");
        
        ElezenImgui.WrappedText("Snowcloak stores downloaded files from paired people permanently. This is to improve loading performance and requiring less downloads. " +
                                        "The storage governs itself by clearing data beyond the set storage size. Please set the storage size accordingly. It is not necessary to manually clear the storage.");

        _uiShared.DrawFileScanState();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Monitoring Penumbra Folder: " + (_cacheMonitor.PenumbraWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.PenumbraWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("penumbraMonitor");
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
            }
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Monitoring Snowcloak Storage Folder: " + (_cacheMonitor.SnowWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.SnowWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("snowMonitor");
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartSnowWatcher(_configService.Current.CacheFolder);
            }
        }
        if (_cacheMonitor.SnowWatcher == null || _cacheMonitor.PenumbraWatcher == null)
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Play, "Resume Monitoring"))
            {
                _cacheMonitor.StartSnowWatcher(_configService.Current.CacheFolder);
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
                _cacheMonitor.InvokeScan();
            }
            UiSharedService.AttachToolTip("Attempts to resume monitoring for both Penumbra and Snowcloak Storage. "
                                                                             + "Resuming the monitoring will also force a full scan to run." + Environment.NewLine
                                                                             + "If the button remains present after clicking it, consult /xllog for errors");
        }
        else
        {
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Stop, "Stop Monitoring"))
                {
                    _cacheMonitor.StopMonitoring();
                }
            }
            UiSharedService.AttachToolTip("Stops the monitoring for both Penumbra and Snowcloak Storage. "
                                                                           + "Do not stop the monitoring, unless you plan to move the Penumbra and Snowcloak Storage folders, to ensure correct functionality of Snowcloak." + Environment.NewLine
                                                                           + "If you stop the monitoring to move folders around, resume it after you are finished moving the files."
                                                                           + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");
        }

        _uiShared.DrawCacheDirectorySetting();
        ImGui.AlignTextToFramePadding();
        if (_cacheMonitor.FileCacheSize >= 0)
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "Currently utilized local storage: {0:0.00} GiB", _cacheMonitor.FileCacheSize / 1024.0 / 1024.0 / 1024.0));
        else
            ImGui.TextUnformatted("Currently utilized local storage: Calculating...");
        bool isLinux = _dalamudUtilService.IsWine;
        if (!isLinux)
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "Remaining space free on drive: {0:0.00} GiB", _cacheMonitor.FileCacheDriveFree / 1024.0 / 1024.0 / 1024.0));
        bool useFileCompactor = _configService.Current.UseCompactor;
        if (!useFileCompactor && !isLinux)
        {
            ElezenImgui.ColouredWrappedText("Hint: To free up space when using Snowcloak consider enabling the File Compactor", ImGuiColors.DalamudYellow);
        }
        if (isLinux || !_cacheMonitor.StorageisNTFS) ImGui.BeginDisabled();
        if (ImGui.Checkbox( "Use file compactor", ref useFileCompactor))
        {
            _configService.Current.UseCompactor = useFileCompactor;
            _configService.Save();
            if (!isLinux)
            {
                _fileCompactor.CompactStorage(useFileCompactor);
            }
        }

        _uiShared.DrawHelpText(
            "The file compactor can massively reduce your saved files. It might incur a minor penalty on loading files on a slow CPU." +
            Environment.NewLine
            + "It is recommended to leave it enabled to save on space.");
        if (isLinux || !_cacheMonitor.StorageisNTFS)
        {
            ImGui.EndDisabled();
            ImGui.TextUnformatted("The file compactor is only available on Windows and NTFS drives.");
        }
        
        bool useMultithreadedCompression = _configService.Current.UseMultithreadedCompression;
        if (ImGui.Checkbox("Enable multithreaded compression", ref useMultithreadedCompression))
        {
            _configService.Current.UseMultithreadedCompression = useMultithreadedCompression;
            _configService.Save();
        }
        _uiShared.DrawHelpText("When enabled, compression will use a number of workers equal to your CPU thread count. This will alter performance characteristics with different results based on your CPU, enable/disable based on your experience.");
        int compressionLevel = _configService.Current.CompressionLevel;
        if (ImGui.SliderInt("Compression level", ref compressionLevel, 3, 9, "%d"))
        {
            compressionLevel = Math.Clamp(compressionLevel, 2, 9);
            _configService.Current.CompressionLevel = compressionLevel;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Higher compression levels create smaller uploads. This uses more of your CPU, but allows sync partners to download faster. Level 3 is the default.");
        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));
        ImGui.Separator();
        ElezenImgui.WrappedText("File Storage validation can make sure that all files in your local storage folder are valid. " +
                                        "Run the validation before you clear the Storage for no reason. " + Environment.NewLine +
                                        "This operation, depending on how many files you have in your storage, can take a while and will be CPU and drive intensive.");
        using (ImRaii.Disabled(_validationTask != null && !_validationTask.IsCompleted))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Check, "Start File Storage Validation"))
            {
                _validationCts?.Cancel();
                _validationCts?.Dispose();
                _validationCts = new();
                var token = _validationCts.Token;
                _validationTask = Task.Run(() => _fileCacheManager.ValidateLocalIntegrity(_validationProgress, token));
            }
        }
        if (_validationTask != null && !_validationTask.IsCompleted)
        {
            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Times, "Cancel"))
            {
                _validationCts?.Cancel();
            }
        }

        if (_validationTask != null)
        {
            using (ImRaii.PushIndent(20f))
            {
                if (_validationTask.IsCompleted)
                {
                    ElezenImgui.WrappedText(string.Format(CultureInfo.InvariantCulture, "The storage validation has completed and removed {0} invalid files from storage.", _validationTask.Result.Count));
                }
                else
                {
                    ElezenImgui.WrappedText(string.Format(CultureInfo.InvariantCulture, "Storage validation is running: {0}/{1}", _currentProgress.Item1, _currentProgress.Item2));
                    ElezenImgui.WrappedText(string.Format(CultureInfo.InvariantCulture, "Current item: {0}", _currentProgress.Item3.ResolvedFilepath));
                }
            }
        }
        ImGui.Separator();

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));
        ImGui.TextUnformatted("To clear the local storage accept the following disclaimer");
        ImGui.Indent();
        ImGui.Checkbox("##readClearCache", ref _readClearCache);
        ImGui.SameLine();
        ElezenImgui.WrappedText("I understand that: "
                                        + Environment.NewLine + "- By clearing the local storage I put the file servers of my connected service under extra strain by having to redownload all data."
                                        + Environment.NewLine + "- This is not a step to try to fix sync issues."
                                        + Environment.NewLine + "- This can make the situation of not getting other players data worse in situations of heavy file server load.");
        if (!_readClearCache)
            ImGui.BeginDisabled();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Clear local storage") && UiSharedService.CtrlPressed() && _readClearCache)
        {
            _ = Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(_configService.Current.CacheFolder))
                {
                    File.Delete(file);
                }
            });
        }
        UiSharedService.AttachToolTip("You normally do not need to do this. THIS IS NOT SOMETHING YOU SHOULD BE DOING TO TRY TO FIX SYNC ISSUES." + Environment.NewLine
            + "This will solely remove all downloaded data from all players and will require you to re-download everything again." + Environment.NewLine
            + "Snowcloak's storage is self-clearing and will not surpass the limit you have set it to." + Environment.NewLine
            + "If you still think you need to do this hold CTRL while pressing the button.");
        if (!_readClearCache)
            ImGui.EndDisabled();
        ImGui.Unindent();
    }

    private void DrawGeneral()
    {
        if (!string.Equals(_lastTab, "General", StringComparison.OrdinalIgnoreCase))
        {
            _notesSuccessfullyApplied = null;
        }

        _lastTab = "General";

        _uiShared.BigText("Notes");
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.StickyNote, "Export all your user notes to clipboard"))
        {
            ImGui.SetClipboardText(UiSharedService.GetNotes(_pairManager.DirectPairs.UnionBy(_pairManager.GroupPairs.SelectMany(p => p.Value), p => p.UserData, UserDataComparer.Instance).ToList()));
        }
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileImport, "Import notes from clipboard"))
        {
            _notesSuccessfullyApplied = null;
            var notes = ImGui.GetClipboardText();
            _notesSuccessfullyApplied = _uiShared.ApplyNotesFromClipboard(notes, _overwriteExistingLabels);
        }

        ImGui.SameLine();
        ImGui.Checkbox("Overwrite existing notes", ref _overwriteExistingLabels);
        _uiShared.DrawHelpText("If this option is selected all already existing notes for UIDs will be overwritten by the imported notes.");
        if (_notesSuccessfullyApplied.HasValue && _notesSuccessfullyApplied.Value)
        {
            ElezenImgui.ColouredWrappedText("User Notes successfully imported", ImGuiColors.HealerGreen);
        }
        else if (_notesSuccessfullyApplied.HasValue && !_notesSuccessfullyApplied.Value)
        {
            ElezenImgui.ColouredWrappedText("Attempt to import notes from clipboard failed. Check formatting and try again", ImGuiColors.DalamudRed);
        }

        var openPopupOnAddition = _configService.Current.OpenPopupOnAdd;

        if (ImGui.Checkbox("Open Notes Popup on user addition", ref openPopupOnAddition))
        {
            _configService.Current.OpenPopupOnAdd = openPopupOnAddition;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will open a popup that allows you to set the notes for a user after successfully adding them to your individual pairs.");
        
        var autofillNotes = _configService.Current.AutofillEmptyNotesFromCharaName;
        if (ImGui.Checkbox("Automatically update empty notes with player names", ref autofillNotes))
        {
            _configService.Current.AutofillEmptyNotesFromCharaName = autofillNotes;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will automatically set a user's note with their player name unless you override it");
        
        ImGui.Separator();
        _uiShared.BigText("Venues");
        var autoJoinVenues = _configService.Current.AutoJoinVenueSyncshells;
        if (ImGui.Checkbox("Show prompts to join venue syncshells when on their grounds", ref autoJoinVenues))
        {
            _configService.Current.AutoJoinVenueSyncshells = autoJoinVenues;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Automatically detects venue housing plots and offers users an option to join them.");
        
        ImGui.Separator();
        _uiShared.BigText("UI");
        var showCharacterNames = _configService.Current.ShowCharacterNames;
        var showVisibleSeparate = _configService.Current.ShowVisibleUsersSeparately;
        var sortSyncshellByVRAM = _configService.Current.SortSyncshellsByVRAM;
        var showOfflineSeparate = _configService.Current.ShowOfflineUsersSeparately;
        var showProfiles = _configService.Current.ProfilesShow;
        var showNsfwProfiles = _configService.Current.ProfilesAllowNsfw;
        var profileDelay = _configService.Current.ProfileDelay;
        var profileOnRight = _configService.Current.ProfilePopoutRight;
        var enableRightClickMenu = _configService.Current.EnableRightClickMenus;
        var enableDtrEntry = _configService.Current.EnableDtrEntry;
        var showUidInDtrTooltip = _configService.Current.ShowUidInDtrTooltip;
        var preferNoteInDtrTooltip = _configService.Current.PreferNoteInDtrTooltip;
        var useColorsInDtr = _configService.Current.UseColorsInDtr;
        var dtrColorsDefault = _configService.Current.DtrColorsDefault;
        var allowBbCodeImages = _configService.Current.AllowBbCodeImages;
        var dtrColorsNotConnected = _configService.Current.DtrColorsNotConnected;
        var dtrColorsPairsInRange = _configService.Current.DtrColorsPairsInRange;
        var dtrColorsPendingRequests = _configService.Current.DtrColorsPendingRequests;
        var showChangelog = _configService.Current.ShowChangelog;
        if (ImGui.Checkbox("Show changelogs on update", ref showChangelog))
        {
            _configService.Current.ShowChangelog = showChangelog;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will add Snowcloak related right click menu entries in the game UI on paired players.");
        if (ImGui.Checkbox("Enable Game Right Click Menu Entries", ref enableRightClickMenu))
        {
            _configService.Current.EnableRightClickMenus = enableRightClickMenu;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will add Snowcloak related right click menu entries in the game UI on paired players.");
        
        if (ImGui.Checkbox("Display status and visible pair count in Server Info Bar", ref enableDtrEntry))
        {
            _configService.Current.EnableDtrEntry = enableDtrEntry;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will add Snowcloak connection status and visible pair count in the Server Info Bar.\nYou can further configure this through your Dalamud Settings.");
        
        using (ImRaii.Disabled(!enableDtrEntry))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox("Show visible character's UID in tooltip", ref showUidInDtrTooltip))
            {
                _configService.Current.ShowUidInDtrTooltip = showUidInDtrTooltip;
                _configService.Save();
            }

            if (ImGui.Checkbox("Prefer notes over player names in tooltip", ref preferNoteInDtrTooltip))
            {
                _configService.Current.PreferNoteInDtrTooltip = preferNoteInDtrTooltip;
                _configService.Save();
            }

            ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
            _uiShared.DrawCombo("Server Info Bar style", Enumerable.Range(0, DtrEntry.NumStyles), (i) => DtrEntry.RenderDtrStyle(i, "123"),
                (i) =>
            {
                _configService.Current.DtrStyle = i;
                _configService.Save();
            }, _configService.Current.DtrStyle);

            if (ImGui.Checkbox("Color-code the Server Info Bar entry according to status", ref useColorsInDtr))
            {
                _configService.Current.UseColorsInDtr = useColorsInDtr;
                _configService.Save();
            }

            using (ImRaii.Disabled(!useColorsInDtr))
            {
                using var indent2 = ImRaii.PushIndent();
                if (ImGui.BeginTable("DtrColorTable", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableNextColumn();
                    if (InputDtrColors("Default", ref dtrColorsDefault))
                    {
                        _configService.Current.DtrColorsDefault = dtrColorsDefault;
                        _configService.Save();
                    }

                    ImGui.TableNextColumn();
                    if (InputDtrColors("Not Connected", ref dtrColorsNotConnected))
                    {
                        _configService.Current.DtrColorsNotConnected = dtrColorsNotConnected;
                        _configService.Save();
                    }

                    ImGui.TableNextColumn();
                    if (InputDtrColors("Pairs in Range", ref dtrColorsPairsInRange))
                    {
                        _configService.Current.DtrColorsPairsInRange = dtrColorsPairsInRange;
                        _configService.Save();
                    }

                    ImGui.TableNextColumn();
                    if (InputDtrColors("Pending Requests", ref dtrColorsPendingRequests))
                    {
                        _configService.Current.DtrColorsPendingRequests = dtrColorsPendingRequests;
                        _configService.Save();
                    }

                    ImGui.EndTable();
                }
            }
        }

        var useNameColors = _configService.Current.UseNameColors;
        var nameColors = _configService.Current.NameColors;
        var autoPausedNameColors = _configService.Current.BlockedNameColors;
        if (ImGui.Checkbox("Color nameplates of paired players", ref useNameColors))
        {
            _configService.Current.UseNameColors = useNameColors;
            _configService.Save();
            _guiHookService.RequestRedraw();
        }

        using (ImRaii.Disabled(!useNameColors))
        {
            using var indent = ImRaii.PushIndent();
            if (InputDtrColors("Character Name Color", ref nameColors))
            {
                _configService.Current.NameColors = nameColors;
                _configService.Save();
                _guiHookService.RequestRedraw();
            }

            ImGui.SameLine();

            if (InputDtrColors("Blocked Character Color", ref autoPausedNameColors))
            {
                _configService.Current.BlockedNameColors = autoPausedNameColors;
                _configService.Save();
                _guiHookService.RequestRedraw();
            }
        }
        
        if (ImGui.Checkbox( "Show separate Visible group", ref showVisibleSeparate))
        {
            _configService.Current.ShowVisibleUsersSeparately = showVisibleSeparate;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will show all currently visible users in a special 'Visible' group in the main UI.");
        if (ImGui.Checkbox("Sort visible syncshell users by VRAM usage", ref sortSyncshellByVRAM))
        {
            _configService.Current.SortSyncshellsByVRAM = sortSyncshellByVRAM;
            _logger.LogWarning("Changing value: {sortSyncshellsByVRAM}", sortSyncshellByVRAM);

            _configService.Save();
        }
        _uiShared.DrawHelpText("This will put users using the most VRAM in a syncshell at the top of the list.");
        if (ImGui.Checkbox("Group users by connection status", ref showOfflineSeparate))
        {
            _configService.Current.ShowOfflineUsersSeparately = showOfflineSeparate;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will categorize users by their connection status in the main UI.");
        
        if (ImGui.Checkbox("Show player names", ref showCharacterNames))
        {
            _configService.Current.ShowCharacterNames = showCharacterNames;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will show character names instead of UIDs when possible");
        
        if (ImGui.Checkbox("Show Profiles on Hover", ref showProfiles))
        {
            Mediator.Publish(new ClearProfileDataMessage());
            _configService.Current.ProfilesShow = showProfiles;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will show the configured user profile after a set delay");
        ImGui.Indent();
        if (!showProfiles) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Popout profiles on the right", ref profileOnRight))
        {
            _configService.Current.ProfilePopoutRight = profileOnRight;
            _configService.Save();
            Mediator.Publish(new CompactUiChange(Vector2.Zero, Vector2.Zero));
        }
        _uiShared.DrawHelpText("Will show profiles on the right side of the main UI");
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("Hover Delay", ref profileDelay, 1, 10))
        {
            _configService.Current.ProfileDelay = profileDelay;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Delay until the profile should be displayed");
        if (!showProfiles) ImGui.EndDisabled();
        ImGui.Unindent();
        if (ImGui.Checkbox("Show profiles marked as NSFW", ref showNsfwProfiles))
        {
            Mediator.Publish(new ClearProfileDataMessage());
            _configService.Current.ProfilesAllowNsfw = showNsfwProfiles;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Will show profiles that have the NSFW tag enabled");
        
        if (ImGui.Checkbox("Render BBCode images", ref allowBbCodeImages))
        {
            _configService.Current.AllowBbCodeImages = allowBbCodeImages;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Disable this to show [img] tags as text instead of loading external images.");
        
        ImGui.Separator();

        var disableOptionalPluginWarnings = _configService.Current.DisableOptionalPluginWarnings;
        var onlineNotifs = _configService.Current.ShowOnlineNotifications;
        var onlineNotifsPairsOnly = _configService.Current.ShowOnlineNotificationsOnlyForIndividualPairs;
        var onlineNotifsNamedOnly = _configService.Current.ShowOnlineNotificationsOnlyForNamedPairs;
        _uiShared.BigText("Notifications");
        
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawCombo("Info Notification Display##settingsUi", (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
            (i) =>
        {
            _configService.Current.InfoNotification = i;
            _configService.Save();
        }, _configService.Current.InfoNotification);
        _uiShared.DrawHelpText("The location where \"Info\" notifications will display."
                                                                           + Environment.NewLine + "'Nowhere' will not show any Info notifications"
                                                                           + Environment.NewLine + "'Chat' will print Info notifications in chat"
                                                                           + Environment.NewLine + "'Toast' will show Warning toast notifications in the bottom right corner"
                                                                           + Environment.NewLine + "'Both' will show chat as well as the toast notification");

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawCombo("Warning Notification Display##settingsUi", (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
            (i) =>
        {
            _configService.Current.WarningNotification = i;
            _configService.Save();
        }, _configService.Current.WarningNotification);
        _uiShared.DrawHelpText("The location where \"Warning\" notifications will display."
                                                                              + Environment.NewLine + "'Nowhere' will not show any Warning notifications"
                                                                              + Environment.NewLine + "'Chat' will print Warning notifications in chat"
                                                                              + Environment.NewLine + "'Toast' will show Warning toast notifications in the bottom right corner"
                                                                              + Environment.NewLine + "'Both' will show chat as well as the toast notification");

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawCombo("Error Notification Display##settingsUi", (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
            (i) =>
        {
            _configService.Current.ErrorNotification = i;
            _configService.Save();
        }, _configService.Current.ErrorNotification);
        _uiShared.DrawHelpText("The location where \"Error\" notifications will display."
                                                                            + Environment.NewLine + "'Nowhere' will not show any Error notifications"
                                                                            + Environment.NewLine + "'Chat' will print Error notifications in chat"
                                                                            + Environment.NewLine + "'Toast' will show Error toast notifications in the bottom right corner"
                                                                            + Environment.NewLine + "'Both' will show chat as well as the toast notification");
        
        if (ImGui.Checkbox("Disable optional plugin warnings", ref disableOptionalPluginWarnings))
        {
            _configService.Current.DisableOptionalPluginWarnings = disableOptionalPluginWarnings;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Enabling this will not show any \"Warning\" labeled messages for missing optional plugins.");
        if (ImGui.Checkbox("Enable online notifications", ref onlineNotifs))
        {
            _configService.Current.ShowOnlineNotifications = onlineNotifs;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Enabling this will show a small notification (type: Info) in the bottom right corner when pairs go online.");
        
        using (ImRaii.Disabled(!onlineNotifs))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox("Notify only for individual pairs", ref onlineNotifsPairsOnly))
            {
                _configService.Current.ShowOnlineNotificationsOnlyForIndividualPairs = onlineNotifsPairsOnly;
                _configService.Save();
            }
            _uiShared.DrawHelpText("Enabling this will only show online notifications (type: Info) for individual pairs.");
            if (ImGui.Checkbox("Notify only for named pairs", ref onlineNotifsNamedOnly))
            {
                _configService.Current.ShowOnlineNotificationsOnlyForNamedPairs = onlineNotifsNamedOnly;
                _configService.Save();
            }
            _uiShared.DrawHelpText("Enabling this will only show online notifications (type: Info) for pairs where you have set an individual note.");
        }
    }

    private bool _perfUnapplied = false;

    private void DrawPerformance()
    {
        _uiShared.BigText("Performance Settings");
        ElezenImgui.WrappedText("The configuration options here are to give you more informed warnings and automation when it comes to other performance-intensive synced players.");
        ImGui.Separator();
        bool recalculatePerformance = false;
        string? recalculatePerformanceUID = null;

        _uiShared.BigText("Global Configuration");
        
        bool alwaysShrinkTextures = _playerPerformanceConfigService.Current.TextureShrinkMode == TextureShrinkMode.Always;
        bool deleteOriginalTextures = _playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal;

        bool preferCompressedTextures = _configService.Current.PreferCompressedTextures;
        if (ImGui.Checkbox("Prefer compressed textures when available", ref preferCompressedTextures))
        {
            _configService.Current.PreferCompressedTextures = preferCompressedTextures;
            _configService.Save();
            _ = _apiController.UserSetTextureCompressionPreference(new TextureCompressionPreferenceDto(preferCompressedTextures));
        }
        _uiShared.DrawHelpText("When enabled, Snowcloak will request compressed texture variants if the server has them. Disable to request higher quality when available.");

        using (ImRaii.Disabled(deleteOriginalTextures))
        {
            if (ImGui.Checkbox("Shrink downloaded textures", ref alwaysShrinkTextures))
            {
                if (alwaysShrinkTextures)
                    _playerPerformanceConfigService.Current.TextureShrinkMode = TextureShrinkMode.Always;
                else
                    _playerPerformanceConfigService.Current.TextureShrinkMode = TextureShrinkMode.Never;
                _playerPerformanceConfigService.Save();
                recalculatePerformance = true;
                _cacheMonitor.ClearSubstStorage();
            }
        }
        _uiShared.DrawHelpText("Automatically shrinks texture resolution of synced players to reduce VRAM utilization." 
                                                                    + UiSharedService.TooltipSeparator + "Texture Size Limit (DXT/BC5/BC7 Compressed): 2048x2048" + Environment.NewLine
                                                                    + "Texture Size Limit (A8R8G8B8 Uncompressed): 1024x1024" + UiSharedService.TooltipSeparator
                                                                    + "Enable to reduce lag in large crowds." + Environment.NewLine
                                                                    + "Disable this for higher quality during GPose.");
        using (ImRaii.Disabled(!alwaysShrinkTextures || _cacheMonitor.FileCacheSize < 0))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox("Delete original textures from disk", ref deleteOriginalTextures))
            {
                _playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal = deleteOriginalTextures;
                _playerPerformanceConfigService.Save();
                _ = Task.Run(() =>
                {
                    _cacheMonitor.DeleteSubstOriginals();
                    _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                });
            }
            _uiShared.DrawHelpText("Deletes original, full-sized, textures from disk after downloading and shrinking." + UiSharedService.TooltipSeparator
                + "Caution!!! This will cause a re-download of all textures when the shrink option is disabled.");
        }

        var totalVramBytes = _pairManager.GetOnlineUserPairs().Where(p => p.IsVisible && p.LastAppliedApproximateVRAMBytes > 0).Sum(p => p.LastAppliedApproximateVRAMBytes);

        ImGui.TextUnformatted("Current VRAM utilization by all nearby players:");
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, totalVramBytes < 2.0 * 1024.0 * 1024.0 * 1024.0))
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, totalVramBytes >= 4.0 * 1024.0 * 1024.0 * 1024.0))
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, totalVramBytes >= 6.0 * 1024.0 * 1024.0 * 1024.0))
                    ImGui.TextUnformatted($"{totalVramBytes / 1024.0 / 1024.0 / 1024.0:0.00} GiB");

        ImGui.Separator();
        _uiShared.BigText("Individual Limits");
        bool autoPause = _playerPerformanceConfigService.Current.AutoPausePlayersExceedingThresholds;
        if (ImGui.Checkbox("Automatically block players exceeding thresholds", ref autoPause))
        {
            _playerPerformanceConfigService.Current.AutoPausePlayersExceedingThresholds = autoPause;
            _playerPerformanceConfigService.Save();
            recalculatePerformance = true;
        }
        _uiShared.DrawHelpText("When enabled, it will automatically block the modded appearance of all players that exceed the thresholds defined below." + Environment.NewLine
            + "Will print a warning in chat when a player is blocked automatically.");
        using (ImRaii.Disabled(!autoPause))
        {
            using var indent = ImRaii.PushIndent();
            var notifyDirectPairs = _playerPerformanceConfigService.Current.NotifyAutoPauseDirectPairs;
            var notifyGroupPairs = _playerPerformanceConfigService.Current.NotifyAutoPauseGroupPairs;
            if (ImGui.Checkbox("Display auto-block warnings for individual pairs", ref notifyDirectPairs))
            {
                _playerPerformanceConfigService.Current.NotifyAutoPauseDirectPairs = notifyDirectPairs;
                _playerPerformanceConfigService.Save();
            }
            if (ImGui.Checkbox("Display auto-block warnings for syncshell pairs", ref notifyGroupPairs))
            {
                _playerPerformanceConfigService.Current.NotifyAutoPauseGroupPairs = notifyGroupPairs;
                _playerPerformanceConfigService.Save();
            }
            var vramAuto = _playerPerformanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB;
            var trisAuto = _playerPerformanceConfigService.Current.TrisAutoPauseThresholdThousands;
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Auto Block VRAM threshold", ref vramAuto))
            {
                _playerPerformanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB = vramAuto;
                _playerPerformanceConfigService.Save();
                _perfUnapplied = true;
            }
            ImGui.SameLine();
            ImGui.Text("(MiB)");
            _uiShared.DrawHelpText("When a loading in player and their VRAM usage exceeds this amount, automatically blocks the synced player." + UiSharedService.TooltipSeparator
                + "Default: 500 MiB");
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Auto Block Triangle threshold", ref trisAuto))
            {
                _playerPerformanceConfigService.Current.TrisAutoPauseThresholdThousands = trisAuto;
                _playerPerformanceConfigService.Save();
                _perfUnapplied = true;
            }
            ImGui.SameLine();
            ImGui.Text("(thousand triangles)");
            _uiShared.DrawHelpText("When a loading in player and their triangle count exceeds this amount, automatically blocks the synced player." + UiSharedService.TooltipSeparator
                + "Default: 400 thousand");
            using (ImRaii.Disabled(!_perfUnapplied))
            {
                if (ImGui.Button("Apply Changes Now"))
                {
                    recalculatePerformance = true;
                    _perfUnapplied = false;
                }
            }
        }

#region Whitelist
        ImGui.Separator();
        _uiShared.BigText("Whitelisted UIDs");
        bool ignoreDirectPairs = _playerPerformanceConfigService.Current.IgnoreDirectPairs;
        if (ImGui.Checkbox("Whitelist all individual pairs", ref ignoreDirectPairs))
        {
            _playerPerformanceConfigService.Current.IgnoreDirectPairs = ignoreDirectPairs;
            _playerPerformanceConfigService.Save();
            recalculatePerformance = true;
        }
        _uiShared.DrawHelpText("Individual pairs will never be affected by auto blocks.");
        ImGui.Dummy(new Vector2(5));
        ElezenImgui.WrappedText("The entries in the list below will be not have auto block thresholds enforced.");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var whitelistPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##whitelistuid", ref _uidToAddForIgnore, 20);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnore)))
        {
            ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add UID/Vanity ID to whitelist"))
            {
                if (!_serverConfigurationManager.IsUidWhitelisted(_uidToAddForIgnore))
                {
                    _serverConfigurationManager.AddWhitelistUid(_uidToAddForIgnore);
                    recalculatePerformance = true;
                    recalculatePerformanceUID = _uidToAddForIgnore;
                }
                _uidToAddForIgnore = string.Empty;
            }
        }
        ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawHelpText("Hint: UIDs are case sensitive.\nVanity IDs are also acceptable.");
        ImGui.Dummy(new Vector2(10));
        var playerList = _serverConfigurationManager.Whitelist;
        if (_selectedEntry > playerList.Count - 1)
            _selectedEntry = -1;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.SetCursorPosY(whitelistPos.Y);
        using (var lb = ImRaii.ListBox("##whitelist"))
        {
            if (lb)
            {
                for (int i = 0; i < playerList.Count; i++)
                {
                    bool shouldBeSelected = _selectedEntry == i;
                    if (ImGui.Selectable(playerList[i] + "##" + i, shouldBeSelected))
                    {
                        _selectedEntry = i;
                    }
                    string? lastSeenName = _serverConfigurationManager.GetNameForUid(playerList[i]);
                    if (lastSeenName != null)
                    {
                        ImGui.SameLine();
                        ElezenImgui.ShowIcon(FontAwesomeIcon.InfoCircle);
                        UiSharedService.AttachToolTip(string.Format(CultureInfo.InvariantCulture, "Last seen name: {0}", lastSeenName));
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedEntry == -1))
        {
            using var pushId = ImRaii.PushId("deleteSelectedWhitelist");
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete selected UID"))
            {
                _serverConfigurationManager.RemoveWhitelistUid(_serverConfigurationManager.Whitelist[_selectedEntry]);
                if (_selectedEntry > playerList.Count - 1)
                    --_selectedEntry;
                _playerPerformanceConfigService.Save();
                recalculatePerformance = true;
            }
        }
#endregion Whitelist

#region Blacklist
        ImGui.Separator();
        _uiShared.BigText("Blacklisted UIDs");
        ElezenImgui.WrappedText("The entries in the list below will never have their characters displayed.");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var blacklistPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##uid", ref _uidToAddForIgnoreBlacklist, 20);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnoreBlacklist)))
        {
            ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add UID/Vanity ID to blacklist"))
            {
                if (!_serverConfigurationManager.IsUidBlacklisted(_uidToAddForIgnoreBlacklist))
                {
                    _serverConfigurationManager.AddBlacklistUid(_uidToAddForIgnoreBlacklist);
                    recalculatePerformance = true;
                    recalculatePerformanceUID = _uidToAddForIgnoreBlacklist;
                }
                _uidToAddForIgnoreBlacklist = string.Empty;
            }
        }
        _uiShared.DrawHelpText("Hint: UIDs are case sensitive.\nVanity IDs are also acceptable.");
        ImGui.Dummy(new Vector2(10));
        var blacklist = _serverConfigurationManager.Blacklist;
        if (_selectedEntryBlacklist > blacklist.Count - 1)
            _selectedEntryBlacklist = -1;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.SetCursorPosY(blacklistPos.Y);
        using (var lb = ImRaii.ListBox("##blacklist"))
        {
            if (lb)
            {
                for (int i = 0; i < blacklist.Count; i++)
                {
                    bool shouldBeSelected = _selectedEntryBlacklist == i;
                    if (ImGui.Selectable(blacklist[i] + "##BL" + i, shouldBeSelected))
                    {
                        _selectedEntryBlacklist = i;
                    }
                    string? lastSeenName = _serverConfigurationManager.GetNameForUid(blacklist[i]);
                    if (lastSeenName != null)
                    {
                        ImGui.SameLine();
                        ElezenImgui.ShowIcon(FontAwesomeIcon.InfoCircle);
                        UiSharedService.AttachToolTip(string.Format(CultureInfo.InvariantCulture,"Last seen name: {0}", lastSeenName));
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedEntryBlacklist == -1))
        {
            using var pushId = ImRaii.PushId("deleteSelectedBlacklist");
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete selected UID"))
            {
                _serverConfigurationManager.RemoveBlacklistUid(_serverConfigurationManager.Blacklist[_selectedEntryBlacklist]);
                if (_selectedEntryBlacklist > blacklist.Count - 1)
                    --_selectedEntryBlacklist;
                _playerPerformanceConfigService.Save();
                recalculatePerformance = true;
            }
        }
#endregion Blacklist

        if (recalculatePerformance)
            Mediator.Publish(new RecalculatePerformanceMessage(recalculatePerformanceUID));
    }

    private bool InputDtrColors(string label, ref ElezenStrings.Colour colors)
    {
        using var id = ImRaii.PushId(label);
        var innerSpacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var foregroundColor = ConvertColor(colors.Foreground);
        var glowColor = ConvertColor(colors.Glow);

        var ret = ImGui.ColorEdit3("###foreground", ref foregroundColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Foreground Color - Set to pure black (#000000) to use the default color");
        
        ImGui.SameLine(0.0f, innerSpacing);
        ret |= ImGui.ColorEdit3("###glow", ref glowColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Glow Color - Set to pure black (#000000) to use the default color");
        
        ImGui.SameLine(0.0f, innerSpacing);
        ImGui.TextUnformatted(label);

        if (ret)
            colors = new(ConvertBackColor(foregroundColor), ConvertBackColor(glowColor));

        return ret;

        static Vector3 ConvertColor(uint color)
            => unchecked(new((byte)color / 255.0f, (byte)(color >> 8) / 255.0f, (byte)(color >> 16) / 255.0f));

        static uint ConvertBackColor(Vector3 color)
            => byte.CreateSaturating(color.X * 255.0f) | ((uint)byte.CreateSaturating(color.Y * 255.0f) << 8) | ((uint)byte.CreateSaturating(color.Z * 255.0f) << 16);
    }
    
    private static Vector4 ConvertColorToVec4(uint color)
        => new(
            (byte)color / 255.0f,
            (byte)(color >> 8) / 255.0f,
            (byte)(color >> 16) / 255.0f,
            1.0f);


    private void DrawServerConfiguration()
    {
        _lastTab = "Service Settings";
        if (ApiController.ServerAlive)
        {
            _uiShared.BigText("Service Actions");
            ImGuiHelpers.ScaledDummy(new Vector2(5, 5));
            var deleteAccountPopupTitle ="Delete your account?";
            if (ImGui.Button("Delete account"))
            {
                _deleteAccountPopupModalShown = true;
                ImGui.OpenPopup(deleteAccountPopupTitle);
            }

            _uiShared.DrawHelpText("Completely deletes your currently connected account.");
            
            if (ImGui.BeginPopupModal(deleteAccountPopupTitle, ref _deleteAccountPopupModalShown, UiSharedService.PopupWindowFlags))
            {
                ElezenImgui.WrappedText(
                   "Your account and all associated files and data on the service will be deleted.");
                ElezenImgui.WrappedText("Your UID will be removed from all pairing lists.");
                ImGui.TextUnformatted("Are you sure you want to continue?");
                ImGui.Separator();
                ImGui.Spacing();

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                  ImGui.GetStyle().ItemSpacing.X) / 2;

                if (ImGui.Button("Delete account", new Vector2(buttonSize, 0)))
                {
                    _ = Task.Run(ApiController.UserDelete);
                    _deleteAccountPopupModalShown = false;
                    Mediator.Publish(new SwitchToIntroUiMessage());
                }

                ImGui.SameLine();

                if (ImGui.Button("Cancel##cancelDelete", new Vector2(buttonSize, 0)))
                {
                    _deleteAccountPopupModalShown = false;
                }

                UiSharedService.SetScaledWindowSize(325);
                ImGui.EndPopup();
            }
            ImGui.Separator();
        }

        _uiShared.BigText("Service & Character Settings");
        
        var idx = _uiShared.DrawServiceSelection();
        var playerName = _dalamudUtilService.GetPlayerName();
        var playerWorldId = _dalamudUtilService.GetHomeWorldId();
        var worldData = _uiShared.WorldData.OrderBy(u => u.Value, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value);
        string playerWorldName = worldData.GetValueOrDefault((ushort)playerWorldId, $"{playerWorldId}");

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));

        var selectedServer = _serverConfigurationManager.GetServerByIndex(idx);
        if (selectedServer == _serverConfigurationManager.CurrentServer)
        {
            if (_apiController.IsConnected)
                ElezenImgui.ColouredWrappedText("For any changes to be applied to the current service you need to reconnect to the service.", ImGuiColors.DalamudYellow);
        }

        if (ImGui.BeginTabBar("serverTabBar"))
        {
            var characterAssignmentsTab = "Character Assignments";
            var secretKeyTab = "Secret Key Management";
            var serviceSettingsTab = "Service Settings";
            if (ImGui.BeginTabItem(characterAssignmentsTab))            {
                if (selectedServer.SecretKeys.Count > 0)
                {
                    float windowPadding = ImGui.GetStyle().WindowPadding.X;
                    float itemSpacing = ImGui.GetStyle().ItemSpacing.X;
                    float longestName = 0.0f;
                    if (selectedServer.Authentications.Count > 0)
                        longestName = selectedServer.Authentications.Max(p => ImGui.CalcTextSize($"{p.CharacterName} @ Pandaemonium  ").X);
                    float iconWidth;

                    using (_ = _uiShared.IconFont.Push())
                        iconWidth = ImGui.CalcTextSize(FontAwesomeIcon.Trash.ToIconString()).X;

                    ElezenImgui.ColouredWrappedText("Characters listed here will connect with the specified secret key.", ImGuiColors.DalamudYellow);
                    int i = 0;
                    foreach (var item in selectedServer.Authentications.ToList())
                    {
                        using var charaId = ImRaii.PushId("selectedChara" + i);

                        bool thisIsYou = string.Equals(playerName, item.CharacterName, StringComparison.OrdinalIgnoreCase)
                            && playerWorldId == item.WorldId;

                        if (!worldData.TryGetValue((ushort)item.WorldId, out string? worldPreview))
                            worldPreview = worldData.First().Value;

                        ElezenImgui.ShowIcon(thisIsYou ? FontAwesomeIcon.Star : FontAwesomeIcon.None);

                        if (thisIsYou)
                            UiSharedService.AttachToolTip("Current character");
                        
                        ImGui.SameLine(windowPadding + iconWidth + itemSpacing);
                        float beforeName = ImGui.GetCursorPosX();
                        ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture,"{0} @ {1}", item.CharacterName, worldPreview));
                        float afterName = ImGui.GetCursorPosX();

                        ImGui.SameLine(afterName + (afterName - beforeName) + longestName + itemSpacing);

                        var secretKeyIdx = item.SecretKeyIdx;
                        var keys = selectedServer.SecretKeys;
                        if (!keys.TryGetValue(secretKeyIdx, out var secretKey))
                        {
                            secretKey = new();
                        }
                        var friendlyName = secretKey.FriendlyName;

                        ImGui.SetNextItemWidth(afterName - iconWidth - itemSpacing * 2 - windowPadding);

                        string selectedKeyName = string.Empty;
                        if (selectedServer.SecretKeys.TryGetValue(item.SecretKeyIdx, out var selectedKey))
                            selectedKeyName = selectedKey.FriendlyName;

                        // _uiShared.DrawCombo() remembers the selected option -- we don't want that, because the value can change
                        if (ImGui.BeginCombo($"##{item.CharacterName}{i}", selectedKeyName))
                        {
                            foreach (var key in selectedServer.SecretKeys)
                            {
                                if (ImGui.Selectable($"{key.Value.FriendlyName}##{i}", key.Key == item.SecretKeyIdx)
                                    && key.Key != item.SecretKeyIdx)
                                {
                                    item.SecretKeyIdx = key.Key;
                                    _serverConfigurationManager.Save();
                                }
                            }
                            ImGui.EndCombo();
                        }

                        ImGui.SameLine();

                        if (_uiShared.IconButton(FontAwesomeIcon.Trash))
                            _serverConfigurationManager.RemoveCharacterFromServer(idx, item);
                        UiSharedService.AttachToolTip("Delete character assignment");
                        i++;
                    }

                    ImGui.Separator();
                    using (_ = ImRaii.Disabled(selectedServer.Authentications.Exists(c =>
                            string.Equals(c.CharacterName, _uiShared.PlayerName, StringComparison.Ordinal)
                                && c.WorldId == _uiShared.WorldId
                    )))
                    {
                        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.User, "Add current character"))
                        {
                            _serverConfigurationManager.AddCurrentCharacterToServer(idx);
                        }
                        ImGui.SameLine();
                    }
                }
                else
                {
                    ElezenImgui.ColouredWrappedText("You need to add a Secret Key first before adding Characters.", ImGuiColors.DalamudYellow);
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(secretKeyTab))
            {
                var currentCharacterAssignment = selectedServer.Authentications.Find(a =>
                    string.Equals(a.CharacterName, _uiShared.PlayerName, StringComparison.OrdinalIgnoreCase)
                        && a.WorldId == playerWorldId
                );
                var hasSecretKey =
                    currentCharacterAssignment != null
                    && selectedServer.SecretKeys.TryGetValue(currentCharacterAssignment.SecretKeyIdx, out var currentSecretKey)
                    && !currentSecretKey.Key.IsNullOrEmpty();

                var invalidSecretKey = _apiController.ServerState == ServerState.Unauthorized
                                       && !_apiController.AuthFailureMessage.IsNullOrEmpty()
                                       && _apiController.AuthFailureMessage.Contains("secret", StringComparison.OrdinalIgnoreCase);

                var invalidSecretKeyIdx = currentCharacterAssignment?.SecretKeyIdx;
                var removeInvalidSecretKey = invalidSecretKey
                                             && invalidSecretKeyIdx.HasValue
                                             && selectedServer.SecretKeys.ContainsKey(invalidSecretKeyIdx.Value);

                if (!hasSecretKey || invalidSecretKey)
                {
                    var xivAuthPrompt = invalidSecretKey
                        ? "Your current character's secret key appears to be invalid. Log in with XIVAuth to replace and assign a working key automatically, or create a legacy key."
                        : "Your current character is not linked to a secret key. Log in with XIVAuth to add and assign one automatically, or create a legacy key.";
                    ElezenImgui.ColouredWrappedText(
                        xivAuthPrompt,
                        ImGuiColors.DalamudYellow);

                    using (ImRaii.Disabled(_xivAuthRegistrationInProgress))
                    {
                        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Log in with XIVAuth"))
                        {
                            var currentPlayerName = playerName;
                            var currentPlayerWorldId = playerWorldId;

                            _xivAuthRegistrationInProgress = true;
                            _xivAuthRegistrationMessage = null;
                            _xivAuthRegistrationSuccess = false;

                            var server = selectedServer;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var reply = await _registerService.XIVAuth(CancellationToken.None).ConfigureAwait(false);
                                    if (!reply.Success)
                                    {
                                        _logger.LogWarning("XIVAuth registration failed: {err}", reply.ErrorMessage);
                                        _xivAuthRegistrationMessage = reply.ErrorMessage;
                                        if (_xivAuthRegistrationMessage.IsNullOrEmpty())
                                            _xivAuthRegistrationMessage = "An unknown error occured. Please try again later.";
                                        return;
                                    }

                                    var assignedCharacter = server.Authentications.Find(a =>
                                        string.Equals(a.CharacterName, currentPlayerName, StringComparison.OrdinalIgnoreCase)
                                        && a.WorldId == currentPlayerWorldId);

                                    var newSecretKeyIdx = server.SecretKeys.Any() ? server.SecretKeys.Max(p => p.Key) + 1 : 0;
                                    server.SecretKeys.Add(newSecretKeyIdx, new SecretKey()
                                    {
                                        FriendlyName = string.Format(CultureInfo.InvariantCulture, "{0} {1}", reply.UID,
                                            string.Format(CultureInfo.InvariantCulture, "(registered {0:yyyy-MM-dd})", DateTime.Now)),
                                        Key = reply.SecretKey ?? string.Empty
                                    });

                                    if (removeInvalidSecretKey && invalidSecretKeyIdx.HasValue)
                                    {
                                        foreach (var auth in server.Authentications.Where(a => a.SecretKeyIdx == invalidSecretKeyIdx.Value).ToList())
                                        {
                                            auth.SecretKeyIdx = newSecretKeyIdx;
                                        }
                                        server.SecretKeys.Remove(invalidSecretKeyIdx.Value);
                                    }
                                    else
                                    {
                                        if (assignedCharacter == null)
                                        {
                                            server.Authentications.Add(new Authentication()
                                            {
                                                CharacterName = currentPlayerName,
                                                WorldId = currentPlayerWorldId,
                                                SecretKeyIdx = newSecretKeyIdx
                                            });
                                        }
                                        else
                                        {
                                            assignedCharacter.SecretKeyIdx = newSecretKeyIdx;
                                        }
                                    }

                                    _serverConfigurationManager.Save();
                                    _ = _apiController.CreateConnections();

                                    _xivAuthRegistrationSuccess = true;
                                    _xivAuthRegistrationMessage = "XIVAuth login successful. Added a new secret key and assigned it to your current character.";
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "XIVAuth registration failed");
                                    _xivAuthRegistrationSuccess = false;
                                    _xivAuthRegistrationMessage ="An unknown error occured. Please try again later.";
                                }
                                finally
                                {
                                    _xivAuthRegistrationInProgress = false;
                                }
                            }, CancellationToken.None);
                        }
                    }

                    if (_xivAuthRegistrationInProgress)
                    {
                        ImGui.TextUnformatted("Waiting for XIVAuth...");
                    }
                    else if (!_xivAuthRegistrationMessage.IsNullOrEmpty())
                    {
                        if (!_xivAuthRegistrationSuccess)
                            ImGui.TextColored(ImGuiColors.DalamudYellow, _xivAuthRegistrationMessage);
                        else
                            ImGui.TextWrapped(_xivAuthRegistrationMessage);
                    }

                    ImGui.Separator();
                }                
                foreach (var item in selectedServer.SecretKeys.ToList())
                {
                    using var id = ImRaii.PushId("key" + item.Key);
                    var friendlyName = item.Value.FriendlyName;
                    if (ImGui.InputText("Secret Key Display Name", ref friendlyName, 255))
                    {
                        item.Value.FriendlyName = friendlyName;
                        _serverConfigurationManager.Save();
                    }
                    var key = item.Value.Key;
                    var keyInUse = selectedServer.Authentications.Exists(p => p.SecretKeyIdx == item.Key);
                    if (keyInUse) ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
                    if (ImGui.InputText("Secret Key", ref key, 64, keyInUse ? ImGuiInputTextFlags.ReadOnly : default))
                    {
                        item.Value.Key = key;
                        _serverConfigurationManager.Save();
                    }
                    if (keyInUse) ImGui.PopStyleColor();

                    bool thisIsYou = selectedServer.Authentications.Any(a =>
                        a.SecretKeyIdx == item.Key
                            && string.Equals(a.CharacterName, _uiShared.PlayerName, StringComparison.OrdinalIgnoreCase)
                            && a.WorldId == playerWorldId
                    );

                    bool disableAssignment = thisIsYou || item.Value.Key.IsNullOrEmpty();

                    using (_ = ImRaii.Disabled(disableAssignment))
                    {
                        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.User, "Assign current character"))
                        {
                            var existingAssignment = selectedServer.Authentications.Find(a =>
                                string.Equals(a.CharacterName, _uiShared.PlayerName, StringComparison.OrdinalIgnoreCase)
                                    && a.WorldId == playerWorldId
                            );

                            if (existingAssignment == null)
                            {
                                selectedServer.Authentications.Add(new Authentication()
                                {
                                    CharacterName = playerName,
                                    WorldId = playerWorldId,
                                    SecretKeyIdx = item.Key
                                });
                            }
                            else
                            {
                                existingAssignment.SecretKeyIdx = item.Key;
                            }
                        }
                        if (!disableAssignment)
                            UiSharedService.AttachToolTip(string.Format(CultureInfo.InvariantCulture, "Use this secret key for {0} @ {1}", playerName, playerWorldName));
                    }

                    ImGui.SameLine();
                    using var disableDelete = ImRaii.Disabled(keyInUse);
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete Secret Key") && UiSharedService.CtrlPressed())
                    {
                        selectedServer.SecretKeys.Remove(item.Key);
                        _serverConfigurationManager.Save();
                    }
                    if (!keyInUse)
                        UiSharedService.AttachToolTip("Hold CTRL to delete this secret key entry");
                    
                    if (keyInUse)
                    {
                        ElezenImgui.ColouredWrappedText("This key is currently assigned to a character and cannot be edited or deleted.", ImGuiColors.DalamudYellow);
                    }

                    if (item.Key != selectedServer.SecretKeys.Keys.LastOrDefault())
                        ImGui.Separator();
                }

                ImGui.Separator();
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add new Secret Key"))
                {
                    selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Any() ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                    {
                        FriendlyName = "New Secret Key",
                    });
                    _serverConfigurationManager.Save();
                }

                if (true) // Enable registration button for all servers
                {
                    ImGui.SameLine();
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Register a Snowcloak account (legacy method)"))
                    {
                        _registrationInProgress = true;
                        _ = Task.Run(async () => {
                            try
                            {
                                var reply = await _registerService.RegisterAccount(CancellationToken.None).ConfigureAwait(false);
                                if (!reply.Success)
                                {
                                    _logger.LogWarning("Registration failed: {err}", reply.ErrorMessage);
                                    _registrationMessage = reply.ErrorMessage;
                                    if (_registrationMessage.IsNullOrEmpty())
                                        _registrationMessage = "An unknown error occured. Please try again later.";
                                    return;
                                }
                                _registrationMessage ="New account registered.\nPlease keep a copy of your secret key in case you need to reset your plugins, or to use it on another PC.";
                                _registrationSuccess = true;
                                selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Any() ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                                {
                                    FriendlyName = string.Format(CultureInfo.InvariantCulture, "{0} {1}", reply.UID, string.Format(CultureInfo.InvariantCulture, "(registered {0:yyyy-MM-dd})", DateTime.Now)),
                                    Key = reply.SecretKey ?? ""
                                });
                                _serverConfigurationManager.Save();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Registration failed");
                                _registrationSuccess = false;
                                _registrationMessage = "An unknown error occured. Please try again later.";
                                
                            }
                            finally
                            {
                                _registrationInProgress = false;
                            }
                        }, CancellationToken.None);
                    }
                    if (_registrationInProgress)
                    {
                        ImGui.TextUnformatted("Sending request...");
                    }
                    else if (!_registrationMessage.IsNullOrEmpty())
                    {
                        if (!_registrationSuccess)
                            ImGui.TextColored(ImGuiColors.DalamudYellow, _registrationMessage);
                        else
                            ImGui.TextWrapped(_registrationMessage);
                    }
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(serviceSettingsTab))
            {
                var serverName = selectedServer.ServerName;
                var serverUri = selectedServer.ServerUri;
                var isMain = string.Equals(serverName, ApiController.SnowcloakServer, StringComparison.OrdinalIgnoreCase);
                var flags = isMain ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None;

                if (ImGui.InputText("Service URI", ref serverUri, 255, flags))
                {
                    selectedServer.ServerUri = serverUri;
                }
                if (isMain)
                {
                    _uiShared.DrawHelpText("You cannot edit the URI of the main service.");
                }

                if (ImGui.InputText("Service Name", ref serverName, 255, flags))
                {
                    selectedServer.ServerName = serverName;
                    _serverConfigurationManager.Save();
                }
                if (isMain)
                {
                    _uiShared.DrawHelpText("You cannot edit the name of the main service.");
                }

                if (!isMain && selectedServer != _serverConfigurationManager.CurrentServer)
                {
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete Service") && UiSharedService.CtrlPressed())
                    {
                        _serverConfigurationManager.DeleteServer(selectedServer);
                    }
                    _uiShared.DrawHelpText("Hold CTRL to delete this service");
                }
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private string _uidToAddForIgnore = string.Empty;
    private int _selectedEntry = -1;

    private string _uidToAddForIgnoreBlacklist = string.Empty;
    private int _selectedEntryBlacklist = -1;

    private void DrawSettingsContent()
    {
        if (_apiController.ServerState is ServerState.Connected)
        {
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture,"Service {0}:", _serverConfigurationManager.CurrentServer!.ServerName));
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.ParsedGreen, "Available");
            ImGui.SameLine();
            ImGui.TextUnformatted("(");
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.ParsedGreen, _apiController.OnlineUsers.ToString(CultureInfo.InvariantCulture));
            ImGui.SameLine();
            ImGui.TextUnformatted("Users Online");
            ImGui.SameLine();
            ImGui.TextUnformatted(")");
        }
        ImGui.Separator();
        if (ImGui.BeginTabBar("mainTabBar"))
        {
            var generalTab = "General";
            var performanceTab ="Performance";
            var storageTab = "Storage";
            var transfersTab = "Transfers";
            var serviceTab = "Service Settings";
            var chatTab = "Chat";
            var advancedTab = "Advanced";
            if (ImGui.BeginTabItem(generalTab))
            {
                DrawGeneral();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(performanceTab))
            {
                DrawPerformance();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(storageTab))
            {
                DrawFileStorageSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(transfersTab))
            {
                DrawCurrentTransfers();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(serviceTab))
            {
                ImGui.BeginDisabled(_registrationInProgress);
                DrawServerConfiguration();
                ImGui.EndTabItem();
                ImGui.EndDisabled(); // _registrationInProgress
            }

            if (ImGui.BeginTabItem(chatTab))
            {
                DrawChatConfig();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(advancedTab))
            {
                DrawAdvanced();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void UiSharedService_GposeEnd()
    {
        IsOpen = _wasOpen;
    }

    private void UiSharedService_GposeStart()
    {
        _wasOpen = IsOpen;
        IsOpen = false;
    }
    
}
