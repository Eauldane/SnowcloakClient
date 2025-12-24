using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
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
using Snowcloak.Services.Localisation;

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
    private readonly CapabilityRegistry _capabilityRegistry;
    private readonly LocalisationService _localisationService;
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
        DalamudUtilService dalamudUtilService, AccountRegistrationService registerService, CapabilityRegistry capabilityRegistry, LocalisationService localisationService) 
        : base(logger, mediator, localisationService.GetString("SettingsUi.WindowTitle", "Snowcloak Settings"), performanceCollector)
    {
        _localisationService = localisationService;
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
        _capabilityRegistry = capabilityRegistry;
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
    
    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"SettingsUi.{key}", fallback);
    }


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
        UiSharedService.ColorTextWrapped(L("BlockedTransfers.Description", "Files that you attempted to upload or download that were forbidden to be transferred by their creators will appear here. " +
                                                                           "If you see file paths from your drive here, then those files were not allowed to be uploaded. If you see hashes, those files were not allowed to be downloaded. " +
                                                                           "Ask your paired friend to send you the mod in question through other means or acquire the mod yourself."),
            ImGuiColors.DalamudGrey);

        if (ImGui.BeginTable("TransfersTable", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(
                L("BlockedTransfers.Columns.HashOrFilename", "Hash/Filename"));
            ImGui.TableSetupColumn(L("BlockedTransfers.Columns.ForbiddenBy", "Forbidden by"));

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
        _uiShared.BigText(L("Transfers.Header", "Transfer Settings"));
        
        int maxParallelDownloads = _configService.Current.ParallelDownloads;
        int downloadSpeedLimit = _configService.Current.DownloadSpeedLimitInBytes;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L("Transfers.DownloadSpeedLimit", "Global Download Speed Limit"));
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
                DownloadSpeeds.Bps => L("Transfers.SpeedUnits.Bps", "Byte/s"),
                DownloadSpeeds.KBps => L("Transfers.SpeedUnits.KBps", "KB/s"),
                DownloadSpeeds.MBps => L("Transfers.SpeedUnits.MBps", "MB/s"),
                _ => throw new NotSupportedException()
            }, (s) =>
            {
                _configService.Current.DownloadSpeedType = s;
                _configService.Save();
                Mediator.Publish(new DownloadLimitChangedMessage());
            }, _configService.Current.DownloadSpeedType);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L("Transfers.NoLimitHint", "0 = No limit/infinite"));
        
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt(L("Transfers.MaxParallelDownloads", "Maximum Parallel Downloads"), ref maxParallelDownloads, 1, 10))
        {
            _configService.Current.ParallelDownloads = maxParallelDownloads;
            _configService.Save();
        }

        ImGui.Separator();
        _uiShared.BigText(L("Transfers.UiHeader", "Transfer UI"));
        
        bool showTransferWindow = _configService.Current.ShowTransferWindow;
        if (ImGui.Checkbox(L("Transfers.ShowWindow", "Show separate transfer window"), ref showTransferWindow))
        {
            _configService.Current.ShowTransferWindow = showTransferWindow;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Transfers.ShowWindow.Help", $"The download window will show the current progress of outstanding downloads.{Environment.NewLine}{Environment.NewLine}" +
                                                              $"What do W/Q/P/D stand for?{Environment.NewLine}W = Waiting for Slot (see Maximum Parallel Downloads){Environment.NewLine}" +
                                                              $"Q = Queued on Server, waiting for queue ready signal{Environment.NewLine}" +
                                                              $"P = Processing download (aka downloading){Environment.NewLine}" +
                                                              $"D = Decompressing download"));
        if (!_configService.Current.ShowTransferWindow) ImGui.BeginDisabled();
        ImGui.Indent();
        bool editTransferWindowPosition = _uiShared.EditTrackerPosition;
        if (ImGui.Checkbox(L("Transfers.EditWindowPosition", "Edit Transfer Window position"), ref editTransferWindowPosition))
        {
            _uiShared.EditTrackerPosition = editTransferWindowPosition;
        }
        ImGui.Unindent();
        if (!_configService.Current.ShowTransferWindow) ImGui.EndDisabled();

        bool showTransferBars = _configService.Current.ShowTransferBars;
        if (ImGui.Checkbox(L("Transfers.ShowTransferBars", "Show transfer bars rendered below players"), ref showTransferBars))
        {
            _configService.Current.ShowTransferBars = showTransferBars;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Transfers.ShowTransferBars.Help", "This will render a progress bar during the download at the feet of the player you are downloading from."));
        
        if (!showTransferBars) ImGui.BeginDisabled();
        ImGui.Indent();
        bool transferBarShowText = _configService.Current.TransferBarsShowText;
        if (ImGui.Checkbox(L("Transfers.ShowDownloadText", "Show Download Text"), ref transferBarShowText))
        {
            _configService.Current.TransferBarsShowText = transferBarShowText;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Transfers.ShowDownloadText.Help", "Shows download text (amount of MiB downloaded) in the transfer bars"));
        int transferBarWidth = _configService.Current.TransferBarsWidth;
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt(L("Transfers.TransferBarWidth", "Transfer Bar Width"), ref transferBarWidth, 0, 500))
        {
            if (transferBarWidth < 10)
                transferBarWidth = 10;
            _configService.Current.TransferBarsWidth = transferBarWidth;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Transfers.TransferBarWidth.Help", "Width of the displayed transfer bars (will never be less wide than the displayed text)"));
        int transferBarHeight = _configService.Current.TransferBarsHeight;
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt(L("Transfers.TransferBarHeight", "Transfer Bar Height"), ref transferBarHeight, 0, 50))
        {
            if (transferBarHeight < 2)
                transferBarHeight = 2;
            _configService.Current.TransferBarsHeight = transferBarHeight;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Transfers.TransferBarHeight.Help", "Height of the displayed transfer bars (will never be less tall than the displayed text)"));
        bool showUploading = _configService.Current.ShowUploading;
        if (ImGui.Checkbox(L("Transfers.ShowUploading", "Show 'Uploading' text below players that are currently uploading"), ref showUploading))
        {
            _configService.Current.ShowUploading = showUploading;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Transfers.ShowUploading.Help", "This will render an 'Uploading' text at the feet of the player that is in progress of uploading data."));
        
        ImGui.Unindent();
        if (!showUploading) ImGui.BeginDisabled();
        ImGui.Indent();
        bool showUploadingBigText = _configService.Current.ShowUploadingBigText;
        if (ImGui.Checkbox(L("Transfers.LargeUploadingText", "Large font for 'Uploading' text"), ref showUploadingBigText))
        {
            _configService.Current.ShowUploadingBigText = showUploadingBigText;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Transfers.LargeUploadingText.Help", "This will render an 'Uploading' text in a larger font."));
        
        ImGui.Unindent();

        if (!showUploading) ImGui.EndDisabled();
        if (!showTransferBars) ImGui.EndDisabled();

        ImGui.Separator();
        _uiShared.BigText(L("Transfers.CurrentTransfers", "Current Transfers"));
        
        if (ImGui.BeginTabBar("TransfersTabBar"))
        {
            if (ApiController.ServerState is ServerState.Connected && ImGui.BeginTabItem("Transfers"))
            {
                ImGui.TextUnformatted(L("Transfers.UploadsHeader", "Uploads"));
                if (ImGui.BeginTable("UploadsTable", 3))
                {
                    ImGui.TableSetupColumn(L("Transfers.Table.File", "File"));
                    ImGui.TableSetupColumn(L("Transfers.Table.Uploaded", "Uploaded"));
                    ImGui.TableSetupColumn(L("Transfers.Table.Size", "Size"));
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
                ImGui.TextUnformatted(L("Transfers.DownloadsHeader", "Downloads"));
                if (ImGui.BeginTable("DownloadsTable", 4))
                {
                    ImGui.TableSetupColumn(L("Transfers.Table.User", "User"));
                    ImGui.TableSetupColumn(L("Transfers.Table.Server", "Server"));
                    ImGui.TableSetupColumn(L("Transfers.Table.Files", "Files"));
                    ImGui.TableSetupColumn(L("Transfers.Table.Download", "Download"));
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

            if (ImGui.BeginTabItem(L("Transfers.BlockedTab", "Blocked Transfers")))
            {
                DrawBlockedTransfers();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private List<(XivChatType, string)> GetSyncshellChatTypes() =>
    [
        (XivChatType.None, L("Chat.ChatType.Global", "(use global setting)")),
        (XivChatType.Debug, L("Chat.ChatType.Debug", "Debug")),
        (XivChatType.Echo, L("Chat.ChatType.Echo", "Echo")),
        (XivChatType.StandardEmote, L("Chat.ChatType.StandardEmote", "Standard Emote")),
        (XivChatType.CustomEmote, L("Chat.ChatType.CustomEmote", "Custom Emote")),
        (XivChatType.SystemMessage, L("Chat.ChatType.SystemMessage", "System Message")),
        (XivChatType.SystemError, L("Chat.ChatType.SystemError", "System Error")),
        (XivChatType.GatheringSystemMessage, L("Chat.ChatType.GatheringSystemMessage", "Gathering Message")),
        (XivChatType.ErrorMessage, L("Chat.ChatType.ErrorMessage", "Error message")),
    ];

    private void DrawChatConfig()
    {
        _lastTab = "Chat";

        _uiShared.BigText(L("Chat.Header", "Chat Settings"));
        
        var disableSyncshellChat = _configService.Current.DisableSyncshellChat;

        if (ImGui.Checkbox(L("Chat.DisableGlobally", "Disable chat globally"), ref disableSyncshellChat))
        {
            _configService.Current.DisableSyncshellChat = disableSyncshellChat;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Chat.DisableGlobally.Help", "Global setting to disable chat for all syncshells."));
        
        using var pushDisableGlobal = ImRaii.Disabled(disableSyncshellChat);

        var uiColors = _dalamudUtilService.UiColors.Value;
        int globalChatColor = _configService.Current.ChatColor;

        if (globalChatColor != 0 && !uiColors.ContainsKey(globalChatColor))
        {
            globalChatColor = 0;
            _configService.Current.ChatColor = 0;
            _configService.Save();
        }

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawColorCombo(L("Chat.TextColor", "Chat text color"), Enumerable.Concat([0], uiColors.Keys),
            i => i switch
        {
            0 => (uiColors[ChatService.DefaultColor].Dark, L("Chat.TextColor.Default", "Plugin Default")),
            _ => (uiColors[i].Dark, $"[{i}] {L("Chat.TextColor.Sample", "Sample Text")}")
        },
        i => {
            _configService.Current.ChatColor = i;
            _configService.Save();
        }, globalChatColor);

        var syncshellChatTypes = GetSyncshellChatTypes();
        int globalChatType = _configService.Current.ChatLogKind;
        int globalChatTypeIdx = syncshellChatTypes.FindIndex(x => globalChatType == (int)x.Item1);
        
        if (globalChatTypeIdx == -1)
            globalChatTypeIdx = 0;

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawCombo(L("Chat.ChatChannel", "Chat channel"), Enumerable.Range(1, syncshellChatTypes.Count - 1), i => $"{syncshellChatTypes[i].Item2}",
            i => {
            if (_configService.Current.ChatLogKind == (int)syncshellChatTypes[i].Item1)
                return;
            _configService.Current.ChatLogKind = (int)syncshellChatTypes[i].Item1;
            _chatService.PrintChannelExample(string.Format(CultureInfo.InvariantCulture, L("Chat.ChatChannel.Selection", "Selected channel: {0}"), syncshellChatTypes[i].Item2));
            _configService.Save();
        }, globalChatTypeIdx);
        _uiShared.DrawHelpText(L("Chat.ChatChannel.Help", "FFXIV chat channel to output chat messages on."));
        
        ImGui.SetWindowFontScale(0.6f);
        _uiShared.BigText(L("Chat.Chat2Integration", "\"Chat 2\" Plugin Integration"));
        ImGui.SetWindowFontScale(1.0f);

        var extraChatTags = _configService.Current.ExtraChatTags;
        if (ImGui.Checkbox(L("Chat.ExtraChat.TagMessages", "Tag messages as ExtraChat"), ref extraChatTags))
        {
            _configService.Current.ExtraChatTags = extraChatTags;
            if (!extraChatTags)
                _configService.Current.ExtraChatAPI = false;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Chat.ExtraChat.TagMessages.Help", "If enabled, messages will be filtered under the category \"ExtraChat channels: All\".\n\nThis works even if ExtraChat is also installed and enabled."));
        
        ImGui.Separator();

        _uiShared.BigText(L("Chat.Syncshell.Header", "Syncshell Settings"));
        if (!ApiController.ServerAlive)
        {
            ImGui.TextUnformatted(L("Chat.Syncshell.ConnectHint", "Connect to the server to configure individual syncshell settings."));
            return;
        }

        if (_pairManager.Groups.Count == 0)
        {
            ImGui.TextUnformatted(L("Chat.Syncshell.JoinHint", "Once you join a syncshell you can configure its chat settings here."));
            return;
        }

        foreach (var group in _pairManager.Groups.OrderBy(k => k.Key.GID, StringComparer.Ordinal))
        {
            var gid = group.Key.GID;
            using var pushId = ImRaii.PushId(gid);

            var shellConfig = _serverConfigurationManager.GetShellConfigForGid(gid);
            var shellNumber = shellConfig.ShellNumber;
            var shellEnabled = shellConfig.Enabled;
            var shellName = _serverConfigurationManager.GetNoteForGid(gid) ?? group.Key.AliasOrGID;

            if (shellEnabled)
                shellName = $"[{shellNumber}] {shellName}";

            ImGui.SetWindowFontScale(0.6f);
            _uiShared.BigText(shellName);
            ImGui.SetWindowFontScale(1.0f);

            using var pushIndent = ImRaii.PushIndent();

            if (ImGui.Checkbox(string.Format(CultureInfo.InvariantCulture, L("Chat.Syncshell.Enable", "Enable chat for this syncshell##{0}"), gid), ref shellEnabled))
            {
                // If there is an active group with the same syncshell number, pick a new one
                int nextNumber = 1;
                bool conflict = false;
                foreach (var otherGroup in _pairManager.Groups)
                {
                    if (gid.Equals(otherGroup.Key.GID, StringComparison.Ordinal)) continue;
                    var otherShellConfig = _serverConfigurationManager.GetShellConfigForGid(otherGroup.Key.GID);
                    if (otherShellConfig.Enabled && otherShellConfig.ShellNumber == shellNumber)
                        conflict = true;
                    nextNumber = Math.Max(nextNumber, otherShellConfig.ShellNumber) + 1;
                }
                if (conflict)
                    shellConfig.ShellNumber = nextNumber;
                shellConfig.Enabled = shellEnabled;
                _serverConfigurationManager.SaveShellConfigForGid(gid, shellConfig);
            }

            using var pushDisabled = ImRaii.Disabled(!shellEnabled);

            ImGui.SetNextItemWidth(50 * ImGuiHelpers.GlobalScale);

            // _uiShared.DrawCombo() remembers the selected option -- we don't want that, because the value can change
            if (ImGui.BeginCombo("Syncshell number##{gid}", $"{shellNumber}"))
            {
                // Same hard-coded number in CommandManagerService
                for (int i = 1; i <= ChatService.CommandMaxNumber; ++i)
                {
                    if (ImGui.Selectable($"{i}", i == shellNumber))
                    {
                        // Find an active group with the same syncshell number as selected, and swap it
                        // This logic can leave duplicate IDs present in the config but its not critical
                        foreach (var otherGroup in _pairManager.Groups)
                        {
                            if (gid.Equals(otherGroup.Key.GID, StringComparison.Ordinal)) continue;
                            var otherShellConfig = _serverConfigurationManager.GetShellConfigForGid(otherGroup.Key.GID);
                            if (otherShellConfig.Enabled && otherShellConfig.ShellNumber == i)
                            {
                                otherShellConfig.ShellNumber = shellNumber;
                                _serverConfigurationManager.SaveShellConfigForGid(otherGroup.Key.GID, otherShellConfig);
                                break;
                            }
                        }
                        shellConfig.ShellNumber = i;
                        _serverConfigurationManager.SaveShellConfigForGid(gid, shellConfig);
                    }
                }
                ImGui.EndCombo();
            }

            if (shellConfig.Color != 0 && !uiColors.ContainsKey(shellConfig.Color))
            {
                shellConfig.Color = 0;
                _serverConfigurationManager.SaveShellConfigForGid(gid, shellConfig);
            }

            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            _uiShared.DrawColorCombo(string.Format(CultureInfo.InvariantCulture, L("Chat.Syncshell.TextColor", "Chat text color##{0}"), gid), Enumerable.Concat([0], uiColors.Keys),
                i => i switch
            {
                0 => (uiColors[globalChatColor > 0 ? globalChatColor : ChatService.DefaultColor].Dark, L("Chat.Syncshell.TextColor.Global", "(use global setting)")),
                _ => (uiColors[i].Dark, $"[{i}] {L("Chat.Syncshell.TextColor.Sample", "Sample Text")}")
            },
            i => {
                shellConfig.Color = i;
                _serverConfigurationManager.SaveShellConfigForGid(gid, shellConfig);
            }, shellConfig.Color);

            int shellChatTypeIdx = syncshellChatTypes.FindIndex(x => shellConfig.LogKind == (int)x.Item1);

            if (shellChatTypeIdx == -1)
                shellChatTypeIdx = 0;

            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            _uiShared.DrawCombo(string.Format(CultureInfo.InvariantCulture, L("Chat.Syncshell.Channel", "Chat channel##{0}"), gid), Enumerable.Range(0, syncshellChatTypes.Count), i => $"{syncshellChatTypes[i].Item2}",
                i => {
                shellConfig.LogKind = (int)syncshellChatTypes[i].Item1;
                _serverConfigurationManager.SaveShellConfigForGid(gid, shellConfig);
            }, shellChatTypeIdx);
            _uiShared.DrawHelpText(L("Chat.Syncshell.Channel.Help", "Override the FFXIV chat channel used for this syncshell."));
        }
    }

    private void DrawAdvanced()
    {
        _lastTab = "Advanced";

        _uiShared.BigText(L("Advanced.Header", "Advanced"));
        
        bool logEvents = _configService.Current.LogEvents;
        if (ImGui.Checkbox(L("Advanced.LogEvents", "Log Event Viewer data to disk"), ref logEvents))
        {
            _configService.Current.LogEvents = logEvents;
            _configService.Save();
        }

        ImGui.SameLine(300.0f * ImGuiHelpers.GlobalScale);
        if (_uiShared.IconTextButton(FontAwesomeIcon.NotesMedical, L("Advanced.OpenEventViewer", "Open Event Viewer")))
        {
            Mediator.Publish(new UiToggleMessage(typeof(EventViewerUI)));
        }

        bool holdCombatApplication = _configService.Current.HoldCombatApplication;
        if (ImGui.Checkbox(L("Advanced.HoldApplication", "Hold application during combat"), ref holdCombatApplication))
        {
            if (!holdCombatApplication)
                Mediator.Publish(new CombatOrPerformanceEndMessage());
            _configService.Current.HoldCombatApplication = holdCombatApplication;
            _configService.Save();
        }

        bool serializedApplications = _configService.Current.SerialApplication;
        if (ImGui.Checkbox(L("Advanced.SerializedApplications", "Serialized player applications"), ref serializedApplications))
        {
            _configService.Current.SerialApplication = serializedApplications;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Advanced.SerializedApplications.Help", "Experimental - May reduce issues in crowded areas"));
        
        ImGui.Separator();
        _uiShared.BigText(L("Advanced.Debug.Header", "Debug"));
#if DEBUG
        if (LastCreatedCharacterData != null && ImGui.TreeNode(L("Advanced.Debug.LastCreatedCharacter", "Last created character data")))
        {
            foreach (var l in JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true }).Split('\n'))
            {
                ImGui.TextUnformatted($"{l}");
            }

            ImGui.TreePop();
        }
#endif
        if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, L("Advanced.Debug.CopyCharacterData", "[DEBUG] Copy Last created Character Data to clipboard")))
        {
            if (LastCreatedCharacterData != null)
            {
                ImGui.SetClipboardText(JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true }));
            }
            else
            {
                ImGui.SetClipboardText(L("Advanced.Debug.CopyCharacterData.Error", "ERROR: No created character data, cannot copy."));
            }
        }
        UiSharedService.AttachToolTip(L("Advanced.Debug.CopyCharacterData.Help", "Use this when reporting mods being rejected from the server."));
        
        _uiShared.DrawCombo(L("Advanced.LogLevel", "Log Level"), Enum.GetValues<LogLevel>(), (l) => l.ToString(), (l) =>
        {
            _configService.Current.LogLevel = l;
            _configService.Save();
        }, _configService.Current.LogLevel);

        bool logPerformance = _configService.Current.LogPerformance;
        if (ImGui.Checkbox(L("Advanced.LogPerformance", "Log Performance Counters"), ref logPerformance))
        {
            _configService.Current.LogPerformance = logPerformance;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Advanced.LogPerformance.Help", "Enabling this can incur a (slight) performance impact. Enabling this for extended periods of time is not recommended."));
        
        using (ImRaii.Disabled(!logPerformance))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, L("Advanced.PerformanceStats.Print", "Print Performance Stats to /xllog")))
            {
                _performanceCollector.PrintPerformanceStats();
            }
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, L("Advanced.PerformanceStats.PrintLast60", "Print Performance Stats (last 60s) to /xllog")))
            {
                _performanceCollector.PrintPerformanceStats(60);
            }
        }

        if (ImGui.TreeNode(L("Advanced.ActiveCharacterBlocks", "Active Character Blocks")))
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
        ImGui.Separator();
        _uiShared.BigText(L("Advanced.Capabilities.Header", "Client Capability Levels"));
        ImGui.TextWrapped(L("Advanced.Capabilities.Description", "This section details the current capability levels of your client. This information is " +
                                                                 "primarily used for debugging purposes, and will be used in future version to " +
                                                                 "ensure servers communicate with your client in a way it can understand."));
        var capabilities = _capabilityRegistry.GetCapabilities();
        if (ImGui.BeginTable("capabilities", 2))
        {
            ImGui.TableSetupColumn(
                L("Advanced.Capabilities.Function", "Function"));
            ImGui.TableSetupColumn(L("Advanced.Capabilities.Level", "Capability Level"));

            ImGui.TableHeadersRow();
            foreach (var item in capabilities)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(_capabilityRegistry.GetCapabilityFullName(item.Key));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Value.ToString());
            }

            ImGui.EndTable();
        }
    }

    private void DrawFileStorageSettings()
    {
        _lastTab = "FileCache";

        _uiShared.BigText(L("Storage.Header", "Storage"));
        
        UiSharedService.TextWrapped(L("Storage.Description", "Snowcloak stores downloaded files from paired people permanently. This is to improve loading performance and requiring less downloads. " +
                                                             "The storage governs itself by clearing data beyond the set storage size. Please set the storage size accordingly. It is not necessary to manually clear the storage."));

        _uiShared.DrawFileScanState();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L("Storage.MonitoringPenumbra", "Monitoring Penumbra Folder: ") + (_cacheMonitor.PenumbraWatcher?.Path ?? L("Storage.NotMonitoring", "Not monitoring")));
        if (string.IsNullOrEmpty(_cacheMonitor.PenumbraWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("penumbraMonitor");
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsToCircle, L("Storage.ReinitMonitor", "Try to reinitialize Monitor")))
            {
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
            }
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L("Storage.MonitoringSnowcloak", "Monitoring Snowcloak Storage Folder: ") + (_cacheMonitor.SnowWatcher?.Path ?? L("Storage.NotMonitoring", "Not monitoring")));
        if (string.IsNullOrEmpty(_cacheMonitor.SnowWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("snowMonitor");
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsToCircle, L("Storage.ReinitMonitor", "Try to reinitialize Monitor")))
            {
                _cacheMonitor.StartSnowWatcher(_configService.Current.CacheFolder);
            }
        }
        if (_cacheMonitor.SnowWatcher == null || _cacheMonitor.PenumbraWatcher == null)
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Play, L("Storage.ResumeMonitoring", "Resume Monitoring")))
            {
                _cacheMonitor.StartSnowWatcher(_configService.Current.CacheFolder);
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
                _cacheMonitor.InvokeScan();
            }
            UiSharedService.AttachToolTip(L("Storage.ResumeMonitoring.Help", "Attempts to resume monitoring for both Penumbra and Snowcloak Storage. "
                                                                             + "Resuming the monitoring will also force a full scan to run." + Environment.NewLine
                                                                             + "If the button remains present after clicking it, consult /xllog for errors"));
        }
        else
        {
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.Stop, L("Storage.StopMonitoring", "Stop Monitoring")))
                {
                    _cacheMonitor.StopMonitoring();
                }
            }
            UiSharedService.AttachToolTip(L("Storage.StopMonitoring.Help", "Stops the monitoring for both Penumbra and Snowcloak Storage. "
                                                                           + "Do not stop the monitoring, unless you plan to move the Penumbra and Snowcloak Storage folders, to ensure correct functionality of Snowcloak." + Environment.NewLine
                                                                           + "If you stop the monitoring to move folders around, resume it after you are finished moving the files."
                                                                           + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button"));
        }

        _uiShared.DrawCacheDirectorySetting();
        ImGui.AlignTextToFramePadding();
        if (_cacheMonitor.FileCacheSize >= 0)
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, L("Storage.Utilized", "Currently utilized local storage: {0:0.00} GiB"), _cacheMonitor.FileCacheSize / 1024.0 / 1024.0 / 1024.0));
        else
            ImGui.TextUnformatted(L("Storage.Utilized.Calculating", "Currently utilized local storage: Calculating..."));
        bool isLinux = _dalamudUtilService.IsWine;
        if (!isLinux)
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, L("Storage.RemainingSpace", "Remaining space free on drive: {0:0.00} GiB"), _cacheMonitor.FileCacheDriveFree / 1024.0 / 1024.0 / 1024.0));
        bool useFileCompactor = _configService.Current.UseCompactor;
        if (!useFileCompactor && !isLinux)
        {
            UiSharedService.ColorTextWrapped(L("Storage.CompactorHint", "Hint: To free up space when using Snowcloak consider enabling the File Compactor"), ImGuiColors.DalamudYellow);
        }
        if (isLinux || !_cacheMonitor.StorageisNTFS) ImGui.BeginDisabled();
        if (ImGui.Checkbox(L("Storage.UseCompactor", "Use file compactor"), ref useFileCompactor))
        {
            _configService.Current.UseCompactor = useFileCompactor;
            _configService.Save();
            if (!isLinux)
            {
                _fileCompactor.CompactStorage(useFileCompactor);
            }
        }
        _uiShared.DrawHelpText(L("Storage.UseCompactor.Help", "The file compactor can massively reduce your saved files. It might incur a minor penalty on loading files on a slow CPU." + Environment.NewLine
            + "It is recommended to leave it enabled to save on space."));
        if (isLinux || !_cacheMonitor.StorageisNTFS)
        {
            ImGui.EndDisabled();
            ImGui.TextUnformatted(L("Storage.CompactorUnavailable", "The file compactor is only available on Windows and NTFS drives."));
        }
        
        bool useMultithreadedCompression = _configService.Current.UseMultithreadedCompression;
        if (ImGui.Checkbox(L("Storage.MultithreadedCompression", "Enable multithreaded compression"), ref useMultithreadedCompression))
        {
            _configService.Current.UseMultithreadedCompression = useMultithreadedCompression;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Storage.MultithreadedCompression.Help", "When enabled, compression will use a number of workers equal to your CPU thread count. This will alter performance characteristics with different results based on your CPU, enable/disable based on your experience."));
        int compressionLevel = _configService.Current.CompressionLevel;
        if (ImGui.SliderInt(L("Storage.CompressionLevel", "Compression level"), ref compressionLevel, 3, 9, "%d"))
        {
            compressionLevel = Math.Clamp(compressionLevel, 2, 9);
            _configService.Current.CompressionLevel = compressionLevel;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("Storage.CompressionLevel.Help", "Higher compression levels create smaller uploads. This uses more of your CPU, but allows sync partners to download faster. Level 3 is the default."));
        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));
        ImGui.Separator();
        UiSharedService.TextWrapped(L("Storage.Validation.Description", "File Storage validation can make sure that all files in your local storage folder are valid. " +
                                                                        "Run the validation before you clear the Storage for no reason. " + Environment.NewLine +
                                                                        "This operation, depending on how many files you have in your storage, can take a while and will be CPU and drive intensive."));
        using (ImRaii.Disabled(_validationTask != null && !_validationTask.IsCompleted))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Check, L("Storage.Validation.Start", "Start File Storage Validation")))
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
            if (_uiShared.IconTextButton(FontAwesomeIcon.Times, L("Storage.Validation.Cancel", "Cancel")))
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
                    UiSharedService.TextWrapped(string.Format(CultureInfo.InvariantCulture, L("Storage.Validation.Completed", "The storage validation has completed and removed {0} invalid files from storage."), _validationTask.Result.Count));
                }
                else
                {
                    UiSharedService.TextWrapped(string.Format(CultureInfo.InvariantCulture, L("Storage.Validation.Running", "Storage validation is running: {0}/{1}"), _currentProgress.Item1, _currentProgress.Item2));
                    UiSharedService.TextWrapped(string.Format(CultureInfo.InvariantCulture, L("Storage.Validation.CurrentItem", "Current item: {0}"), _currentProgress.Item3.ResolvedFilepath));
                }
            }
        }
        ImGui.Separator();

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));
        ImGui.TextUnformatted(L("Storage.ClearDisclaimer", "To clear the local storage accept the following disclaimer"));
        ImGui.Indent();
        ImGui.Checkbox("##readClearCache", ref _readClearCache);
        ImGui.SameLine();
        UiSharedService.TextWrapped(L("Storage.ClearDisclaimer.Body", "I understand that: "
                                                                      + Environment.NewLine + "- By clearing the local storage I put the file servers of my connected service under extra strain by having to redownload all data."
                                                                      + Environment.NewLine + "- This is not a step to try to fix sync issues."
                                                                      + Environment.NewLine + "- This can make the situation of not getting other players data worse in situations of heavy file server load."));
        if (!_readClearCache)
            ImGui.BeginDisabled();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, L("Storage.Clear", "Clear local storage")) && UiSharedService.CtrlPressed() && _readClearCache)
        {
            _ = Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(_configService.Current.CacheFolder))
                {
                    File.Delete(file);
                }
            });
        }
        UiSharedService.AttachToolTip(L("Storage.Clear.Help", "You normally do not need to do this. THIS IS NOT SOMETHING YOU SHOULD BE DOING TO TRY TO FIX SYNC ISSUES." + Environment.NewLine
            + "This will solely remove all downloaded data from all players and will require you to re-download everything again." + Environment.NewLine
            + "Snowcloak's storage is self-clearing and will not surpass the limit you have set it to." + Environment.NewLine
            + "If you still think you need to do this hold CTRL while pressing the button."));
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

        _uiShared.BigText(L("General.Notes.Header", "Notes"));
        if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, L("General.Notes.Export", "Export all your user notes to clipboard")))
        {
            ImGui.SetClipboardText(UiSharedService.GetNotes(_pairManager.DirectPairs.UnionBy(_pairManager.GroupPairs.SelectMany(p => p.Value), p => p.UserData, UserDataComparer.Instance).ToList()));
        }
        if (_uiShared.IconTextButton(FontAwesomeIcon.FileImport, L("General.Notes.Import", "Import notes from clipboard")))
        {
            _notesSuccessfullyApplied = null;
            var notes = ImGui.GetClipboardText();
            _notesSuccessfullyApplied = _uiShared.ApplyNotesFromClipboard(notes, _overwriteExistingLabels);
        }

        ImGui.SameLine();
        ImGui.Checkbox(L("General.Notes.Overwrite", "Overwrite existing notes"), ref _overwriteExistingLabels);
        _uiShared.DrawHelpText(L("General.Notes.Overwrite.Help", "If this option is selected all already existing notes for UIDs will be overwritten by the imported notes."));
        if (_notesSuccessfullyApplied.HasValue && _notesSuccessfullyApplied.Value)
        {
            UiSharedService.ColorTextWrapped(L("General.Notes.Import.Success", "User Notes successfully imported"), ImGuiColors.HealerGreen);
        }
        else if (_notesSuccessfullyApplied.HasValue && !_notesSuccessfullyApplied.Value)
        {
            UiSharedService.ColorTextWrapped(L("General.Notes.Import.Failure", "Attempt to import notes from clipboard failed. Check formatting and try again"), ImGuiColors.DalamudRed);
        }

        var openPopupOnAddition = _configService.Current.OpenPopupOnAdd;

        if (ImGui.Checkbox(L("General.Notes.OpenPopup", "Open Notes Popup on user addition"), ref openPopupOnAddition))
        {
            _configService.Current.OpenPopupOnAdd = openPopupOnAddition;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Notes.OpenPopup.Help", "This will open a popup that allows you to set the notes for a user after successfully adding them to your individual pairs."));
        
        var autofillNotes = _configService.Current.AutofillEmptyNotesFromCharaName;
        if (ImGui.Checkbox(L("General.Notes.Autofill", "Automatically update empty notes with player names"), ref autofillNotes))
        {
            _configService.Current.AutofillEmptyNotesFromCharaName = autofillNotes;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Notes.Autofill.Help", "This will automatically set a user's note with their player name unless you override it"));
        
        ImGui.Separator();
        _uiShared.BigText(L("General.Venues.Header", "Venues"));
        var autoJoinVenues = _configService.Current.AutoJoinVenueSyncshells;
        if (ImGui.Checkbox(L("General.Venues.AutoJoin", "Show prompts to join venue syncshells when on their grounds"), ref autoJoinVenues))
        {
            _configService.Current.AutoJoinVenueSyncshells = autoJoinVenues;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Venues.AutoJoin.Help", "Automatically detects venue housing plots and offers users an option to join them."));
        
        ImGui.Separator();
        _uiShared.BigText(L("General.Ui.Header", "UI"));
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

        if (ImGui.Checkbox(L("General.Ui.EnableRightClick", "Enable Game Right Click Menu Entries"), ref enableRightClickMenu))
        {
            _configService.Current.EnableRightClickMenus = enableRightClickMenu;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Ui.EnableRightClick.Help", "This will add Snowcloak related right click menu entries in the game UI on paired players."));
        
        if (ImGui.Checkbox(L("General.Ui.DtrEntry", "Display status and visible pair count in Server Info Bar"), ref enableDtrEntry))
        {
            _configService.Current.EnableDtrEntry = enableDtrEntry;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Ui.DtrEntry.Help", "This will add Snowcloak connection status and visible pair count in the Server Info Bar.\nYou can further configure this through your Dalamud Settings."));
        
        using (ImRaii.Disabled(!enableDtrEntry))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox(L("General.Ui.DtrEntry.ShowUid", "Show visible character's UID in tooltip"), ref showUidInDtrTooltip))
            {
                _configService.Current.ShowUidInDtrTooltip = showUidInDtrTooltip;
                _configService.Save();
            }

            if (ImGui.Checkbox(L("General.Ui.DtrEntry.PreferNote", "Prefer notes over player names in tooltip"), ref preferNoteInDtrTooltip))
            {
                _configService.Current.PreferNoteInDtrTooltip = preferNoteInDtrTooltip;
                _configService.Save();
            }

            ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
            _uiShared.DrawCombo(L("General.Ui.DtrEntry.Style", "Server Info Bar style"), Enumerable.Range(0, DtrEntry.NumStyles), (i) => DtrEntry.RenderDtrStyle(i, "123"),
                (i) =>
            {
                _configService.Current.DtrStyle = i;
                _configService.Save();
            }, _configService.Current.DtrStyle);

            if (ImGui.Checkbox(L("General.Ui.DtrEntry.UseColors", "Color-code the Server Info Bar entry according to status"), ref useColorsInDtr))
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
                    if (InputDtrColors(L("General.Ui.DtrEntry.Colors.Default", "Default"), ref dtrColorsDefault))
                    {
                        _configService.Current.DtrColorsDefault = dtrColorsDefault;
                        _configService.Save();
                    }

                    ImGui.TableNextColumn();
                    if (InputDtrColors(L("General.Ui.DtrEntry.Colors.NotConnected", "Not Connected"), ref dtrColorsNotConnected))
                    {
                        _configService.Current.DtrColorsNotConnected = dtrColorsNotConnected;
                        _configService.Save();
                    }

                    ImGui.TableNextColumn();
                    if (InputDtrColors(L("General.Ui.DtrEntry.Colors.PairsInRange", "Pairs in Range"), ref dtrColorsPairsInRange))
                    {
                        _configService.Current.DtrColorsPairsInRange = dtrColorsPairsInRange;
                        _configService.Save();
                    }

                    ImGui.TableNextColumn();
                    if (InputDtrColors(L("General.Ui.DtrEntry.Colors.PendingRequests", "Pending Requests"), ref dtrColorsPendingRequests))
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
        if (ImGui.Checkbox(L("General.Ui.NameplateColors", "Color nameplates of paired players"), ref useNameColors))
        {
            _configService.Current.UseNameColors = useNameColors;
            _configService.Save();
            _guiHookService.RequestRedraw();
        }

        using (ImRaii.Disabled(!useNameColors))
        {
            using var indent = ImRaii.PushIndent();
            if (InputDtrColors(L("General.Ui.NameplateColors.Character", "Character Name Color"), ref nameColors))
            {
                _configService.Current.NameColors = nameColors;
                _configService.Save();
                _guiHookService.RequestRedraw();
            }

            ImGui.SameLine();

            if (InputDtrColors(L("General.Ui.NameplateColors.Blocked", "Blocked Character Color"), ref autoPausedNameColors))
            {
                _configService.Current.BlockedNameColors = autoPausedNameColors;
                _configService.Save();
                _guiHookService.RequestRedraw();
            }
        }
        
        if (ImGui.Checkbox(L("General.Ui.VisibleGroup", "Show separate Visible group"), ref showVisibleSeparate))
        {
            _configService.Current.ShowVisibleUsersSeparately = showVisibleSeparate;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Ui.VisibleGroup.Help", "This will show all currently visible users in a special 'Visible' group in the main UI."));
        if (ImGui.Checkbox(L("General.Ui.SortByVram", "Sort visible syncshell users by VRAM usage"), ref sortSyncshellByVRAM))
        {
            _configService.Current.SortSyncshellsByVRAM = sortSyncshellByVRAM;
            _logger.LogWarning("Changing value: {sortSyncshellsByVRAM}", sortSyncshellByVRAM);

            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Ui.SortByVram.Help", "This will put users using the most VRAM in a syncshell at the top of the list."));
        if (ImGui.Checkbox(L("General.Ui.GroupByStatus", "Group users by connection status"), ref showOfflineSeparate))
        {
            _configService.Current.ShowOfflineUsersSeparately = showOfflineSeparate;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Ui.GroupByStatus.Help", "This will categorize users by their connection status in the main UI."));
        
        if (ImGui.Checkbox(L("General.Ui.ShowPlayerNames", "Show player names"), ref showCharacterNames))
        {
            _configService.Current.ShowCharacterNames = showCharacterNames;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Ui.ShowPlayerNames.Help", "This will show character names instead of UIDs when possible"));
        
        if (ImGui.Checkbox(L("General.Ui.ShowProfiles", "Show Profiles on Hover"), ref showProfiles))
        {
            Mediator.Publish(new ClearProfileDataMessage());
            _configService.Current.ProfilesShow = showProfiles;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Ui.ShowProfiles.Help", "This will show the configured user profile after a set delay"));
        ImGui.Indent();
        if (!showProfiles) ImGui.BeginDisabled();
        if (ImGui.Checkbox(L("General.Ui.ProfileOnRight", "Popout profiles on the right"), ref profileOnRight))
        {
            _configService.Current.ProfilePopoutRight = profileOnRight;
            _configService.Save();
            Mediator.Publish(new CompactUiChange(Vector2.Zero, Vector2.Zero));
        }
        _uiShared.DrawHelpText(L("General.Ui.ProfileOnRight.Help", "Will show profiles on the right side of the main UI"));
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat(L("General.Ui.HoverDelay", "Hover Delay"), ref profileDelay, 1, 10))
        {
            _configService.Current.ProfileDelay = profileDelay;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Ui.HoverDelay.Help", "Delay until the profile should be displayed"));
        if (!showProfiles) ImGui.EndDisabled();
        ImGui.Unindent();
        if (ImGui.Checkbox(L("General.Ui.ShowNsfw", "Show profiles marked as NSFW"), ref showNsfwProfiles))
        {
            Mediator.Publish(new ClearProfileDataMessage());
            _configService.Current.ProfilesAllowNsfw = showNsfwProfiles;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Ui.ShowNsfw.Help", "Will show profiles that have the NSFW tag enabled"));
        
        if (ImGui.Checkbox(L("General.Ui.RenderBbcodeImages", "Render BBCode images"), ref allowBbCodeImages))
        {
            _configService.Current.AllowBbCodeImages = allowBbCodeImages;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Ui.RenderBbcodeImages.Help", "Disable this to show [img] tags as text instead of loading external images."));
        
        ImGui.Separator();

        var disableOptionalPluginWarnings = _configService.Current.DisableOptionalPluginWarnings;
        var onlineNotifs = _configService.Current.ShowOnlineNotifications;
        var onlineNotifsPairsOnly = _configService.Current.ShowOnlineNotificationsOnlyForIndividualPairs;
        var onlineNotifsNamedOnly = _configService.Current.ShowOnlineNotificationsOnlyForNamedPairs;
        _uiShared.BigText(L("General.Notifications.Header", "Notifications"));
        
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawCombo(L("General.Notifications.InfoDisplay", "Info Notification Display##settingsUi"), (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
            (i) =>
        {
            _configService.Current.InfoNotification = i;
            _configService.Save();
        }, _configService.Current.InfoNotification);
        _uiShared.DrawHelpText(L("General.Notifications.InfoDisplay.Help", "The location where \"Info\" notifications will display."
                                                                           + Environment.NewLine + "'Nowhere' will not show any Info notifications"
                                                                           + Environment.NewLine + "'Chat' will print Info notifications in chat"
                                                                           + Environment.NewLine + "'Toast' will show Warning toast notifications in the bottom right corner"
                                                                           + Environment.NewLine + "'Both' will show chat as well as the toast notification"));

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawCombo(L("General.Notifications.WarningDisplay", "Warning Notification Display##settingsUi"), (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
            (i) =>
        {
            _configService.Current.WarningNotification = i;
            _configService.Save();
        }, _configService.Current.WarningNotification);
        _uiShared.DrawHelpText(L("General.Notifications.WarningDisplay.Help", "The location where \"Warning\" notifications will display."
                                                                              + Environment.NewLine + "'Nowhere' will not show any Warning notifications"
                                                                              + Environment.NewLine + "'Chat' will print Warning notifications in chat"
                                                                              + Environment.NewLine + "'Toast' will show Warning toast notifications in the bottom right corner"
                                                                              + Environment.NewLine + "'Both' will show chat as well as the toast notification"));

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawCombo(L("General.Notifications.ErrorDisplay", "Error Notification Display##settingsUi"), (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
            (i) =>
        {
            _configService.Current.ErrorNotification = i;
            _configService.Save();
        }, _configService.Current.ErrorNotification);
        _uiShared.DrawHelpText(L("General.Notifications.ErrorDisplay.Help", "The location where \"Error\" notifications will display."
                                                                            + Environment.NewLine + "'Nowhere' will not show any Error notifications"
                                                                            + Environment.NewLine + "'Chat' will print Error notifications in chat"
                                                                            + Environment.NewLine + "'Toast' will show Error toast notifications in the bottom right corner"
                                                                            + Environment.NewLine + "'Both' will show chat as well as the toast notification"));
        
        if (ImGui.Checkbox(L("General.Notifications.DisableOptionalWarnings", "Disable optional plugin warnings"), ref disableOptionalPluginWarnings))
        {
            _configService.Current.DisableOptionalPluginWarnings = disableOptionalPluginWarnings;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Notifications.DisableOptionalWarnings.Help", "Enabling this will not show any \"Warning\" labeled messages for missing optional plugins."));
        if (ImGui.Checkbox(L("General.Notifications.EnableOnline", "Enable online notifications"), ref onlineNotifs))
        {
            _configService.Current.ShowOnlineNotifications = onlineNotifs;
            _configService.Save();
        }
        _uiShared.DrawHelpText(L("General.Notifications.EnableOnline.Help", "Enabling this will show a small notification (type: Info) in the bottom right corner when pairs go online."));
        
        using (ImRaii.Disabled(!onlineNotifs))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox(L("General.Notifications.OnlyIndividualPairs", "Notify only for individual pairs"), ref onlineNotifsPairsOnly))
            {
                _configService.Current.ShowOnlineNotificationsOnlyForIndividualPairs = onlineNotifsPairsOnly;
                _configService.Save();
            }
            _uiShared.DrawHelpText(L("General.Notifications.OnlyIndividualPairs.Help", "Enabling this will only show online notifications (type: Info) for individual pairs."));
            if (ImGui.Checkbox(L("General.Notifications.OnlyNamedPairs", "Notify only for named pairs"), ref onlineNotifsNamedOnly))
            {
                _configService.Current.ShowOnlineNotificationsOnlyForNamedPairs = onlineNotifsNamedOnly;
                _configService.Save();
            }
            _uiShared.DrawHelpText(L("General.Notifications.OnlyNamedPairs.Help", "Enabling this will only show online notifications (type: Info) for pairs where you have set an individual note."));
        }
    }

    private bool _perfUnapplied = false;

    private void DrawPerformance()
    {
        _uiShared.BigText(L("Performance.Header", "Performance Settings"));
        UiSharedService.TextWrapped(L("Performance.Description", "The configuration options here are to give you more informed warnings and automation when it comes to other performance-intensive synced players."));
        ImGui.Separator();
        bool recalculatePerformance = false;
        string? recalculatePerformanceUID = null;

        _uiShared.BigText(L("Performance.GlobalConfiguration", "Global Configuration"));
        
        bool alwaysShrinkTextures = _playerPerformanceConfigService.Current.TextureShrinkMode == TextureShrinkMode.Always;
        bool deleteOriginalTextures = _playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal;

        using (ImRaii.Disabled(deleteOriginalTextures))
        {
            if (ImGui.Checkbox(L("Performance.ShrinkTextures", "Shrink downloaded textures"), ref alwaysShrinkTextures))
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
        _uiShared.DrawHelpText(L("Performance.ShrinkTextures.Help", "Automatically shrinks texture resolution of synced players to reduce VRAM utilization." 
                                                                    + UiSharedService.TooltipSeparator + "Texture Size Limit (DXT/BC5/BC7 Compressed): 2048x2048" + Environment.NewLine
                                                                    + "Texture Size Limit (A8R8G8B8 Uncompressed): 1024x1024" + UiSharedService.TooltipSeparator
                                                                    + "Enable to reduce lag in large crowds." + Environment.NewLine
                                                                    + "Disable this for higher quality during GPose."));
        using (ImRaii.Disabled(!alwaysShrinkTextures || _cacheMonitor.FileCacheSize < 0))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox(L("Performance.DeleteOriginalTextures", "Delete original textures from disk"), ref deleteOriginalTextures))
            {
                _playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal = deleteOriginalTextures;
                _playerPerformanceConfigService.Save();
                _ = Task.Run(() =>
                {
                    _cacheMonitor.DeleteSubstOriginals();
                    _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                });
            }
            _uiShared.DrawHelpText(L("Performance.DeleteOriginalTextures.Help", "Deletes original, full-sized, textures from disk after downloading and shrinking." + UiSharedService.TooltipSeparator
                + "Caution!!! This will cause a re-download of all textures when the shrink option is disabled."));
        }

        var totalVramBytes = _pairManager.GetOnlineUserPairs().Where(p => p.IsVisible && p.LastAppliedApproximateVRAMBytes > 0).Sum(p => p.LastAppliedApproximateVRAMBytes);

        ImGui.TextUnformatted(L("Performance.CurrentVram", "Current VRAM utilization by all nearby players:"));
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, totalVramBytes < 2.0 * 1024.0 * 1024.0 * 1024.0))
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, totalVramBytes >= 4.0 * 1024.0 * 1024.0 * 1024.0))
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, totalVramBytes >= 6.0 * 1024.0 * 1024.0 * 1024.0))
                    ImGui.TextUnformatted($"{totalVramBytes / 1024.0 / 1024.0 / 1024.0:0.00} GiB");

        ImGui.Separator();
        _uiShared.BigText(L("Performance.IndividualLimits", "Individual Limits"));
        bool autoPause = _playerPerformanceConfigService.Current.AutoPausePlayersExceedingThresholds;
        if (ImGui.Checkbox(L("Performance.AutoPause", "Automatically block players exceeding thresholds"), ref autoPause))
        {
            _playerPerformanceConfigService.Current.AutoPausePlayersExceedingThresholds = autoPause;
            _playerPerformanceConfigService.Save();
            recalculatePerformance = true;
        }
        _uiShared.DrawHelpText(L("Performance.AutoPause.Help", "When enabled, it will automatically block the modded appearance of all players that exceed the thresholds defined below." + Environment.NewLine
            + "Will print a warning in chat when a player is blocked automatically."));
        using (ImRaii.Disabled(!autoPause))
        {
            using var indent = ImRaii.PushIndent();
            var notifyDirectPairs = _playerPerformanceConfigService.Current.NotifyAutoPauseDirectPairs;
            var notifyGroupPairs = _playerPerformanceConfigService.Current.NotifyAutoPauseGroupPairs;
            if (ImGui.Checkbox(L("Performance.AutoPause.NotifyDirect", "Display auto-block warnings for individual pairs"), ref notifyDirectPairs))
            {
                _playerPerformanceConfigService.Current.NotifyAutoPauseDirectPairs = notifyDirectPairs;
                _playerPerformanceConfigService.Save();
            }
            if (ImGui.Checkbox(L("Performance.AutoPause.NotifyGroup", "Display auto-block warnings for syncshell pairs"), ref notifyGroupPairs))
            {
                _playerPerformanceConfigService.Current.NotifyAutoPauseGroupPairs = notifyGroupPairs;
                _playerPerformanceConfigService.Save();
            }
            var vramAuto = _playerPerformanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB;
            var trisAuto = _playerPerformanceConfigService.Current.TrisAutoPauseThresholdThousands;
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt(L("Performance.AutoPause.VramThreshold", "Auto Block VRAM threshold"), ref vramAuto))
            {
                _playerPerformanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB = vramAuto;
                _playerPerformanceConfigService.Save();
                _perfUnapplied = true;
            }
            ImGui.SameLine();
            ImGui.Text(L("Performance.AutoPause.VramThreshold.Unit", "(MiB)"));
            _uiShared.DrawHelpText(L("Performance.AutoPause.VramThreshold.Help", "When a loading in player and their VRAM usage exceeds this amount, automatically blocks the synced player." + UiSharedService.TooltipSeparator
                + "Default: 550 MiB"));
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt(L("Performance.AutoPause.TriangleThreshold", "Auto Block Triangle threshold"), ref trisAuto))
            {
                _playerPerformanceConfigService.Current.TrisAutoPauseThresholdThousands = trisAuto;
                _playerPerformanceConfigService.Save();
                _perfUnapplied = true;
            }
            ImGui.SameLine();
            ImGui.Text(L("Performance.AutoPause.TriangleThreshold.Unit", "(thousand triangles)"));
            _uiShared.DrawHelpText(L("Performance.AutoPause.TriangleThreshold.Help", "When a loading in player and their triangle count exceeds this amount, automatically blocks the synced player." + UiSharedService.TooltipSeparator
                + "Default: 375 thousand"));
            using (ImRaii.Disabled(!_perfUnapplied))
            {
                if (ImGui.Button(L("Performance.AutoPause.Apply", "Apply Changes Now")))
                {
                    recalculatePerformance = true;
                    _perfUnapplied = false;
                }
            }
        }

#region Whitelist
        ImGui.Separator();
        _uiShared.BigText(L("Performance.Whitelist.Header", "Whitelisted UIDs"));
        bool ignoreDirectPairs = _playerPerformanceConfigService.Current.IgnoreDirectPairs;
        if (ImGui.Checkbox(L("Performance.Whitelist.AllPairs", "Whitelist all individual pairs"), ref ignoreDirectPairs))
        {
            _playerPerformanceConfigService.Current.IgnoreDirectPairs = ignoreDirectPairs;
            _playerPerformanceConfigService.Save();
            recalculatePerformance = true;
        }
        _uiShared.DrawHelpText(L("Performance.Whitelist.AllPairs.Help", "Individual pairs will never be affected by auto blocks."));
        ImGui.Dummy(new Vector2(5));
        UiSharedService.TextWrapped(L("Performance.Whitelist.Description", "The entries in the list below will be not have auto block thresholds enforced."));
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var whitelistPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##whitelistuid", ref _uidToAddForIgnore, 20);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnore)))
        {
            ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
            if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, L("Performance.Whitelist.Add", "Add UID/Vanity ID to whitelist")))
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
        _uiShared.DrawHelpText(L("Performance.Whitelist.Hint", "Hint: UIDs are case sensitive.\nVanity IDs are also acceptable."));
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
                        _uiShared.IconText(FontAwesomeIcon.InfoCircle);
                        UiSharedService.AttachToolTip(string.Format(CultureInfo.InvariantCulture, L("Performance.Whitelist.LastSeen", "Last seen name: {0}"), lastSeenName));
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedEntry == -1))
        {
            using var pushId = ImRaii.PushId("deleteSelectedWhitelist");
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, L("Performance.Whitelist.Delete", "Delete selected UID")))
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
        _uiShared.BigText(L("Performance.Blacklist.Header", "Blacklisted UIDs"));
        UiSharedService.TextWrapped(L("Performance.Blacklist.Description", "The entries in the list below will never have their characters displayed."));
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var blacklistPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##uid", ref _uidToAddForIgnoreBlacklist, 20);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnoreBlacklist)))
        {
            ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
            if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, L("Performance.Blacklist.Add", "Add UID/Vanity ID to blacklist")))
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
        _uiShared.DrawHelpText(L("Performance.Blacklist.Hint", "Hint: UIDs are case sensitive.\nVanity IDs are also acceptable."));
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
                        _uiShared.IconText(FontAwesomeIcon.InfoCircle);
                        UiSharedService.AttachToolTip(string.Format(CultureInfo.InvariantCulture, L("Performance.Blacklist.LastSeen", "Last seen name: {0}"), lastSeenName));
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedEntryBlacklist == -1))
        {
            using var pushId = ImRaii.PushId("deleteSelectedBlacklist");
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, L("Performance.Blacklist.Delete", "Delete selected UID")))
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

    private bool InputDtrColors(string label, ref DtrEntry.Colors colors)
    {
        using var id = ImRaii.PushId(label);
        var innerSpacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var foregroundColor = ConvertColor(colors.Foreground);
        var glowColor = ConvertColor(colors.Glow);

        var ret = ImGui.ColorEdit3("###foreground", ref foregroundColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("General.Ui.ColorTooltip.Foreground", "Foreground Color - Set to pure black (#000000) to use the default color"));
        
        ImGui.SameLine(0.0f, innerSpacing);
        ret |= ImGui.ColorEdit3("###glow", ref glowColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L("General.Ui.ColorTooltip.Glow", "Glow Color - Set to pure black (#000000) to use the default color"));
        
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
            _uiShared.BigText(L("Service.Actions.Header", "Service Actions"));
            ImGuiHelpers.ScaledDummy(new Vector2(5, 5));
            var deleteAccountPopupTitle = L("Service.Actions.DeleteAccount.PopupTitle", "Delete your account?");
            if (ImGui.Button(L("Service.Actions.DeleteAccount", "Delete account")))
            {
                _deleteAccountPopupModalShown = true;
                ImGui.OpenPopup(deleteAccountPopupTitle);
            }

            _uiShared.DrawHelpText(L("Service.Actions.DeleteAccount.Help", "Completely deletes your currently connected account."));
            
            if (ImGui.BeginPopupModal(deleteAccountPopupTitle, ref _deleteAccountPopupModalShown, UiSharedService.PopupWindowFlags))
            {
                UiSharedService.TextWrapped(
                    L("Service.Actions.DeleteAccount.Body1", "Your account and all associated files and data on the service will be deleted."));
                UiSharedService.TextWrapped(L("Service.Actions.DeleteAccount.Body2", "Your UID will be removed from all pairing lists."));
                ImGui.TextUnformatted(L("Service.Actions.DeleteAccount.ConfirmPrompt", "Are you sure you want to continue?"));
                ImGui.Separator();
                ImGui.Spacing();

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                  ImGui.GetStyle().ItemSpacing.X) / 2;

                if (ImGui.Button(L("Service.Actions.DeleteAccount", "Delete account"), new Vector2(buttonSize, 0)))
                {
                    _ = Task.Run(ApiController.UserDelete);
                    _deleteAccountPopupModalShown = false;
                    Mediator.Publish(new SwitchToIntroUiMessage());
                }

                ImGui.SameLine();

                if (ImGui.Button(L("Service.Actions.DeleteAccount.Cancel", "Cancel##cancelDelete"), new Vector2(buttonSize, 0)))
                {
                    _deleteAccountPopupModalShown = false;
                }

                UiSharedService.SetScaledWindowSize(325);
                ImGui.EndPopup();
            }
            ImGui.Separator();
        }

        _uiShared.BigText(L("Service.Header", "Service & Character Settings"));
        
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
                UiSharedService.ColorTextWrapped(L("Service.ReconnectNotice", "For any changes to be applied to the current service you need to reconnect to the service."), ImGuiColors.DalamudYellow);
        }

        if (ImGui.BeginTabBar("serverTabBar"))
        {
            var characterAssignmentsTab = L("Service.Tabs.CharacterAssignments", "Character Assignments");
            var secretKeyTab = L("Service.Tabs.SecretKeyManagement", "Secret Key Management");
            var serviceSettingsTab = L("Service.Tabs.ServiceSettings", "Service Settings");
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

                    UiSharedService.ColorTextWrapped(L("Service.CharacterAssignments.Description", "Characters listed here will connect with the specified secret key."), ImGuiColors.DalamudYellow);
                    int i = 0;
                    foreach (var item in selectedServer.Authentications.ToList())
                    {
                        using var charaId = ImRaii.PushId("selectedChara" + i);

                        bool thisIsYou = string.Equals(playerName, item.CharacterName, StringComparison.OrdinalIgnoreCase)
                            && playerWorldId == item.WorldId;

                        if (!worldData.TryGetValue((ushort)item.WorldId, out string? worldPreview))
                            worldPreview = worldData.First().Value;

                        _uiShared.IconText(thisIsYou ? FontAwesomeIcon.Star : FontAwesomeIcon.None);

                        if (thisIsYou)
                            UiSharedService.AttachToolTip(L("Service.CharacterAssignments.CurrentCharacter", "Current character"));
                        
                        ImGui.SameLine(windowPadding + iconWidth + itemSpacing);
                        float beforeName = ImGui.GetCursorPosX();
                        ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, L("Service.CharacterAssignments.CharacterRow", "{0} @ {1}"), item.CharacterName, worldPreview));
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
                        UiSharedService.AttachToolTip(L("Service.CharacterAssignments.Delete", "Delete character assignment"));
                        i++;
                    }

                    ImGui.Separator();
                    using (_ = ImRaii.Disabled(selectedServer.Authentications.Exists(c =>
                            string.Equals(c.CharacterName, _uiShared.PlayerName, StringComparison.Ordinal)
                                && c.WorldId == _uiShared.WorldId
                    )))
                    {
                        if (_uiShared.IconTextButton(FontAwesomeIcon.User, L("Service.CharacterAssignments.AddCurrent", "Add current character")))
                        {
                            _serverConfigurationManager.AddCurrentCharacterToServer(idx);
                        }
                        ImGui.SameLine();
                    }
                }
                else
                {
                    UiSharedService.ColorTextWrapped(L("Service.CharacterAssignments.NoSecretKey", "You need to add a Secret Key first before adding Characters."), ImGuiColors.DalamudYellow);
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(secretKeyTab))
            {
                foreach (var item in selectedServer.SecretKeys.ToList())
                {
                    using var id = ImRaii.PushId("key" + item.Key);
                    var friendlyName = item.Value.FriendlyName;
                    if (ImGui.InputText(L("Service.SecretKey.DisplayName", "Secret Key Display Name"), ref friendlyName, 255))
                    {
                        item.Value.FriendlyName = friendlyName;
                        _serverConfigurationManager.Save();
                    }
                    var key = item.Value.Key;
                    var keyInUse = selectedServer.Authentications.Exists(p => p.SecretKeyIdx == item.Key);
                    if (keyInUse) ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
                    if (ImGui.InputText(L("Service.SecretKey.Key", "Secret Key"), ref key, 64, keyInUse ? ImGuiInputTextFlags.ReadOnly : default))
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
                        if (_uiShared.IconTextButton(FontAwesomeIcon.User, L("Service.SecretKey.AssignCurrent", "Assign current character")))
                        {
                            var currentAssignment = selectedServer.Authentications.Find(a =>
                                string.Equals(a.CharacterName, _uiShared.PlayerName, StringComparison.OrdinalIgnoreCase)
                                    && a.WorldId == playerWorldId
                            );

                            if (currentAssignment == null)
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
                                currentAssignment.SecretKeyIdx = item.Key;
                            }
                        }
                        if (!disableAssignment)
                            UiSharedService.AttachToolTip(string.Format(CultureInfo.InvariantCulture, L("Service.SecretKey.AssignCurrent.Tooltip", "Use this secret key for {0} @ {1}"), playerName, playerWorldName));
                    }

                    ImGui.SameLine();
                    using var disableDelete = ImRaii.Disabled(keyInUse);
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, L("Service.SecretKey.Delete", "Delete Secret Key")) && UiSharedService.CtrlPressed())
                    {
                        selectedServer.SecretKeys.Remove(item.Key);
                        _serverConfigurationManager.Save();
                    }
                    if (!keyInUse)
                        UiSharedService.AttachToolTip(L("Service.SecretKey.Delete.Help", "Hold CTRL to delete this secret key entry"));
                    
                    if (keyInUse)
                    {
                        UiSharedService.ColorTextWrapped(L("Service.SecretKey.InUse", "This key is currently assigned to a character and cannot be edited or deleted."), ImGuiColors.DalamudYellow);
                    }

                    if (item.Key != selectedServer.SecretKeys.Keys.LastOrDefault())
                        ImGui.Separator();
                }

                ImGui.Separator();
                if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, L("Service.SecretKey.Add", "Add new Secret Key")))
                {
                    selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Any() ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                    {
                        FriendlyName = L("Service.SecretKey.NewName", "New Secret Key"),
                    });
                    _serverConfigurationManager.Save();
                }

                if (true) // Enable registration button for all servers
                {
                    ImGui.SameLine();
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, L("Service.SecretKey.Register", "Register a Snowcloak account (legacy method)")))
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
                                        _registrationMessage = L("Service.SecretKey.Register.UnknownError", "An unknown error occured. Please try again later.");
                                    return;
                                }
                                _registrationMessage = L("Service.SecretKey.Register.Success", "New account registered.\nPlease keep a copy of your secret key in case you need to reset your plugins, or to use it on another PC.");
                                _registrationSuccess = true;
                                selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Any() ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                                {
                                    FriendlyName = string.Format(CultureInfo.InvariantCulture, "{0} {1}", reply.UID, string.Format(CultureInfo.InvariantCulture, L("Service.SecretKey.Register.RegistrationDate", "(registered {0:yyyy-MM-dd})"), DateTime.Now)),
                                    Key = reply.SecretKey ?? ""
                                });
                                _serverConfigurationManager.Save();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Registration failed");
                                _registrationSuccess = false;
                                _registrationMessage = L("Service.SecretKey.Register.UnknownError", "An unknown error occured. Please try again later.");
                                
                            }
                            finally
                            {
                                _registrationInProgress = false;
                            }
                        }, CancellationToken.None);
                    }
                    if (_registrationInProgress)
                    {
                        ImGui.TextUnformatted(L("Service.SecretKey.Register.InProgress", "Sending request..."));
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

                if (ImGui.InputText(L("Service.Settings.Uri", "Service URI"), ref serverUri, 255, flags))
                {
                    selectedServer.ServerUri = serverUri;
                }
                if (isMain)
                {
                    _uiShared.DrawHelpText(L("Service.Settings.Uri.Help", "You cannot edit the URI of the main service."));
                }

                if (ImGui.InputText(L("Service.Settings.Name", "Service Name"), ref serverName, 255, flags))
                {
                    selectedServer.ServerName = serverName;
                    _serverConfigurationManager.Save();
                }
                if (isMain)
                {
                    _uiShared.DrawHelpText(L("Service.Settings.Name.Help", "You cannot edit the name of the main service."));
                }

                if (!isMain && selectedServer != _serverConfigurationManager.CurrentServer)
                {
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, L("Service.Settings.Delete", "Delete Service")) && UiSharedService.CtrlPressed())
                    {
                        _serverConfigurationManager.DeleteServer(selectedServer);
                    }
                    _uiShared.DrawHelpText(L("Service.Settings.Delete.Help", "Hold CTRL to delete this service"));
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
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, L("SettingsContent.ServiceStatus", "Service {0}:"), _serverConfigurationManager.CurrentServer!.ServerName));
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.ParsedGreen, L("SettingsContent.ServiceAvailable", "Available"));
            ImGui.SameLine();
            ImGui.TextUnformatted("(");
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.ParsedGreen, _apiController.OnlineUsers.ToString(CultureInfo.InvariantCulture));
            ImGui.SameLine();
            ImGui.TextUnformatted(L("SettingsContent.UsersOnline", "Users Online"));
            ImGui.SameLine();
            ImGui.TextUnformatted(")");
        }
        ImGui.Separator();
        if (ImGui.BeginTabBar("mainTabBar"))
        {
            var generalTab = L("Tabs.General", "General");
            var performanceTab = L("Tabs.Performance", "Performance");
            var storageTab = L("Tabs.Storage", "Storage");
            var transfersTab = L("Tabs.Transfers", "Transfers");
            var serviceTab = L("Tabs.ServiceSettings", "Service Settings");
            var chatTab = L("Tabs.Chat", "Chat");
            var advancedTab = L("Tabs.Advanced", "Advanced");
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
