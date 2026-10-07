using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ElezenTools.UI;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Account;
using Snowcloak.API.Dto.User;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.UI.Handlers;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Snowcloak.UI;

public partial class CompactUi
{
    private void DrawTransfers()
    {
        var currentUploads = _fileTransferManager.GetCurrentUploadsSnapshot();

        var backup = _snapshotService.Progress.Read();
        bool backupUploading = _snapshotService.Busy && backup.Uploads > 0;

        if (currentUploads.Any() || backupUploading)
        {
            ImGui.AlignTextToFramePadding();
            ElezenImgui.ShowIcon(FontAwesomeIcon.Upload);
            ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);

            var totalUploads = currentUploads.Count + (backupUploading ? backup.Uploads : 0);

            var doneUploads = currentUploads.Count(c => c.IsTransferred)
                + (backupUploading ? backup.UploadedObjects : 0);
            var totalUploaded = currentUploads.Sum(c => c.Transferred) + (backupUploading ? backup.SentBytes : 0);
            var totalToUpload = currentUploads.Sum(c => c.Total) + (backupUploading ? backup.UploadBytes : 0);

            ImGui.TextUnformatted($"{doneUploads}/{totalUploads}");
            string totalBytes = ElezenImgui.ByteToString(totalToUpload);
            var uploadText = $"({ElezenImgui.ByteToString(totalUploaded)}/{totalBytes})";
            var textSize = ImGui.CalcTextSize(uploadText);
            ImGui.SameLine(_windowContentWidth - textSize.X);
            ImGui.TextUnformatted(uploadText);
        }

        var currentDownloads = _statusStore.Snapshot();

        if (currentDownloads.Count > 0)
        {
            ImGui.AlignTextToFramePadding();
            ElezenImgui.ShowIcon(FontAwesomeIcon.Download);
            ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);

            var totalDownloads = currentDownloads.Sum(c => c.TotalFiles);
            var doneDownloads = currentDownloads.Sum(c => c.TransferredFiles);
            var totalDownloaded = currentDownloads.Sum(c => c.TransferredBytes);
            var totalToDownload = currentDownloads.Sum(c => c.TotalBytes);

            ImGui.TextUnformatted($"{doneDownloads}/{totalDownloads}");
            var downloadText =
                $"({ElezenImgui.ByteToString(totalDownloaded)}/{ElezenImgui.ByteToString(totalToDownload)})";
            var textSize = ImGui.CalcTextSize(downloadText);
            ImGui.SameLine(_windowContentWidth - textSize.X);
            ImGui.TextUnformatted(downloadText);
        }
    }
}
