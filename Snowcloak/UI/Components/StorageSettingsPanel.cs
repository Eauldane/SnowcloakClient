using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using ElezenTools.UI;
using Snowcloak.CacheFile.Enums;
using Snowcloak.Configuration;
using Snowcloak.FileCache;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services;
using Snowcloak.WebAPI.Files.Models;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Snowcloak.UI.Components;

public sealed partial class StorageSettingsPanel : IDisposable
{
    private readonly CacheMonitor _cacheMonitor;
    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileCompactor _fileCompactor;
    private readonly FileDialogManager _fileDialogManager;
    private readonly IpcManager _ipcManager;
    private readonly UiFontService _fontService;
    private readonly Dictionary<string, object> _selectedComboItems = new(StringComparer.Ordinal);
    private bool _cacheDirectoryHasOtherFilesThanCache;
    private bool _cacheDirectoryIsValidPath = true;
    private (int, int, FileCacheEntity) _currentProgress;
    private bool _isDirectoryWritable;
    private bool _isOneDrive;
    private bool _isPenumbraDirectory;
    private bool _readClearCache;
    private CancellationTokenSource? _validationCts;
    private readonly IProgress<(int, int, FileCacheEntity)> _validationProgress;
    private Task<List<FileCacheEntity>>? _validationTask;

    public StorageSettingsPanel(CacheMonitor cacheMonitor, FileDialogManager fileDialogManager,
        SnowcloakConfigService configService, DalamudUtilService dalamudUtil, IpcManager ipcManager,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor, UiFontService fontService)
    {
        _cacheMonitor = cacheMonitor;
        _fileDialogManager = fileDialogManager;
        _configService = configService;
        _dalamudUtil = dalamudUtil;
        _ipcManager = ipcManager;
        _fileCacheManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _fontService = fontService;
        _isDirectoryWritable = IsDirectoryWritable(_configService.Current.CacheFolder);
        _validationProgress = new Progress<(int, int, FileCacheEntity)>(v => _currentProgress = v);
    }

    public bool HasValidPenumbraModPath => !(_ipcManager.Penumbra.ModDirectory ?? string.Empty).IsNullOrEmpty()
                                           && Directory.Exists(_ipcManager.Penumbra.ModDirectory);

    public void Dispose()
    {
        _validationCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Draw()
    {
        _fontService.BigText("Storage");

        ElezenImgui.WrappedText("Snowcloak stores downloaded files from paired people permanently. This is to improve loading performance and requiring less downloads. "
                                + "The storage governs itself by clearing data beyond the set storage size. Please set the storage size accordingly. It is not necessary to manually clear the storage.");

        DrawFileScanState();
        DrawWatcherState();
        DrawCacheDirectorySetting();
        DrawStorageUsage();
        DrawCompactionSettings();
        DrawCompressionSettings();
        DrawValidation();
        DrawClearStorage();
    }

    private void DrawWatcherState()
    {
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
            ElezenImgui.AttachTooltip("Attempts to resume monitoring for both Penumbra and Snowcloak Storage. "
                                      + "Resuming the monitoring will also force a full scan to run." + Environment.NewLine
                                      + "If the button remains present after clicking it, consult /xllog for errors");
        }
        else
        {
            using (ImRaii.Disabled(!ElezenImgui.CtrlPressed()))
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Stop, "Stop Monitoring"))
                {
                    _cacheMonitor.StopMonitoring();
                }
            }
            ElezenImgui.AttachTooltip("Stops the monitoring for both Penumbra and Snowcloak Storage. "
                                      + "Do not stop the monitoring, unless you plan to move the Penumbra and Snowcloak Storage folders, to ensure correct functionality of Snowcloak." + Environment.NewLine
                                      + "If you stop the monitoring to move folders around, resume it after you are finished moving the files."
                                      + ElezenImgui.TooltipSeparator + "Hold CTRL to enable this button");
        }
    }

    public void DrawCacheDirectorySetting()
    {
        ElezenImgui.ColouredWrappedText("Note: The storage folder should be somewhere close to root (i.e. C\\SnowcloakStorage) in a new empty folder. DO NOT point this to your game folder. DO NOT point this to your Penumbra folder.", ImGuiColors.DalamudYellow);
        var cacheDirectory = _configService.Current.CacheFolder;
        ImGui.SetNextItemWidth(400 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Storage Folder##cache", ref cacheDirectory, 255, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        using (ImRaii.Disabled(_cacheMonitor.SnowWatcher != null))
        {
            if (ElezenImgui.IconButton(FontAwesomeIcon.Folder))
            {
                _fileDialogManager.OpenFolderDialog("Pick Snowcloak Storage Folder", (success, path) =>
                {
                    if (!success) return;

                    _isOneDrive = path.Contains("onedrive", StringComparison.OrdinalIgnoreCase);
                    _isPenumbraDirectory = string.Equals(path, _ipcManager.Penumbra.ModDirectory, StringComparison.OrdinalIgnoreCase);
                    _isDirectoryWritable = IsDirectoryWritable(path);
                    _cacheDirectoryHasOtherFilesThanCache = false;
                    var cacheDirFiles = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                    var cacheSubDirs = Directory.GetDirectories(path);

                    _cacheDirectoryHasOtherFilesThanCache = cacheDirFiles.Any(f =>
                    {
                        var fileName = Path.GetFileName(f);
                        if (string.IsNullOrEmpty(fileName) || fileName.StartsWith('.'))
                        {
                            return false;
                        }

                        var extension = Path.GetExtension(f);
                        if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
                            || extension.Equals(".blk", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        return Path.GetFileNameWithoutExtension(f).Length != 64;
                    });

                    if (!_cacheDirectoryHasOtherFilesThanCache
                        && cacheSubDirs.Select(f => Path.GetFileName(Path.TrimEndingDirectorySeparator(f))).Any(f =>
                        {
                            if (string.IsNullOrEmpty(f) || f.StartsWith('.'))
                            {
                                return false;
                            }

                            return !f.Equals("subst", StringComparison.OrdinalIgnoreCase);
                        }))
                    {
                        _cacheDirectoryHasOtherFilesThanCache = true;
                    }

                    _cacheDirectoryIsValidPath = PathRegex().IsMatch(path);

                    if (!string.IsNullOrEmpty(path)
                        && Directory.Exists(path)
                        && _isDirectoryWritable
                        && !_isPenumbraDirectory
                        && !_isOneDrive
                        && !_cacheDirectoryHasOtherFilesThanCache
                        && _cacheDirectoryIsValidPath)
                    {
                        _configService.Update(c => c.CacheFolder = path);
                        _cacheMonitor.StartSnowWatcher(path);
                        _cacheMonitor.InvokeScan();
                    }
                }, _dalamudUtil.IsWine ? @"Z:\" : @"C:\");
            }
        }

        if (_cacheMonitor.SnowWatcher != null)
        {
            ElezenImgui.AttachTooltip("Stop the Monitoring before changing the Storage folder. As long as monitoring is active, you cannot change the Storage folder location.");
        }

        if (_isPenumbraDirectory)
        {
            ElezenImgui.ColouredWrappedText("Do not point the storage path directly to the Penumbra directory. If necessary, make a subfolder in it.", ImGuiColors.DalamudRed);
        }
        else if (_isOneDrive)
        {
            ElezenImgui.ColouredWrappedText("Do not point the storage path to a folder in OneDrive. Do not use OneDrive folders for any Mod related functionality.", ImGuiColors.DalamudRed);
        }
        else if (!_isDirectoryWritable)
        {
            ElezenImgui.ColouredWrappedText("The folder you selected does not exist or cannot be written to. Please provide a valid path.", ImGuiColors.DalamudRed);
        }
        else if (_cacheDirectoryHasOtherFilesThanCache)
        {
            ElezenImgui.ColouredWrappedText("Your selected directory has files or directories inside that are not Snowcloak related. Use an empty directory or a previous storage directory only.", ImGuiColors.DalamudRed);
        }
        else if (!_cacheDirectoryIsValidPath)
        {
            ElezenImgui.ColouredWrappedText("Your selected directory contains illegal characters unreadable by FFXIV. Restrict yourself to latin letters (A-Z), underscores (_), dashes (-) and arabic numbers (0-9).", ImGuiColors.DalamudRed);
        }

        float maxCacheSize = (float)_configService.Current.MaxLocalCacheInGiB;
        ImGui.SetNextItemWidth(400 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("Maximum Storage Size", ref maxCacheSize, 1f, 200f, "%.2f GiB"))
        {
            _configService.Update(c => c.MaxLocalCacheInGiB = maxCacheSize);
        }
        ElezenImgui.DrawHelpText("The storage is automatically governed by Snowcloak. It will clear itself automatically once it reaches the set capacity according to the selected eviction strategy. You typically do not need to clear it yourself.");
        ImGui.SetNextItemWidth(400 * ImGuiHelpers.GlobalScale);
        ElezenImgui.DrawCombo("Eviction Strategy", Enum.GetValues<CacheEvictionMode>(),
            mode => mode switch
            {
                CacheEvictionMode.LeastRecentlyUsed => "Least Recently Used (LRU)",
                CacheEvictionMode.LeastFrequentlyUsed => "Least Frequently Used (LFU)",
                CacheEvictionMode.ExpirationDate => "30-Day Time To Live (TTL)",
                _ => "Unknown",
            },
            _selectedComboItems,
            mode =>
            {
                _configService.Update(c => c.CacheEvictionMode = mode);
            }, _configService.Current.CacheEvictionMode);
        ElezenImgui.DrawHelpText("Choose how Snowcloak removes files when the storage exceeds the configured size. TTL automatically purges files that have not been used in the last 30 days.");
    }

    public void DrawFileScanState()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("File Scanner Status");
        ImGui.SameLine();
        if (_cacheMonitor.IsScanRunning)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Scan is running");
            ImGui.TextUnformatted("Current Progress:");
            ImGui.SameLine();
            ImGui.TextUnformatted(_cacheMonitor.TotalFiles == 1
                ? "Collecting files"
                : string.Format(CultureInfo.CurrentCulture, "Processing {0}/{1} from storage ({2} scanned in)", _cacheMonitor.CurrentFileProgress, _cacheMonitor.TotalFilesStorage, _cacheMonitor.TotalFiles));
            ElezenImgui.AttachTooltip("Note: it is possible to have more files in storage than scanned in, this is due to the scanner normally ignoring those files but the game loading them in and using them on your character, so they get added to the local storage.");
        }
        else if (_cacheMonitor.IsScanHalted)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "Halted ({0})", _cacheMonitor.DescribeHaltSources()));
            ImGui.SameLine();
            if (ImGui.Button("Reset halt requests##clearlocks"))
            {
                _cacheMonitor.ResetLocks();
            }
        }
        else
        {
            ImGui.TextUnformatted("Idle");
            if (_configService.Current.InitialScanComplete)
            {
                ImGui.SameLine();
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Play, "Force rescan"))
                {
                    _cacheMonitor.InvokeScan();
                }
            }
        }
    }

    private void DrawStorageUsage()
    {
        ImGui.AlignTextToFramePadding();
        if (_cacheMonitor.FileCacheSize >= 0)
        {
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "Currently utilized local storage: {0:0.00} GiB", _cacheMonitor.FileCacheSize / 1024.0 / 1024.0 / 1024.0));
        }
        else
        {
            ImGui.TextUnformatted("Currently utilized local storage: Calculating...");
        }

        if (!_dalamudUtil.IsWine)
        {
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "Remaining space free on drive: {0:0.00} GiB", _cacheMonitor.FileCacheDriveFree / 1024.0 / 1024.0 / 1024.0));
        }
    }

    private void DrawCompactionSettings()
    {
        var isLinux = _dalamudUtil.IsWine;
        var useFileCompactor = _configService.Current.UseCompactor;
        if (!useFileCompactor && !isLinux)
        {
            ElezenImgui.ColouredWrappedText("Hint: To free up space when using Snowcloak consider enabling the File Compactor", ImGuiColors.DalamudYellow);
        }

        if (isLinux || !_cacheMonitor.StorageisNTFS)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Checkbox("Use file compactor", ref useFileCompactor))
        {
            _configService.Update(c => c.UseCompactor = useFileCompactor);
            if (!isLinux)
            {
                _fileCompactor.CompactStorage(useFileCompactor);
            }
        }

        ElezenImgui.DrawHelpText("The file compactor can massively reduce your saved files. It might incur a minor penalty on loading files on a slow CPU."
            + Environment.NewLine
            + "It is recommended to leave it enabled to save on space.");
        if (isLinux || !_cacheMonitor.StorageisNTFS)
        {
            ImGui.EndDisabled();
            ImGui.TextUnformatted("The file compactor is only available on Windows and NTFS drives.");
        }
    }

    private void DrawCompressionSettings()
    {
        var useMultithreadedCompression = _configService.Current.UseMultithreadedCompression;
        if (ImGui.Checkbox("Enable multithreaded compression", ref useMultithreadedCompression))
        {
            _configService.Update(c => c.UseMultithreadedCompression = useMultithreadedCompression);
        }
        ElezenImgui.DrawHelpText("Allow larger files to use multithreaded compression.");

        SettingsUiControls.DrawCombo("Preferred download type",
            new[] { CompressionType.ZSTD, CompressionType.LZ4 },
            compressionType => compressionType switch
            {
                CompressionType.ZSTD => "ZSTD",
                CompressionType.LZ4 => "LZ4",
                _ => compressionType.ToString()
            },
            _selectedComboItems,
            compressionType => _configService.Update(c => c.PreferredDownloadType = compressionType),
            _configService.Current.PreferredDownloadType);
        ElezenImgui.DrawHelpText("Choose which SCF compression variant Snowcloak prefers when downloading files from the server. "
                                 + "ZSTD is the default, favouring smaller downloads for metered or slow connections. LZ4 is the old style compression, for slower systems. If you still struggle, compression can be disabled at the expense of bandwidth. Disabling compression is STRONGLY discouraged.");
    }

    private void DrawValidation()
    {
        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));
        ImGui.Separator();
        ElezenImgui.WrappedText("File Storage validation can make sure that all files in your local storage folder are valid. "
                                + "Run the validation before you clear the Storage for no reason. " + Environment.NewLine
                                + "This operation, depending on how many files you have in your storage, can take a while and will be CPU and drive intensive.");
        using (ImRaii.Disabled(_validationTask != null && !_validationTask.IsCompleted))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Check, "Start File Storage Validation"))
            {
                _validationCts?.Cancel();
                _validationCts?.Dispose();
                _validationCts = new CancellationTokenSource();
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
    }

    private void DrawClearStorage()
    {
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
        {
            ImGui.BeginDisabled();
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Clear local storage") && ElezenImgui.CtrlPressed() && _readClearCache)
        {
            _ = Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(_configService.Current.CacheFolder, "*", SearchOption.AllDirectories))
                {
                    File.Delete(file);
                }
            });
        }
        ElezenImgui.AttachTooltip("You normally do not need to do this. THIS IS NOT SOMETHING YOU SHOULD BE DOING TO TRY TO FIX SYNC ISSUES." + Environment.NewLine
            + "This will solely remove all downloaded data from all players and will require you to re-download everything again." + Environment.NewLine
            + "Snowcloak's storage is self-clearing and will not surpass the limit you have set it to." + Environment.NewLine
            + "If you still think you need to do this hold CTRL while pressing the button.");

        if (!_readClearCache)
        {
            ImGui.EndDisabled();
        }

        ImGui.Unindent();
    }

    private static bool IsDirectoryWritable(string dirPath, bool throwIfFails = false)
    {
        try
        {
            using FileStream fs = File.Create(
                Path.Combine(
                    dirPath,
                    Path.GetRandomFileName()
                ),
                1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            if (throwIfFails)
            {
                throw;
            }

            return false;
        }
    }

    [GeneratedRegex(@"^(?:[a-zA-Z]:\\[\w\s\-\\]+?|\/(?:[\w\s\-\/.])+?)$", RegexOptions.ECMAScript, 5000)]
    private static partial Regex PathRegex();
}
