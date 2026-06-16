using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public class DownloadUi : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly SnowcloakConfigService _configService;
    private readonly DownloadStatusStore _statusStore;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileUploadManager _fileTransferManager;
    private readonly TransferOverlayUiState _transferOverlayState;
    private readonly UiFontService _fontService;

    public DownloadUi(ILogger<DownloadUi> logger, DalamudUtilService dalamudUtilService, SnowcloakConfigService configService,
        FileUploadManager fileTransferManager, DownloadStatusStore statusStore, SnowMediator mediator,
        TransferOverlayUiState transferOverlayState, UiFontService fontService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Downloads", performanceCollectorService)
    {
        _dalamudUtilService = dalamudUtilService;
        _configService = configService;
        _fileTransferManager = fileTransferManager;
        _statusStore = statusStore;
        _transferOverlayState = transferOverlayState;
        _fontService = fontService;
        WindowName = "Snowcloak Downloads";

        SizeConstraints = new WindowSizeConstraints()
        {
            MaximumSize = new Vector2(500, 90),
            MinimumSize = new Vector2(500, 90),
        };

        Flags |= ImGuiWindowFlags.NoMove;
        Flags |= ImGuiWindowFlags.NoBackground;
        Flags |= ImGuiWindowFlags.NoInputs;
        Flags |= ImGuiWindowFlags.NoNavFocus;
        Flags |= ImGuiWindowFlags.NoResize;
        Flags |= ImGuiWindowFlags.NoScrollbar;
        Flags |= ImGuiWindowFlags.NoTitleBar;
        Flags |= ImGuiWindowFlags.NoDecoration;
        Flags |= ImGuiWindowFlags.NoFocusOnAppearing;

        DisableWindowSounds = true;

        ForceMainWindow = true;

        IsOpen = true;

        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<GposeEndMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<PlayerUploadingMessage>(this, msg => _transferOverlayState.SetUploading(msg.Handler, msg.IsUploading));
    }
    
    protected override void DrawInternal()
    {
        var snapshot = CreateTransferSnapshot();
        var currentDownloads = snapshot.Downloads;

        if (_configService.Current.ShowTransferWindow)
        {
            try
            {
                var currentUploads = snapshot.Uploads;
                if (currentUploads.Count > 0)
                {
                    var totalUploads = currentUploads.Count;

                    var doneUploads = currentUploads.Count(c => c.IsTransferred);
                    var totalUploaded = currentUploads.Sum(c => c.Transferred);
                    var totalToUpload = currentUploads.Sum(c => c.Total);

                    ElezenImgui.DrawOutlinedFont($"▲", ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.SameLine();
                    var xDistance = ImGui.GetCursorPosX();
                    ElezenImgui.DrawOutlinedFont(string.Format(CultureInfo.InvariantCulture, "Compressing+Uploading {0}/{1}", doneUploads, totalUploads),
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.NewLine();
                    ImGui.SameLine(xDistance);
                    ElezenImgui.DrawOutlinedFont(
                        $"{ElezenImgui.ByteToString(totalUploaded, addSuffix: false)}/{ElezenImgui.ByteToString(totalToUpload)}",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);

                    if (currentDownloads.Count > 0) ImGui.Separator();
                }
            }
            catch
            {
                // ignore errors thrown from UI
            }

            try
            {
                foreach (var item in currentDownloads)
                {
                    var dlSlot = item.CountByStatus(DownloadStatus.WaitingForSlot);
                    var dlQueue = item.CountByStatus(DownloadStatus.WaitingForQueue);
                    var dlProg = item.CountByStatus(DownloadStatus.Downloading);
                    var dlDecomp = item.CountByStatus(DownloadStatus.Decompressing);
                    var totalFiles = item.TotalFiles;
                    var transferredFiles = item.TransferredFiles;
                    var totalBytes = item.TotalBytes;
                    var transferredBytes = item.TransferredBytes;

                    ElezenImgui.DrawOutlinedFont($"▼", ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.SameLine();
                    var xDistance = ImGui.GetCursorPosX();
                    ElezenImgui.DrawOutlinedFont(
                        $"{item.Handler.Name} [W:{dlSlot}/Q:{dlQueue}/P:{dlProg}/D:{dlDecomp}]",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.NewLine();
                    ImGui.SameLine(xDistance);
                    ElezenImgui.DrawOutlinedFont(
                        $"{transferredFiles}/{totalFiles} ({ElezenImgui.ByteToString(transferredBytes, addSuffix: false)}/{ElezenImgui.ByteToString(totalBytes)})",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                }
            }
            catch
            {
                // ignore errors thrown from UI
            }
        }

        if (_configService.Current.ShowTransferBars)
        {
            const int transparency = 100;
            const int dlBarBorder = 3;

            foreach (var transfer in currentDownloads)
            {
                var handler = transfer.Handler;
                if (handler == null) continue;

                var gameObject = handler.GetGameObject();
                if (gameObject == null) continue;

                var screenPos = _dalamudUtilService.WorldToScreen(gameObject);
                if (screenPos == Vector2.Zero) continue;

                var totalBytes = transfer.TotalBytes;
                var transferredBytes = transfer.TransferredBytes;

                var maxDlText = $"{ElezenImgui.ByteToString(totalBytes, addSuffix: false)}/{ElezenImgui.ByteToString(totalBytes)}";
                var textSize = _configService.Current.TransferBarsShowText ? ImGui.CalcTextSize(maxDlText) : new Vector2(10, 10);

                int dlBarHeight = _configService.Current.TransferBarsHeight > ((int)textSize.Y + 5) ? _configService.Current.TransferBarsHeight : (int)textSize.Y + 5;
                int dlBarWidth = _configService.Current.TransferBarsWidth > ((int)textSize.X + 10) ? _configService.Current.TransferBarsWidth : (int)textSize.X + 10;

                var dlBarStart = new Vector2(screenPos.X - dlBarWidth / 2f, screenPos.Y - dlBarHeight / 2f);
                var dlBarEnd = new Vector2(screenPos.X + dlBarWidth / 2f, screenPos.Y + dlBarHeight / 2f);
                var drawList = ImGui.GetBackgroundDrawList();
                drawList.AddRectFilled(
                    dlBarStart with { X = dlBarStart.X - dlBarBorder - 1, Y = dlBarStart.Y - dlBarBorder - 1 },
                    dlBarEnd with { X = dlBarEnd.X + dlBarBorder + 1, Y = dlBarEnd.Y + dlBarBorder + 1 },
                    ElezenTools.UI.Colour.RgbaToColour(0, 0, 0, transparency), 1);
                drawList.AddRectFilled(dlBarStart with { X = dlBarStart.X - dlBarBorder, Y = dlBarStart.Y - dlBarBorder },
                    dlBarEnd with { X = dlBarEnd.X + dlBarBorder, Y = dlBarEnd.Y + dlBarBorder },
                    ElezenTools.UI.Colour.RgbaToColour(220, 220, 255, transparency), 1);
                drawList.AddRectFilled(dlBarStart, dlBarEnd,
                    ElezenTools.UI.Colour.RgbaToColour(0, 0, 0, transparency), 1);
                var dlProgressPercent = totalBytes > 0 ? Math.Clamp(transferredBytes / (double)totalBytes, 0d, 1d) : 0d;
                drawList.AddRectFilled(dlBarStart,
                    dlBarEnd with { X = dlBarStart.X + (float)(dlProgressPercent * dlBarWidth) },
                    ElezenTools.UI.Colour.RgbaToColour(100, 100, 255, transparency), 1);

                if (_configService.Current.TransferBarsShowText)
                {
                    var downloadText = $"{ElezenImgui.ByteToString(transferredBytes, addSuffix: false)}/{ElezenImgui.ByteToString(totalBytes)}";
                    ElezenImgui.DrawOutlinedFont(drawList, downloadText,
                        screenPos with { X = screenPos.X - textSize.X / 2f - 1, Y = screenPos.Y - textSize.Y / 2f - 1 },
                        ElezenTools.UI.Colour.RgbaToColour(255, 255, 255, transparency),
                        ElezenTools.UI.Colour.RgbaToColour(0, 0, 0, transparency), 1);
                }
            }

            if (_configService.Current.ShowUploading)
            {
                foreach (var player in snapshot.UploadingPlayers)
                {
                    if (player == null) continue;

                    var gameObject = player.GetGameObject();
                    if (gameObject == null) continue;

                    var screenPos = _dalamudUtilService.WorldToScreen(gameObject);
                    if (screenPos == Vector2.Zero) continue;

                    try
                    {
                        using var _ = _fontService.UidFont.Push();
                        var uploadText = "Uploading";
                        
                        var textSize = ImGui.CalcTextSize(uploadText);

                        var drawList = ImGui.GetBackgroundDrawList();
                        ElezenImgui.DrawOutlinedFont(drawList, uploadText,
                            screenPos with { X = screenPos.X - textSize.X / 2f - 1, Y = screenPos.Y - textSize.Y / 2f - 1 },
                            ElezenTools.UI.Colour.RgbaToColour(255, 255, 0, transparency),
                            ElezenTools.UI.Colour.RgbaToColour(0, 0, 0, transparency), 2);
                    }
                    catch
                    {
                        // ignore errors thrown on UI
                    }
                }
            }
        }
    }

    public override bool DrawConditions()
    {
        if (_transferOverlayState.EditTrackerPosition) return true;
        if (!_configService.Current.ShowTransferWindow && !_configService.Current.ShowTransferBars) return false;
        if (!_statusStore.HasActiveDownloads && !_fileTransferManager.IsUploading && !_transferOverlayState.HasUploadingPlayers) return false;
        if (!IsOpen) return false;
        return true;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        if (_transferOverlayState.EditTrackerPosition)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
            Flags &= ~ImGuiWindowFlags.NoBackground;
            Flags &= ~ImGuiWindowFlags.NoInputs;
            Flags &= ~ImGuiWindowFlags.NoResize;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
            Flags |= ImGuiWindowFlags.NoBackground;
            Flags |= ImGuiWindowFlags.NoInputs;
            Flags |= ImGuiWindowFlags.NoResize;
        }

        var maxHeight = ImGui.GetTextLineHeight() * (_configService.Current.ParallelDownloads + 3);
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(300, maxHeight),
            MaximumSize = new Vector2(300, maxHeight),
        };
    }

    private TransferWindowSnapshot CreateTransferSnapshot()
    {
        return new(
            _fileTransferManager.GetCurrentUploadsSnapshot(),
            _statusStore.Snapshot(),
            _transferOverlayState.UploadingPlayersSnapshot);
    }

    private sealed record TransferWindowSnapshot(
        IReadOnlyList<UploadStatusSnapshot> Uploads,
        IReadOnlyList<DownloadSnapshot> Downloads,
        IReadOnlyList<GameObjectHandler> UploadingPlayers);
}
