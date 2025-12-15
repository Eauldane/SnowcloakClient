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

namespace Snowcloak.UI;

public class SettingsUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly IpcManager _ipcManager;
    private readonly IpcProvider _ipcProvider;
    private readonly CacheMonitor _cacheMonitor;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SnowcloakConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, Dictionary<string, FileDownloadStatus>> _currentDownloads = new();
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
    private bool _snowplowEnablePopupModalShown = false;

    private bool _registrationInProgress = false;
    private bool _registrationSuccess = false;
    private string? _registrationMessage;

    private static readonly (byte RaceId, string RaceLabel, (byte ClanId, string ClanLabel)[] Clans)[] _raceClanOptions =
    [
        (1, "Hyur", new (byte, string)[] { (1, "Midlander"), (2, "Highlander") }),
        (2, "Elezen", new (byte, string)[] { (3, "Wildwood"), (4, "Duskwight") }),
        (3, "Lalafell", new (byte, string)[] { (5, "Plainsfolk"), (6, "Dunesfolk") }),
        (4, "Miqo'te", new (byte, string)[] { (7, "Seeker of the Sun"), (8, "Keeper of the Moon") }),
        (5, "Roegadyn", new (byte, string)[] { (9, "Sea Wolf"), (10, "Hellsguard") }),
        (6, "Au Ra", new (byte, string)[] { (11, "Raen"), (12, "Xaela") }),
        (7, "Hrothgar", new (byte, string)[] { (13, "Helions"), (14, "The Lost") }),
        (8, "Viera", new (byte, string)[] { (15, "Rava"), (16, "Veena") }),
    ];
    
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
        DalamudUtilService dalamudUtilService, AccountRegistrationService registerService, CapabilityRegistry capabilityRegistry) : base(logger, mediator, "Snowcloak Settings", performanceCollector)
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
        UiSharedService.ColorTextWrapped("Files that you attempted to upload or download that were forbidden to be transferred by their creators will appear here. " +
                             "If you see file paths from your drive here, then those files were not allowed to be uploaded. If you see hashes, those files were not allowed to be downloaded. " +
                             "Ask your paired friend to send you the mod in question through other means or acquire the mod yourself.",
            ImGuiColors.DalamudGrey);

        if (ImGui.BeginTable("TransfersTable", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(
                $"Hash/Filename");
            ImGui.TableSetupColumn($"Forbidden by");

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

    private static readonly List<(XivChatType, string)> _syncshellChatTypes = [
        (XivChatType.None, "(use global setting)"),
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
        _lastTab = "Chat";

        _uiShared.BigText("Chat Settings");

        var disableSyncshellChat = _configService.Current.DisableSyncshellChat;

        if (ImGui.Checkbox("Disable chat globally", ref disableSyncshellChat))
        {
            _configService.Current.DisableSyncshellChat = disableSyncshellChat;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Global setting to disable chat for all syncshells.");

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
        _uiShared.DrawColorCombo("Chat text color", Enumerable.Concat([0], uiColors.Keys),
        i => i switch
        {
            0 => (uiColors[ChatService.DefaultColor].Dark, "Plugin Default"),
            _ => (uiColors[i].Dark, $"[{i}] Sample Text")
        },
        i => {
            _configService.Current.ChatColor = i;
            _configService.Save();
        }, globalChatColor);

        int globalChatType = _configService.Current.ChatLogKind;
        int globalChatTypeIdx = _syncshellChatTypes.FindIndex(x => globalChatType == (int)x.Item1);

        if (globalChatTypeIdx == -1)
            globalChatTypeIdx = 0;

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawCombo("Chat channel", Enumerable.Range(1, _syncshellChatTypes.Count - 1), i => $"{_syncshellChatTypes[i].Item2}",
        i => {
            if (_configService.Current.ChatLogKind == (int)_syncshellChatTypes[i].Item1)
                return;
            _configService.Current.ChatLogKind = (int)_syncshellChatTypes[i].Item1;
            _chatService.PrintChannelExample($"Selected channel: {_syncshellChatTypes[i].Item2}");
            _configService.Save();
        }, globalChatTypeIdx);
        _uiShared.DrawHelpText("FFXIV chat channel to output chat messages on.");

        ImGui.SetWindowFontScale(0.6f);
        _uiShared.BigText("\"Chat 2\" Plugin Integration");
        ImGui.SetWindowFontScale(1.0f);

        var extraChatTags = _configService.Current.ExtraChatTags;
        if (ImGui.Checkbox("Tag messages as ExtraChat", ref extraChatTags))
        {
            _configService.Current.ExtraChatTags = extraChatTags;
            if (!extraChatTags)
                _configService.Current.ExtraChatAPI = false;
            _configService.Save();
        }
        _uiShared.DrawHelpText("If enabled, messages will be filtered under the category \"ExtraChat channels: All\".\n\nThis works even if ExtraChat is also installed and enabled.");

        ImGui.Separator();

        _uiShared.BigText("Syncshell Settings");

        if (!ApiController.ServerAlive)
        {
            ImGui.TextUnformatted("Connect to the server to configure individual syncshell settings.");
            return;
        }

        if (_pairManager.Groups.Count == 0)
        {
            ImGui.TextUnformatted("Once you join a syncshell you can configure its chat settings here.");
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

            if (ImGui.Checkbox($"Enable chat for this syncshell##{gid}", ref shellEnabled))
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
            _uiShared.DrawColorCombo($"Chat text color##{gid}", Enumerable.Concat([0], uiColors.Keys),
            i => i switch
            {
                0 => (uiColors[globalChatColor > 0 ? globalChatColor : ChatService.DefaultColor].Dark, "(use global setting)"),
                _ => (uiColors[i].Dark, $"[{i}] Sample Text")
            },
            i => {
                shellConfig.Color = i;
                _serverConfigurationManager.SaveShellConfigForGid(gid, shellConfig);
            }, shellConfig.Color);

            int shellChatTypeIdx = _syncshellChatTypes.FindIndex(x => shellConfig.LogKind == (int)x.Item1);

            if (shellChatTypeIdx == -1)
                shellChatTypeIdx = 0;

            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            _uiShared.DrawCombo($"Chat channel##{gid}", Enumerable.Range(0, _syncshellChatTypes.Count), i => $"{_syncshellChatTypes[i].Item2}",
            i => {
                shellConfig.LogKind = (int)_syncshellChatTypes[i].Item1;
                _serverConfigurationManager.SaveShellConfigForGid(gid, shellConfig);
            }, shellChatTypeIdx);
            _uiShared.DrawHelpText("Override the FFXIV chat channel used for this syncshell.");
        }
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
        if (_uiShared.IconTextButton(FontAwesomeIcon.NotesMedical, "Open Event Viewer"))
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

        bool serializedApplications = _configService.Current.SerialApplication;
        if (ImGui.Checkbox("Serialized player applications", ref serializedApplications))
        {
            _configService.Current.SerialApplication = serializedApplications;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Experimental - May reduce issues in crowded areas");

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
        if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, "[DEBUG] Copy Last created Character Data to clipboard"))
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
            if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Print Performance Stats to /xllog"))
            {
                _performanceCollector.PrintPerformanceStats();
            }
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Print Performance Stats (last 60s) to /xllog"))
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
        ImGui.Separator();
        _uiShared.BigText("Client Capability Levels");
        ImGui.TextWrapped("This section details the current capability levels of your client. This information is " +
                          "primarily used for debugging purposes, and will be used in future version to " +
                          "ensure servers communicate with your client in a way it can understand.");
        var capabilities = _capabilityRegistry.GetCapabilities();
        if (ImGui.BeginTable("capabilities", 2))
        {
            ImGui.TableSetupColumn(
                $"Function");
            ImGui.TableSetupColumn($"Capability Level");

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

        _uiShared.BigText("Export MCDF");

        ImGuiHelpers.ScaledDummy(10);

        UiSharedService.ColorTextWrapped("Exporting MCDF has moved.", ImGuiColors.DalamudYellow);
        ImGuiHelpers.ScaledDummy(5);
        UiSharedService.TextWrapped("It is now found in the Main UI under \"Character Data Hub\"");
        if (_uiShared.IconTextButton(FontAwesomeIcon.Running, "Open Character Data Hub"))
        {
            Mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi)));
        }

        ImGui.Separator();

        _uiShared.BigText("Storage");

        UiSharedService.TextWrapped("Snowcloak stores downloaded files from paired people permanently. This is to improve loading performance and requiring less downloads. " +
            "The storage governs itself by clearing data beyond the set storage size. Please set the storage size accordingly. It is not necessary to manually clear the storage.");

        _uiShared.DrawFileScanState();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Monitoring Penumbra Folder: " + (_cacheMonitor.PenumbraWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.PenumbraWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("penumbraMonitor");
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
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
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartSnowWatcher(_configService.Current.CacheFolder);
            }
        }
        if (_cacheMonitor.SnowWatcher == null || _cacheMonitor.PenumbraWatcher == null)
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Play, "Resume Monitoring"))
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
                if (_uiShared.IconTextButton(FontAwesomeIcon.Stop, "Stop Monitoring"))
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
            ImGui.TextUnformatted($"Currently utilized local storage: {_cacheMonitor.FileCacheSize / 1024.0 / 1024.0 / 1024.0:0.00} GiB");
        else
            ImGui.TextUnformatted($"Currently utilized local storage: Calculating...");
        bool isLinux = _dalamudUtilService.IsWine;
        if (!isLinux)
            ImGui.TextUnformatted($"Remaining space free on drive: {_cacheMonitor.FileCacheDriveFree / 1024.0 / 1024.0 / 1024.0:0.00} GiB");
        bool useFileCompactor = _configService.Current.UseCompactor;
        if (!useFileCompactor && !isLinux)
        {
            UiSharedService.ColorTextWrapped("Hint: To free up space when using Snowcloak consider enabling the File Compactor", ImGuiColors.DalamudYellow);
        }
        if (isLinux || !_cacheMonitor.StorageisNTFS) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Use file compactor", ref useFileCompactor))
        {
            _configService.Current.UseCompactor = useFileCompactor;
            _configService.Save();
            if (!isLinux)
            {
                _fileCompactor.CompactStorage(useFileCompactor);
            }
        }
        _uiShared.DrawHelpText("The file compactor can massively reduce your saved files. It might incur a minor penalty on loading files on a slow CPU." + Environment.NewLine
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
        UiSharedService.TextWrapped("File Storage validation can make sure that all files in your local storage folder are valid. " +
            "Run the validation before you clear the Storage for no reason. " + Environment.NewLine +
            "This operation, depending on how many files you have in your storage, can take a while and will be CPU and drive intensive.");
        using (ImRaii.Disabled(_validationTask != null && !_validationTask.IsCompleted))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Check, "Start File Storage Validation"))
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
            if (_uiShared.IconTextButton(FontAwesomeIcon.Times, "Cancel"))
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
                    UiSharedService.TextWrapped($"The storage validation has completed and removed {_validationTask.Result.Count} invalid files from storage.");
                }
                else
                {

                    UiSharedService.TextWrapped($"Storage validation is running: {_currentProgress.Item1}/{_currentProgress.Item2}");
                    UiSharedService.TextWrapped($"Current item: {_currentProgress.Item3.ResolvedFilepath}");
                }
            }
        }
        ImGui.Separator();

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));
        ImGui.TextUnformatted("To clear the local storage accept the following disclaimer");
        ImGui.Indent();
        ImGui.Checkbox("##readClearCache", ref _readClearCache);
        ImGui.SameLine();
        UiSharedService.TextWrapped("I understand that: " + Environment.NewLine + "- By clearing the local storage I put the file servers of my connected service under extra strain by having to redownload all data."
            + Environment.NewLine + "- This is not a step to try to fix sync issues."
            + Environment.NewLine + "- This can make the situation of not getting other players data worse in situations of heavy file server load.");
        if (!_readClearCache)
            ImGui.BeginDisabled();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Clear local storage") && UiSharedService.CtrlPressed() && _readClearCache)
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
        if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Export all your user notes to clipboard"))
        {
            ImGui.SetClipboardText(UiSharedService.GetNotes(_pairManager.DirectPairs.UnionBy(_pairManager.GroupPairs.SelectMany(p => p.Value), p => p.UserData, UserDataComparer.Instance).ToList()));
        }
        if (_uiShared.IconTextButton(FontAwesomeIcon.FileImport, "Import notes from clipboard"))
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
            UiSharedService.ColorTextWrapped("User Notes successfully imported", ImGuiColors.HealerGreen);
        }
        else if (_notesSuccessfullyApplied.HasValue && !_notesSuccessfullyApplied.Value)
        {
            UiSharedService.ColorTextWrapped("Attempt to import notes from clipboard failed. Check formatting and try again", ImGuiColors.DalamudRed);
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
                if (InputDtrColors("Default", ref dtrColorsDefault))
                {
                    _configService.Current.DtrColorsDefault = dtrColorsDefault;
                    _configService.Save();
                }

                ImGui.SameLine();
                if (InputDtrColors("Not Connected", ref dtrColorsNotConnected))
                {
                    _configService.Current.DtrColorsNotConnected = dtrColorsNotConnected;
                    _configService.Save();
                }

                ImGui.SameLine();
                if (InputDtrColors("Pairs in Range", ref dtrColorsPairsInRange))
                {
                    _configService.Current.DtrColorsPairsInRange = dtrColorsPairsInRange;
                    _configService.Save();
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
        
        if (ImGui.Checkbox("Show separate Visible group", ref showVisibleSeparate))
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
        UiSharedService.TextWrapped("The configuration options here are to give you more informed warnings and automation when it comes to other performance-intensive synced players.");
        ImGui.Separator();
        bool recalculatePerformance = false;
        string? recalculatePerformanceUID = null;

        _uiShared.BigText("Global Configuration");

        bool alwaysShrinkTextures = _playerPerformanceConfigService.Current.TextureShrinkMode == TextureShrinkMode.Always;
        bool deleteOriginalTextures = _playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal;

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
        _uiShared.DrawHelpText("Automatically shrinks texture resolution of synced players to reduce VRAM utilization." + UiSharedService.TooltipSeparator
            + "Texture Size Limit (DXT/BC5/BC7 Compressed): 2048x2048" + Environment.NewLine
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
                + "Default: 550 MiB");
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
                + "Default: 375 thousand");
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
        UiSharedService.TextWrapped("The entries in the list below will be not have auto block thresholds enforced.");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var whitelistPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##whitelistuid", ref _uidToAddForIgnore, 20);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnore)))
        {
            ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
            if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, "Add UID/Vanity ID to whitelist"))
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
                        _uiShared.IconText(FontAwesomeIcon.InfoCircle);
                        UiSharedService.AttachToolTip($"Last seen name: {lastSeenName}");
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedEntry == -1))
        {
            using var pushId = ImRaii.PushId("deleteSelectedWhitelist");
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Delete selected UID"))
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
        UiSharedService.TextWrapped("The entries in the list below will never have their characters displayed.");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var blacklistPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##uid", ref _uidToAddForIgnoreBlacklist, 20);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnoreBlacklist)))
        {
            ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
            if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, "Add UID/Vanity ID to blacklist"))
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
                        _uiShared.IconText(FontAwesomeIcon.InfoCircle);
                        UiSharedService.AttachToolTip($"Last seen name: {lastSeenName}");
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedEntryBlacklist == -1))
        {
            using var pushId = ImRaii.PushId("deleteSelectedBlacklist");
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Delete selected UID"))
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

    private static bool InputDtrColors(string label, ref DtrEntry.Colors colors)
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
            if (ImGui.Button("Delete account"))
            {
                _deleteAccountPopupModalShown = true;
                ImGui.OpenPopup("Delete your account?");
            }

            _uiShared.DrawHelpText("Completely deletes your currently connected account.");

            if (ImGui.BeginPopupModal("Delete your account?", ref _deleteAccountPopupModalShown, UiSharedService.PopupWindowFlags))
            {
                UiSharedService.TextWrapped(
                    "Your account and all associated files and data on the service will be deleted.");
                UiSharedService.TextWrapped("Your UID will be removed from all pairing lists.");
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
                UiSharedService.ColorTextWrapped("For any changes to be applied to the current service you need to reconnect to the service.", ImGuiColors.DalamudYellow);
        }

        if (ImGui.BeginTabBar("serverTabBar"))
        {
            if (ImGui.BeginTabItem("Character Assignments"))
            {
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

                    UiSharedService.ColorTextWrapped("Characters listed here will connect with the specified secret key.", ImGuiColors.DalamudYellow);
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
                            UiSharedService.AttachToolTip("Current character");

                        ImGui.SameLine(windowPadding + iconWidth + itemSpacing);
                        float beforeName = ImGui.GetCursorPosX();
                        ImGui.TextUnformatted($"{item.CharacterName} @ {worldPreview}");
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
                        if (_uiShared.IconTextButton(FontAwesomeIcon.User, "Add current character"))
                        {
                            _serverConfigurationManager.AddCurrentCharacterToServer(idx);
                        }
                        ImGui.SameLine();
                    }
                }
                else
                {
                    UiSharedService.ColorTextWrapped("You need to add a Secret Key first before adding Characters.", ImGuiColors.DalamudYellow);
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Secret Key Management"))
            {
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
                        if (_uiShared.IconTextButton(FontAwesomeIcon.User, "Assign current character"))
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
                            UiSharedService.AttachToolTip($"Use this secret key for {playerName} @ {playerWorldName}");
                    }

                    ImGui.SameLine();
                    using var disableDelete = ImRaii.Disabled(keyInUse);
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Delete Secret Key") && UiSharedService.CtrlPressed())
                    {
                        selectedServer.SecretKeys.Remove(item.Key);
                        _serverConfigurationManager.Save();
                    }
                    if (!keyInUse)
                        UiSharedService.AttachToolTip("Hold CTRL to delete this secret key entry");

                    if (keyInUse)
                    {
                        UiSharedService.ColorTextWrapped("This key is currently assigned to a character and cannot be edited or deleted.", ImGuiColors.DalamudYellow);
                    }

                    if (item.Key != selectedServer.SecretKeys.Keys.LastOrDefault())
                        ImGui.Separator();
                }

                ImGui.Separator();
                if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, "Add new Secret Key"))
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
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, "Register a Snowcloak account (legacy method)"))
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
                                _registrationMessage = "New account registered.\nPlease keep a copy of your secret key in case you need to reset your plugins, or to use it on another PC.";
                                _registrationSuccess = true;
                                selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Any() ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                                {
                                    FriendlyName = reply.UID + $" (registered {DateTime.Now:yyyy-MM-dd})",
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

            if (ImGui.BeginTabItem("Service Settings"))
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
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Delete Service") && UiSharedService.CtrlPressed())
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

    private void SetPairingSystemEnabled(bool enabled)
    {
        _configService.Current.PairingSystemEnabled = enabled;
        _configService.Save();

        _ = _pairRequestService.SyncAdvertisingAsync();

        _guiHookService.RequestRedraw();
    }

    private void DrawSnowplow()
    {
        _lastTab = "Snowplow";

        _uiShared.BigText("Snowplow Pairing");
        
        var pairingEnabled = _configService.Current.PairingSystemEnabled;
        var requestedPairingEnabled = pairingEnabled;
        if (ImGui.Checkbox("Enable Snowplow pairing features", ref requestedPairingEnabled))
        {
            if (requestedPairingEnabled && !pairingEnabled)
            {
                _snowplowEnablePopupModalShown = true;
                ImGui.OpenPopup("Enable Snowplow pairing?");
            }
            else
            {
                SetPairingSystemEnabled(requestedPairingEnabled);
            }
        }
        _uiShared.DrawHelpText("Disable to hide pairing highlights, suppress right-click pairing actions, and pause auto-rejection.");
        
        if (ImGui.BeginPopupModal("Enable Snowplow pairing?", ref _snowplowEnablePopupModalShown, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped("Snowplow pairing features make your opt-in status visible to other opted-in users to simplify pairing.");
            UiSharedService.TextWrapped("Only enable this if you understand that others can see your pairing intent and want to participate in Snowplow.");
            ImGui.Separator();
            ImGui.Spacing();

            var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                              ImGui.GetStyle().ItemSpacing.X) / 2;

            if (ImGui.Button("Confirm", new Vector2(buttonSize, 0)))
            {
                SetPairingSystemEnabled(true);
                _snowplowEnablePopupModalShown = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();

            if (ImGui.Button("Cancel##cancelSnowplowEnable", new Vector2(buttonSize, 0)))
            {
                _snowplowEnablePopupModalShown = false;
                ImGui.CloseCurrentPopup();
            }

            UiSharedService.SetScaledWindowSize(325);
            ImGui.EndPopup();
        }
        using (ImRaii.Disabled(!pairingEnabled))
        {
            ImGuiHelpers.ScaledDummy(new Vector2(0, 5));
            ImGui.Separator();

            _uiShared.BigText("Highlighting");

            var pairRequestColor = _configService.Current.PairRequestNameColors;
            if (InputDtrColors("Highlight color", ref pairRequestColor))
            {
                _configService.Current.PairRequestNameColors = pairRequestColor;
                _configService.Save();
                _guiHookService.RequestRedraw();
            }
            _uiShared.DrawHelpText("Opted-in Snowplow users are shown to you in this color while Snowplow is enabled.");

            ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
            ImGui.TextColored(ConvertColorToVec4(pairRequestColor.Foreground), "Opted-in user preview");

            ImGuiHelpers.ScaledDummy(new Vector2(0, 6));
            ImGui.Separator();

            _uiShared.BigText("Auto-reject filters");
            ImGui.TextWrapped("Snowcloak will automatically filter pair requests from the following characters if they're within" +
                              " inspection range.\n\n Note: If the sender is not within visible range, the request will show as pending and will" +
                              " be checked when they're next in range.");
            ImGuiHelpers.ScaledDummy(new Vector2(0, 2));

            var minimumLevel = _configService.Current.PairRequestMinimumLevel;
            ImGui.TextUnformatted("Reject requests below level");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("##SnowplowMinLevel", ref minimumLevel))
            {
                minimumLevel = Math.Clamp(minimumLevel, 0, 90);
                _configService.Current.PairRequestMinimumLevel = minimumLevel;
                _configService.Save();
            }
            _uiShared.DrawHelpText("Set to 0 to disable level-based rejection.");
            ImGuiHelpers.ScaledDummy(new Vector2(0, 5));
            ImGui.TextWrapped("If you don't want to interact with a certain kind of character regardless of their level, check the appropriate box" +
                              " below. Requests from matching characters will be rejected.");
            ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

            ImGui.TextWrapped("Please note: Snowcloak can only make determinations for this feature based on their unpaired, unglamoured state. For your safety," +
                              " the client does not attempt to automatically load adventurer plates.");
            foreach (var (raceId, raceLabel, clans) in _raceClanOptions)
            {
                ImGuiHelpers.ScaledDummy(new Vector2(0, 2));
                var raceLabelSize = ImGui.CalcTextSize(raceLabel);
                var availableWidth = ImGui.GetContentRegionAvail().X;
                var centeredStart = Math.Max(0, (availableWidth - raceLabelSize.X) / 2);
                var cursorX = ImGui.GetCursorPosX();
                ImGui.SetCursorPosX(cursorX + centeredStart);
                ImGui.TextUnformatted(raceLabel);
                ImGui.SetCursorPosX(cursorX);
                
                if (ImGui.BeginTable($"SnowplowRaceRow_{raceId}", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
                {
                    ImGui.TableSetupColumn("Clan");
                    ImGui.TableSetupColumn("Male", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
                    ImGui.TableSetupColumn("Female", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
                    ImGui.TableHeadersRow();

                    foreach (var (clanId, clanLabel) in clans)
                    {
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(clanLabel);
                        ImGui.TableNextColumn();
                        using (ImRaii.PushId($"{raceId}_{clanId}_M"))
                        {
                            var rejectMale = HasAutoRejectCombo(raceId, clanId, 0);
                            if (ImGui.Checkbox("M", ref rejectMale))
                            {
                                UpdateComboRejection(rejectMale, raceId, clanId, 0);
                            }
                        }

                        ImGui.TableNextColumn();
                        using (ImRaii.PushId($"{raceId}_{clanId}_F"))
                        {
                            var rejectFemale = HasAutoRejectCombo(raceId, clanId, 1);
                            if (ImGui.Checkbox("F", ref rejectFemale))
                            {
                                UpdateComboRejection(rejectFemale, raceId, clanId, 1);
                            }
                        }
                    }

                    ImGui.EndTable();
                }
            }
        }
    }
        
    private void DrawSettingsContent()
    {
        if (_apiController.ServerState is ServerState.Connected)
        {
            ImGui.TextUnformatted("Service " + _serverConfigurationManager.CurrentServer!.ServerName + ":");
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
            if (ImGui.BeginTabItem("General"))
            {
                DrawGeneral();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Performance"))
            {
                DrawPerformance();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Storage"))
            {
                DrawFileStorageSettings();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Transfers"))
            {
                DrawCurrentTransfers();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Snowplow"))
            {
                DrawSnowplow();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Service Settings"))
            {
                ImGui.BeginDisabled(_registrationInProgress);
                DrawServerConfiguration();
                ImGui.EndTabItem();
                ImGui.EndDisabled(); // _registrationInProgress
            }

            if (ImGui.BeginTabItem("Chat"))
            {
                DrawChatConfig();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Advanced"))
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
    
    private bool HasAutoRejectCombo(byte race, byte clan, byte gender)
        => _configService.Current.AutoRejectCombos.Contains(new AutoRejectCombo(race, clan, gender));
    private void UpdateComboRejection(bool enabled, byte race, byte clan, byte gender)
    {
        var key = new AutoRejectCombo(race, clan, gender);
        if (enabled)
            _configService.Current.AutoRejectCombos.Add(key);
        else
            _configService.Current.AutoRejectCombos.Remove(key);
        
        _configService.Save();
    }
}
