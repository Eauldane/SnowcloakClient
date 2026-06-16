using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Numerics;

namespace Snowcloak.UI.Components;

public sealed class TransferSettingsPanel
{
    private const string TransfersTabCurrent = "Transfers";
    private const string TransfersTabBlocked = "Blocked Transfers";

    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _configService;
    private readonly FileUploadManager _fileTransferManager;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly SnowMediator _mediator;
    private readonly DownloadStatusStore _statusStore;
    private readonly TransferOverlayUiState _transferOverlayState;
    private readonly UiFontService _fontService;
    private readonly Dictionary<string, object> _selectedComboItems = new(StringComparer.Ordinal);
    private string _transfersActiveTab = TransfersTabCurrent;

    public TransferSettingsPanel(
        ApiController apiController,
        SnowcloakConfigService configService,
        FileUploadManager fileTransferManager,
        FileTransferOrchestrator fileTransferOrchestrator,
        SnowMediator mediator,
        DownloadStatusStore statusStore,
        TransferOverlayUiState transferOverlayState,
        UiFontService fontService)
    {
        _apiController = apiController;
        _configService = configService;
        _fileTransferManager = fileTransferManager;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _mediator = mediator;
        _statusStore = statusStore;
        _transferOverlayState = transferOverlayState;
        _fontService = fontService;
    }

    public void Draw()
    {
        _fontService.BigText("Transfer Settings");

        DrawDownloadSettings();
        ImGui.Separator();
        DrawTransferUiSettings();
        ImGui.Separator();
        DrawCurrentTransfers();
    }

    private void DrawDownloadSettings()
    {
        var maxParallelDownloads = _configService.Current.ParallelDownloads;
        var downloadSpeedLimit = _configService.Current.DownloadSpeedLimitInBytes;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Global Download Speed Limit");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("###speedlimit", ref downloadSpeedLimit))
        {
            _configService.Update(c => c.DownloadSpeedLimitInBytes = downloadSpeedLimit);
            _mediator.Publish(new DownloadLimitChangedMessage());
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        SettingsUiControls.DrawCombo("###speed", new[] { DownloadSpeeds.Bps, DownloadSpeeds.KBps, DownloadSpeeds.MBps },
            s => s switch
            {
                DownloadSpeeds.Bps => "Byte/s",
                DownloadSpeeds.KBps => "KB/s",
                DownloadSpeeds.MBps => "MB/s",
                _ => throw new NotSupportedException()
            },
            _selectedComboItems,
            s =>
            {
                _configService.Update(c => c.DownloadSpeedType = s);
                _mediator.Publish(new DownloadLimitChangedMessage());
            },
            _configService.Current.DownloadSpeedType);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("0 = No limit/infinite");

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Maximum Parallel Downloads", ref maxParallelDownloads, 1, 10))
        {
            _configService.Update(c => c.ParallelDownloads = maxParallelDownloads);
        }
    }

    private void DrawTransferUiSettings()
    {
        _fontService.BigText("Transfer UI");

        var showTransferWindow = _configService.Current.ShowTransferWindow;
        if (ImGui.Checkbox("Show separate transfer window", ref showTransferWindow))
        {
            _configService.Update(c => c.ShowTransferWindow = showTransferWindow);
        }
        ElezenImgui.DrawHelpText($"The download window will show the current progress of outstanding downloads.{Environment.NewLine}{Environment.NewLine}"
            + $"What do W/Q/P/D stand for?{Environment.NewLine}W = Waiting for Slot (see Maximum Parallel Downloads){Environment.NewLine}"
            + $"Q = Queued on Server, waiting for queue ready signal{Environment.NewLine}"
            + $"P = Processing download (aka downloading){Environment.NewLine}"
            + "D = Decompressing download");

        if (!_configService.Current.ShowTransferWindow)
        {
            ImGui.BeginDisabled();
        }

        ImGui.Indent();
        var editTransferWindowPosition = _transferOverlayState.EditTrackerPosition;
        if (ImGui.Checkbox("Edit Transfer Window position", ref editTransferWindowPosition))
        {
            _transferOverlayState.EditTrackerPosition = editTransferWindowPosition;
        }
        ImGui.Unindent();

        if (!_configService.Current.ShowTransferWindow)
        {
            ImGui.EndDisabled();
        }

        DrawTransferBarSettings();
    }

    private void DrawTransferBarSettings()
    {
        var showTransferBars = _configService.Current.ShowTransferBars;
        if (ImGui.Checkbox("Show transfer bars rendered below players", ref showTransferBars))
        {
            _configService.Update(c => c.ShowTransferBars = showTransferBars);
        }
        ElezenImgui.DrawHelpText("This will render a progress bar during the download at the feet of the player you are downloading from.");

        if (!showTransferBars)
        {
            ImGui.BeginDisabled();
        }

        ImGui.Indent();
        var transferBarShowText = _configService.Current.TransferBarsShowText;
        if (ImGui.Checkbox("Show Download Text", ref transferBarShowText))
        {
            _configService.Update(c => c.TransferBarsShowText = transferBarShowText);
        }
        ElezenImgui.DrawHelpText("Shows download text (amount of MiB downloaded) in the transfer bars");

        var transferBarWidth = _configService.Current.TransferBarsWidth;
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Transfer Bar Width", ref transferBarWidth, 0, 500))
        {
            _configService.Update(c => c.TransferBarsWidth = Math.Max(10, transferBarWidth));
        }
        ElezenImgui.DrawHelpText("Width of the displayed transfer bars (will never be less wide than the displayed text)");

        var transferBarHeight = _configService.Current.TransferBarsHeight;
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Transfer Bar Height", ref transferBarHeight, 0, 50))
        {
            _configService.Update(c => c.TransferBarsHeight = Math.Max(2, transferBarHeight));
        }
        ElezenImgui.DrawHelpText("Height of the displayed transfer bars (will never be less tall than the displayed text)");

        var showUploading = _configService.Current.ShowUploading;
        if (ImGui.Checkbox("Show 'Uploading' text below players that are currently uploading", ref showUploading))
        {
            _configService.Update(c => c.ShowUploading = showUploading);
        }
        ElezenImgui.DrawHelpText("This will render an 'Uploading' text at the feet of the player that is in progress of uploading data.");

        ImGui.Unindent();

        if (!showUploading)
        {
            ImGui.BeginDisabled();
        }

        ImGui.Indent();
        var showUploadingBigText = _configService.Current.ShowUploadingBigText;
        if (ImGui.Checkbox("Large font for 'Uploading' text", ref showUploadingBigText))
        {
            _configService.Update(c => c.ShowUploadingBigText = showUploadingBigText);
        }
        ElezenImgui.DrawHelpText("This will render an 'Uploading' text in a larger font.");
        ImGui.Unindent();

        if (!showUploading)
        {
            ImGui.EndDisabled();
        }

        if (!showTransferBars)
        {
            ImGui.EndDisabled();
        }
    }

    private void DrawCurrentTransfers()
    {
        _fontService.BigText("Current Transfers");

        var connected = _apiController.ServerState is ServerState.Connected;
        var transfersTabs = new List<string>();
        if (connected)
        {
            transfersTabs.Add(TransfersTabCurrent);
        }
        transfersTabs.Add(TransfersTabBlocked);

        _transfersActiveTab = ModernTabBar.Draw("transfers", transfersTabs, _transfersActiveTab);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        if (connected && string.Equals(_transfersActiveTab, TransfersTabCurrent, StringComparison.Ordinal))
        {
            DrawTransferSnapshots();
        }
        else if (string.Equals(_transfersActiveTab, TransfersTabBlocked, StringComparison.Ordinal))
        {
            DrawBlockedTransfers();
        }
    }

    private void DrawTransferSnapshots()
    {
        ImGui.TextUnformatted("Uploads");
        if (ImGui.BeginTable("UploadsTable", 3))
        {
            ImGui.TableSetupColumn("File");
            ImGui.TableSetupColumn("Uploaded");
            ImGui.TableSetupColumn("Size");
            ImGui.TableHeadersRow();

            foreach (var transfer in _fileTransferManager.GetCurrentUploadsSnapshot())
            {
                var color = SnowcloakUi.UploadColor((transfer.Transferred, transfer.Total));
                using var col = ImRaii.PushColor(ImGuiCol.Text, color);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(transfer.Hash);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(ElezenImgui.ByteToString(transfer.Transferred));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(ElezenImgui.ByteToString(transfer.Total));
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

            foreach (var download in _statusStore.Snapshot())
            {
                var userName = download.Handler.Name;
                foreach (var group in download.Groups)
                {
                    var color = SnowcloakUi.UploadColor((group.TransferredBytes, group.TotalBytes));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(userName);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(group.Server);
                    using var col = ImRaii.PushColor(ImGuiCol.Text, color);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(group.TransferredFiles + "/" + group.TotalFiles);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(ElezenImgui.ByteToString(group.TransferredBytes) + "/" + ElezenImgui.ByteToString(group.TotalBytes));
                    ImGui.TableNextRow();
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawBlockedTransfers()
    {
        ElezenImgui.ColouredWrappedText("Files that you attempted to upload or download that were forbidden to be transferred by their creators will appear here. "
            + "If you see file paths from your drive here, then those files were not allowed to be uploaded. If you see hashes, those files were not allowed to be downloaded. "
            + "Ask your paired friend to send you the mod in question through other means or acquire the mod yourself.",
            ImGuiColors.DalamudGrey);

        if (ImGui.BeginTable("TransfersTable", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Hash/Filename");
            ImGui.TableSetupColumn("Forbidden by");
            ImGui.TableHeadersRow();

            foreach (var item in _fileTransferOrchestrator.GetForbiddenTransfers())
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.DisplayName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ForbiddenBy);
            }

            ImGui.EndTable();
        }
    }
}
