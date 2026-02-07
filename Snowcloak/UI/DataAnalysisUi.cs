using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Microsoft.Extensions.Logging;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using System.Numerics;
using Penumbra.Api.Enums;

namespace Snowcloak.UI;

public class DataAnalysisUi : WindowMediatorSubscriberBase
{
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly Progress<(string, int)> _conversionProgress = new();
    private readonly IpcManager _ipcManager;
    private readonly UiSharedService _uiSharedService;
    private readonly Dictionary<string, (TextureType TextureType, string[] Duplicates)> _texturesToConvert = new(StringComparer.Ordinal);
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysis;
    private CancellationTokenSource _conversionCancellationTokenSource = new();
    private string _conversionCurrentFileName = string.Empty;
    private int _conversionCurrentFileProgress = 0;
    private Task? _conversionTask;
    private bool _enableTextureConversionMode = false;
    private bool _hasUpdate = false;
    private bool _sortDirty = true;
    private bool _modalOpen = false;
    private string _selectedFileTypeTab = string.Empty;
    private string _selectedHash = string.Empty;
    private ObjectKind _selectedObjectTab;
    private bool _showModal = false;

    public DataAnalysisUi(ILogger<DataAnalysisUi> logger, SnowMediator mediator,
        CharacterAnalyzer characterAnalyzer, IpcManager ipcManager,
        PerformanceCollectorService performanceCollectorService,
        UiSharedService uiSharedService)
        : base(logger, mediator, "Snowcloak Character Data Analysis###SnowcloakDataAnalysisUI", performanceCollectorService)
    {
        _characterAnalyzer = characterAnalyzer;
        _ipcManager = ipcManager;
        _uiSharedService = uiSharedService;
        WindowName = "Character Data Analysis";
        Mediator.Subscribe<CharacterDataAnalyzedMessage>(this, (_) =>
        {
            _hasUpdate = true;
        });
        SizeConstraints = new()
        {
            MinimumSize = new()
            {
                X = 1100,
                Y = 700
            },
            MaximumSize = new()
            {
                X = 3840,
                Y = 2160
            }
        };

        _conversionProgress.ProgressChanged += ConversionProgress_ProgressChanged;
    }
    
    protected override void DrawInternal()
    {
        var conversionPopupTitle = "Texture Conversion in Progress";
        if (_conversionTask != null && !_conversionTask.IsCompleted)
        {
            _showModal = true;
            if (ImGui.BeginPopupModal(conversionPopupTitle))
            {
                ImGui.TextUnformatted(string.Format("Texture Conversion in progress: {0}/{1}", _conversionCurrentFileProgress, _texturesToConvert.Count));
                ElezenImgui.WrappedText(string.Format("Current file: {0}", _conversionCurrentFileName));
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, "Cancel conversion"))
                {
                    _conversionCancellationTokenSource.Cancel();
                }
                UiSharedService.SetScaledWindowSize(500);
                ImGui.EndPopup();
            }
            else
            {
                _modalOpen = false;
            }
        }
        else if (_conversionTask != null && _conversionTask.IsCompleted && _texturesToConvert.Count > 0)
        {
            _conversionTask = null;
            _texturesToConvert.Clear();
            _showModal = false;
            _modalOpen = false;
            _enableTextureConversionMode = false;
        }

        if (_showModal && !_modalOpen)
        {
            ImGui.OpenPopup(conversionPopupTitle);
            _modalOpen = true;
        }

        if (_hasUpdate)
        {
            _cachedAnalysis = _characterAnalyzer.LastAnalysis.DeepClone();
            _hasUpdate = false;
            _sortDirty = true;
        }

        ElezenImgui.WrappedText("This window shows you all files and their sizes that are currently in use through your character and associated entities");
        
        if (_cachedAnalysis == null || _cachedAnalysis.Count == 0) return;

        bool isAnalyzing = _characterAnalyzer.IsAnalysisRunning;
        bool needAnalysis = _cachedAnalysis!.Any(c => c.Value.Any(f => !f.Value.IsComputed));
        if (isAnalyzing)
        {
            ElezenImgui.ColouredWrappedText(string.Format("Analyzing {0}/{1}", _characterAnalyzer.CurrentFile, _characterAnalyzer.TotalFiles),
                ImGuiColors.DalamudYellow);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, "Cancel analysis"))
            {
                _characterAnalyzer.CancelAnalyze();
            }
        }
        else
        {
            if (needAnalysis)
            {
                var drawList = ImGui.GetWindowDrawList();
                var cursorPos = ImGui.GetCursorPos();
                var padding = new Vector2(10f, 8f);
                ImGui.SetCursorPos(cursorPos + padding);
                drawList.ChannelsSplit(2);
                drawList.ChannelsSetCurrent(1);
                using (ImRaii.Group())
                {
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        ImGui.TextUnformatted(FontAwesomeIcon.ExclamationTriangle.ToIconString());
                    }
                    ImGui.SameLine();
                    ElezenImgui.ColouredText("Analysis incomplete", ImGuiColors.DalamudYellow);
                    ElezenImgui.ColouredWrappedText("Run the analysis to fill missing file sizes and calculate accurate download totals.", ImGuiColors.DalamudGrey);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, "Start analysis (missing entries)"))
                    {
                        _ = _characterAnalyzer.ComputeAnalysis(print: false);
                    }
                }
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                drawList.ChannelsSetCurrent(0);
                drawList.AddRectFilled(min - padding, max + padding, ElezenTools.UI.Colour.Vector4ToColour(new Vector4(0.16f, 0.16f, 0.16f, 0.9f)), 6f);
                drawList.AddRect(min - padding, max + padding, ElezenTools.UI.Colour.Vector4ToColour(ImGuiColors.DalamudYellow), 6f);
                drawList.ChannelsMerge();
            }
            else
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, "Start analysis (recalculate all entries)"))
                {
                    _ = _characterAnalyzer.ComputeAnalysis(print: false, recalculate: true);
                }
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted("Total files:");
        ImGui.SameLine();
        ImGui.TextUnformatted(_cachedAnalysis!.Values.Sum(c => c.Values.Count).ToString());
        ImGui.SameLine();
        using (var font = ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());
        }
        if (ImGui.IsItemHovered())
        {
            string text = "";
            var groupedfiles = _cachedAnalysis.Values.SelectMany(f => f.Values).GroupBy(f => f.FileType, StringComparer.Ordinal);
            text = string.Join(Environment.NewLine, groupedfiles.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => string.Format("{0}: {1} files, size: {2}, compressed: {3}", f.Key, f.Count(), UiSharedService.ByteToString(f.Sum(v => v.OriginalSize)),
                    UiSharedService.ByteToString(f.Sum(v => v.CompressedSize)))));
            ImGui.SetTooltip(text);
        }
        ImGui.TextUnformatted("Total size (actual):");
        ImGui.SameLine();
        ImGui.TextUnformatted(UiSharedService.ByteToString(_cachedAnalysis!.Sum(c => c.Value.Sum(c => c.Value.OriginalSize))));
        ImGui.TextUnformatted("Total size (download size):");
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, needAnalysis))
        {
            ImGui.TextUnformatted(UiSharedService.ByteToString(_cachedAnalysis!.Sum(c => c.Value.Sum(c => c.Value.CompressedSize))));
            if (needAnalysis && !isAnalyzing)
            {
                ImGui.SameLine();
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextUnformatted(FontAwesomeIcon.ExclamationCircle.ToIconString());
                UiSharedService.AttachToolTip("Click \"Start analysis\" to calculate download size");
                
            }
        }
        ImGui.TextUnformatted(string.Format("Total modded model triangles: {0}", UiSharedService.TrisToString(_cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.Triangles)))));
        ImGui.Separator();

        using var tabbar = ImRaii.TabBar("objectSelection");
        foreach (var kvp in _cachedAnalysis)
        {
            using var id = ImRaii.PushId(kvp.Key.ToString());
            string tabText = kvp.Key.ToString();
            using var tab = ImRaii.TabItem(tabText + "###" + kvp.Key.ToString());
            if (tab.Success)
            {
                var groupedfiles = kvp.Value.Select(v => v.Value).GroupBy(f => f.FileType, StringComparer.Ordinal)
                    .OrderBy(k => k.Key, StringComparer.Ordinal).ToList();

                ImGui.TextUnformatted(string.Format("Files for {0}", kvp.Key));
                ImGui.SameLine();
                ImGui.TextUnformatted(kvp.Value.Count.ToString());
                ImGui.SameLine();

                using (var font = ImRaii.PushFont(UiBuilder.IconFont))
                {
                    ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());
                }
                if (ImGui.IsItemHovered())
                {
                    string text = "";
                    text = string.Join(Environment.NewLine, groupedfiles
                        .Select(f => string.Format("{0}: {1} files, size: {2}, compressed: {3}", f.Key, f.Count(), UiSharedService.ByteToString(f.Sum(v => v.OriginalSize)),
                            UiSharedService.ByteToString(f.Sum(v => v.CompressedSize)))));
                    ImGui.SetTooltip(text);
                }
                ImGui.TextUnformatted(string.Format("{0} size (actual):", kvp.Key));
                ImGui.SameLine();
                ImGui.TextUnformatted(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.OriginalSize)));
                ImGui.TextUnformatted(string.Format("{0} size (download size):", kvp.Key));
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, needAnalysis))
                {
                    ImGui.TextUnformatted(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.CompressedSize)));
                    if (needAnalysis && !isAnalyzing)
                    {
                        ImGui.SameLine();
                        using (ImRaii.PushFont(UiBuilder.IconFont))
                            ImGui.TextUnformatted(FontAwesomeIcon.ExclamationCircle.ToIconString());
                        UiSharedService.AttachToolTip("Click \"Start analysis\" to calculate download size");
                    }
                }
                ImGui.TextUnformatted(string.Format("{0} VRAM usage:", kvp.Key));
                ImGui.SameLine();
                var vramUsage = groupedfiles.SingleOrDefault(v => string.Equals(v.Key, "tex", StringComparison.Ordinal));
                if (vramUsage != null)
                {
                    ImGui.TextUnformatted(UiSharedService.ByteToString(vramUsage.Sum(f => f.OriginalSize)));
                }
                ImGui.TextUnformatted(string.Format("{0} modded model triangles: {1}", kvp.Key, UiSharedService.TrisToString(kvp.Value.Sum(f => f.Value.Triangles))));
                ImGui.Separator();
                if (_selectedObjectTab != kvp.Key)
                {
                    _selectedHash = string.Empty;
                    _selectedObjectTab = kvp.Key;
                    _selectedFileTypeTab = string.Empty;
                    _enableTextureConversionMode = false;
                    _texturesToConvert.Clear();
                }

                using var fileTabBar = ImRaii.TabBar("fileTabs");

                foreach (IGrouping<string, CharacterAnalyzer.FileDataEntry>? fileGroup in groupedfiles)
                {
                    string fileGroupText = string.Format("{0} [{1}]", fileGroup.Key, fileGroup.Count());
                    var requiresCompute = fileGroup.Any(k => !k.IsComputed);
                    using var tabcol = ImRaii.PushColor(ImGuiCol.Tab, ElezenTools.UI.Colour.Vector4ToColour(ImGuiColors.DalamudYellow), requiresCompute);
                    ImRaii.IEndObject fileTab;
                    using (var textcol = ImRaii.PushColor(ImGuiCol.Text, ElezenTools.UI.Colour.Vector4ToColour(new Vector4(0, 0, 0, 1)),
                        requiresCompute && !string.Equals(_selectedFileTypeTab, fileGroup.Key, StringComparison.Ordinal)))
                    {
                        fileTab = ImRaii.TabItem(fileGroupText + "###" + fileGroup.Key);
                    }

                    if (!fileTab) { fileTab.Dispose(); continue; }

                    if (!string.Equals(fileGroup.Key, _selectedFileTypeTab, StringComparison.Ordinal))
                    {
                        _selectedFileTypeTab = fileGroup.Key;
                        _selectedHash = string.Empty;
                        _enableTextureConversionMode = false;
                        _texturesToConvert.Clear();
                    }

                    ImGui.TextUnformatted(string.Format("{0} files", fileGroup.Key));
                    ImGui.SameLine();
                    ImGui.TextUnformatted(fileGroup.Count().ToString());

                    ImGui.TextUnformatted(string.Format("{0} files size (actual):", fileGroup.Key));
                    ImGui.SameLine();
                    ImGui.TextUnformatted(UiSharedService.ByteToString(fileGroup.Sum(c => c.OriginalSize)));

                    ImGui.TextUnformatted(string.Format("{0} files size (download size):", fileGroup.Key));
                    ImGui.SameLine();
                    ImGui.TextUnformatted(UiSharedService.ByteToString(fileGroup.Sum(c => c.CompressedSize)));

                    if (string.Equals(_selectedFileTypeTab, "tex", StringComparison.Ordinal))
                    {
                        ImGui.Checkbox("Enable Texture Conversion Mode", ref _enableTextureConversionMode);
                        if (_enableTextureConversionMode)
                        {
                            ElezenImgui.ColouredText("WARNING REGARDING TEXTURE CONVERSION:", ImGuiColors.DalamudYellow);
                            ImGui.SameLine();
                            ElezenImgui.ColouredText("Converting textures is irreversible!", ImGuiColors.DalamudRed);
                            ElezenImgui.ColouredWrappedText("- Converting textures will reduce their size (compressed and uncompressed) drastically. It is recommended to be used for large (4k+) textures." +
                                                            Environment.NewLine + "- Snowcloak automatically selects a recommended format based on texture traits." +
                                                            Environment.NewLine + "- Some textures, especially ones utilizing colorsets, might not be suited for conversion and might produce visual artifacts." +
                                                            Environment.NewLine + "- Before converting textures, make sure to have the original files of the mod you are converting so you can reimport it in case of issues." +
                                                            Environment.NewLine + "- Conversion will convert all found texture duplicates (entries with more than 1 file path) automatically." +
                                                            Environment.NewLine + "- Converting textures is a very expensive operation and, depending on the amount of textures to convert, will take a while to complete.",
                                ImGuiColors.DalamudYellow);
                            if (_texturesToConvert.Count > 0 && _uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, string.Format("Start conversion of {0} texture(s)", _texturesToConvert.Count)))
                            {
                                _conversionCancellationTokenSource = _conversionCancellationTokenSource.CancelRecreate();
                                _conversionTask = _ipcManager.Penumbra.ConvertTextureFiles(_logger, _texturesToConvert, _conversionProgress, _conversionCancellationTokenSource.Token);
                            }
                        }
                    }

                    ImGui.Separator();
                    DrawTable(fileGroup);

                    fileTab.Dispose();
                }
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted("Selected file:");
        ImGui.SameLine();
        ElezenImgui.ColouredText(_selectedHash, ImGuiColors.DalamudYellow);

        if (_cachedAnalysis[_selectedObjectTab].TryGetValue(_selectedHash, out CharacterAnalyzer.FileDataEntry? item))
        {
            var filePaths = item.FilePaths;
            ImGui.TextUnformatted("Local file path:");
            ImGui.SameLine();
            ElezenImgui.WrappedText(filePaths[0]);
            if (filePaths.Count > 1)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(string.Format("(and {0} more)", filePaths.Count - 1));
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.InfoCircle);
                UiSharedService.AttachToolTip(string.Join(Environment.NewLine, filePaths.Skip(1)));
            }

            var gamepaths = item.GamePaths;
            ImGui.TextUnformatted("Used by game path:");
            ImGui.SameLine();
            ElezenImgui.WrappedText(gamepaths[0]);
            if (gamepaths.Count > 1)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted(string.Format("(and {0} more)", gamepaths.Count - 1));
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.InfoCircle);
                UiSharedService.AttachToolTip(string.Join(Environment.NewLine, gamepaths.Skip(1)));
            }
            
            if (string.Equals(item.FileType, "tex", StringComparison.OrdinalIgnoreCase) && item.TextureTraits != null)
            {
                var traits = item.TextureTraits;
                ImGui.TextUnformatted("Texture traits:");
                ImGui.SameLine();
                ElezenImgui.WrappedText(traits.FormatSummary);
                ElezenImgui.WrappedText(string.Format("Channel variance (RGB): {0}/{1}/{2}", traits.RedVariance.ToString("0.0"), traits.GreenVariance.ToString("0.0"), traits.BlueVariance.ToString("0.0")));
                ElezenImgui.WrappedText(string.Format("Alpha transitions: {0}", traits.AlphaTransitionDensity.ToString("P1")));
                if (IsRiskyConversion(item))
                {
                    ElezenImgui.ColouredWrappedText("Flagged as risky for conversion (colourset/dye path, high alpha transitions, or greyscale map).", ImGuiColors.DalamudOrange);
                }
            }
        }
    }

    public override void OnOpen()
    {
        _hasUpdate = true;
        _selectedHash = string.Empty;
        _enableTextureConversionMode = false;
        _texturesToConvert.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _conversionProgress.ProgressChanged -= ConversionProgress_ProgressChanged;
    }

    private void ConversionProgress_ProgressChanged(object? sender, (string, int) e)
    {
        _conversionCurrentFileName = e.Item1;
        _conversionCurrentFileProgress = e.Item2;
    }

    private void DrawTable(IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup)
    {
        var tableColumns = string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal)
            ? (_enableTextureConversionMode ? 7 : 6)
            : (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) ? 6 : 5);
        using var table = ImRaii.Table("Analysis", tableColumns, ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, 300));
        if (!table.Success) return;
        ImGui.TableSetupColumn("Hash");
        ImGui.TableSetupColumn("Filepaths", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Gamepaths", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("File Size", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Download Size", ImGuiTableColumnFlags.PreferSortDescending);
        if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
        {
            ImGui.TableSetupColumn("Format");
            if (_enableTextureConversionMode) ImGui.TableSetupColumn("Convert to...");
        }
        if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal))
        {
            ImGui.TableSetupColumn("Triangles", ImGuiTableColumnFlags.PreferSortDescending);
        }
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsDirty || _sortDirty)
        {
            var idx = sortSpecs.Specs.ColumnIndex;

            if (idx == 0 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 0 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 1 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.FilePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 1 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.FilePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 2 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.GamePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 2 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.GamePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 3 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.OriginalSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 3 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.OriginalSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.CompressedSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.CompressedSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);

            sortSpecs.SpecsDirty = false;
            _sortDirty = false;
        }

        foreach (var item in fileGroup)
        {
            using var text = ImRaii.PushColor(ImGuiCol.Text, new Vector4(0, 0, 0, 1), string.Equals(item.Hash, _selectedHash, StringComparison.Ordinal));
            using var text2 = ImRaii.PushColor(ImGuiCol.Text, new Vector4(1, 1, 1, 1), !item.IsComputed);
            ImGui.TableNextColumn();
            if (string.Equals(_selectedHash, item.Hash, StringComparison.Ordinal))
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ElezenTools.UI.Colour.Vector4ToColour(ImGuiColors.DalamudYellow));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ElezenTools.UI.Colour.Vector4ToColour(ImGuiColors.DalamudYellow));
            }
            ImGui.TextUnformatted(item.Hash);
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.FilePaths.Count.ToString());
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.GamePaths.Count.ToString());
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(UiSharedService.ByteToString(item.OriginalSize));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, !item.IsComputed))
                ImGui.TextUnformatted(UiSharedService.ByteToString(item.CompressedSize));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Format.Value);
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
                if (_enableTextureConversionMode)
                {
                    ImGui.TableNextColumn();
                    if (item.Format.Value.StartsWith("BC", StringComparison.Ordinal) || item.Format.Value.StartsWith("DXT", StringComparison.Ordinal)
                        || item.Format.Value.StartsWith("24864", StringComparison.Ordinal)) // BC4
                    {
                        ImGui.TextUnformatted("");
                        continue;
                    }
                    var conversionType = GetConversionType(item);
                    var conversionLabel = GetConversionLabel(conversionType);
                    var filePath = item.FilePaths[0];
                    bool toConvert = _texturesToConvert.ContainsKey(filePath);
                    if (ImGui.Checkbox("###convert" + item.Hash, ref toConvert))
                    {
                        if (toConvert)
                        {
                            _texturesToConvert[filePath] = (conversionType, item.FilePaths.Skip(1).ToArray());
                        }
                        else if (!toConvert && _texturesToConvert.ContainsKey(filePath))
                        {
                            _texturesToConvert.Remove(filePath);
                        }
                    }
                    if (toConvert)
                    {
                        _texturesToConvert[filePath] = (conversionType, item.FilePaths.Skip(1).ToArray());
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(conversionLabel);
                    if (IsRiskyConversion(item))
                    {
                        ImGui.SameLine();
                        _uiSharedService.IconText(FontAwesomeIcon.ExclamationTriangle);
                        UiSharedService.AttachToolTip("Texture flagged as risky for conversion (alpha/detail patterns or dye/colorset path). Proceed with caution when converting.");                        if (!toConvert)
                        {
                            _texturesToConvert.Remove(filePath);
                        }
                    }
                }
            }
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(UiSharedService.TrisToString(item.Triangles));
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            }
        }
    }

    private static TextureType GetConversionType(CharacterAnalyzer.FileDataEntry item)
    {
        var traits = item.TextureTraits;
        if (traits?.IsGreyscale == true)
        {
            return TextureType.Bc4Tex;
        }
        if (traits?.IsNormalMapStyle == true)
        {
            return TextureType.Bc5Tex;
        }
        if (traits != null && !traits.HasAlpha)
        {
            return TextureType.Bc1Tex;
        }
        return TextureType.Bc7Tex;
    }

    private static bool IsRiskyConversion(CharacterAnalyzer.FileDataEntry item)
    {
        return item.IsRiskyTexture;
    }

    private static string GetConversionLabel(TextureType textureType)
    {
        return textureType switch
        {
            TextureType.Bc1Tex => "BC1",
            TextureType.Bc4Tex => "BC4",
            TextureType.Bc5Tex => "BC5",
            TextureType.Bc7Tex => "BC7",
            _ => textureType.ToString()
        };
    }
}