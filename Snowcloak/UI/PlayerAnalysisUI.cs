using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data.Enum;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using System.Numerics;

namespace Snowcloak.UI;

public class PlayerAnalysisUI : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiSharedService;
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysis;
    private bool _hasUpdate = true;
    private bool _sortDirty = true;
    private string _selectedFileTypeTab = string.Empty;
    private string _selectedHash = string.Empty;
    private ObjectKind _selectedObjectTab;

    public PlayerAnalysisUI(ILogger<PlayerAnalysisUI> logger, Pair pair, SnowMediator mediator, UiSharedService uiSharedService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Character Data Analysis for " + pair.UserData.AliasOrUID + "###SnowcloakPairAnalysis" + pair.UserData.UID, performanceCollectorService)
    {
        Pair = pair;
        _uiSharedService = uiSharedService;
        Mediator.SubscribeKeyed<PairDataAnalyzedMessage>(this, Pair.UserData.UID, (_) =>
        {
            _logger.LogInformation("PairDataAnalyzedMessage received for {uid}", Pair.UserData.UID);
            _hasUpdate = true;
        });
        SizeConstraints = new()
        {
            MinimumSize = new()
            {
                X = 800,
                Y = 600
            },
            MaximumSize = new()
            {
                X = 3840,
                Y = 2160
            }
        };
        IsOpen = true;
    }

    public Pair Pair { get; private init; }
    public PairAnalyzer? PairAnalyzer => Pair.PairAnalyzer;

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    protected override void DrawInternal()
    {
        if (PairAnalyzer == null) return;
        PairAnalyzer analyzer = PairAnalyzer!;

        if (_hasUpdate)
        {
            _cachedAnalysis = analyzer.LastAnalysis.DeepClone();
            _hasUpdate = false;
            _sortDirty = true;
        }

        UiSharedService.TextWrapped($"This window shows you all files and their sizes that are currently in use by {Pair.UserData.AliasOrUID} and associated entities");

        if (_cachedAnalysis == null || _cachedAnalysis.Count == 0) return;

        bool isAnalyzing = analyzer.IsAnalysisRunning;
        bool needAnalysis = _cachedAnalysis!.Any(c => c.Value.Any(f => !f.Value.IsComputed));
        if (isAnalyzing)
        {
            UiSharedService.ColorTextWrapped($"Analyzing {analyzer.CurrentFile}/{analyzer.TotalFiles}",
                ImGuiColors.DalamudYellow);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, "Cancel analysis"))
            {
                analyzer.CancelAnalyze();
            }
        }
        else
        {
            if (needAnalysis)
            {
                UiSharedService.ColorTextWrapped("Some entries in the analysis have file size not determined yet, press the button below to compute missing data",
                    ImGuiColors.DalamudYellow);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, "Start analysis (missing entries)"))
                {
                    _ = analyzer.ComputeAnalysis(print: false);
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
                .Select(f => f.Key + ": " + f.Count() + " files, size: " + UiSharedService.ByteToString(f.Sum(v => v.OriginalSize))
                + ", compressed: " + UiSharedService.ByteToString(f.Sum(v => v.CompressedSize))));
            ImGui.SetTooltip(text);
        }
        ImGui.TextUnformatted("Total size (actual):");
        ImGui.SameLine();
        ImGui.TextUnformatted(UiSharedService.ByteToString(_cachedAnalysis!.Sum(c => c.Value.Sum(c => c.Value.OriginalSize))));
        ImGui.TextUnformatted("Total size (compressed for up/download only):");
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
        ImGui.TextUnformatted($"Total modded model triangles: {UiSharedService.TrisToString(_cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.Triangles)))}");
        ImGui.Separator();

        var playerName = analyzer.LastPlayerName;

        if (playerName.Length == 0)
        {
            playerName = Pair.PlayerName ?? string.Empty;
            analyzer.LastPlayerName = playerName;
        }

        using var tabbar = ImRaii.TabBar("objectSelection");
        foreach (var kvp in _cachedAnalysis)
        {
            using var id = ImRaii.PushId(kvp.Key.ToString());
            string tabText = kvp.Key == ObjectKind.Player ? playerName : $"{playerName}'s {kvp.Key}";
            using var tab = ImRaii.TabItem(tabText + "###" + kvp.Key.ToString());
            if (tab.Success)
            {
                var groupedfiles = kvp.Value.Select(v => v.Value).GroupBy(f => f.FileType, StringComparer.Ordinal)
                    .OrderBy(k => k.Key, StringComparer.Ordinal).ToList();

                ImGui.TextUnformatted($"Files for {tabText}");

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
                        .Select(f => f.Key + ": " + f.Count() + " files, size: " + UiSharedService.ByteToString(f.Sum(v => v.OriginalSize))
                        + ", compressed: " + UiSharedService.ByteToString(f.Sum(v => v.CompressedSize))));
                    ImGui.SetTooltip(text);
                }
                ImGui.TextUnformatted($"{kvp.Key} size (actual):");
                ImGui.SameLine();
                ImGui.TextUnformatted(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.OriginalSize)));
                ImGui.TextUnformatted($"{kvp.Key} size (download size):");
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
                ImGui.TextUnformatted($"{kvp.Key} VRAM usage:");
                ImGui.SameLine();
                var vramUsage = groupedfiles.SingleOrDefault(v => string.Equals(v.Key, "tex", StringComparison.Ordinal));
                if (vramUsage != null)
                {
                    ImGui.TextUnformatted(UiSharedService.ByteToString(vramUsage.Sum(f => f.OriginalSize)));
                }
                ImGui.TextUnformatted($"{kvp.Key} modded model triangles: {UiSharedService.TrisToString(kvp.Value.Sum(f => f.Value.Triangles))}");

                ImGui.Separator();
                if (_selectedObjectTab != kvp.Key)
                {
                    _selectedHash = string.Empty;
                    _selectedObjectTab = kvp.Key;
                    _selectedFileTypeTab = string.Empty;
                }

                using var fileTabBar = ImRaii.TabBar("fileTabs");

                foreach (IGrouping<string, CharacterAnalyzer.FileDataEntry>? fileGroup in groupedfiles)
                {
                    string fileGroupText = fileGroup.Key + " [" + fileGroup.Count() + "]";
                    var requiresCompute = fileGroup.Any(k => !k.IsComputed);
                    using var tabcol = ImRaii.PushColor(ImGuiCol.Tab, UiSharedService.Color(ImGuiColors.DalamudYellow), requiresCompute);
                    ImRaii.IEndObject fileTab;
                    using (var textcol = ImRaii.PushColor(ImGuiCol.Text, UiSharedService.Color(new(0, 0, 0, 1)),
                        requiresCompute && !string.Equals(_selectedFileTypeTab, fileGroup.Key, StringComparison.Ordinal)))
                    {
                        fileTab = ImRaii.TabItem(fileGroupText + "###" + fileGroup.Key);
                    }

                    if (!fileTab) { fileTab.Dispose(); continue; }

                    if (!string.Equals(fileGroup.Key, _selectedFileTypeTab, StringComparison.Ordinal))
                    {
                        _selectedFileTypeTab = fileGroup.Key;
                        _selectedHash = string.Empty;
                    }

                    ImGui.TextUnformatted($"{fileGroup.Key} files");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(fileGroup.Count().ToString());

                    ImGui.TextUnformatted($"{fileGroup.Key} files size (actual):");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(UiSharedService.ByteToString(fileGroup.Sum(c => c.OriginalSize)));

                    ImGui.TextUnformatted($"{fileGroup.Key} files size (download size):");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(UiSharedService.ByteToString(fileGroup.Sum(c => c.CompressedSize)));

                    ImGui.Separator();
                    DrawTable(fileGroup);

                    fileTab.Dispose();
                }
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted("Selected file:");
        ImGui.SameLine();
        UiSharedService.ColorText(_selectedHash, ImGuiColors.DalamudYellow);

        if (_cachedAnalysis[_selectedObjectTab].TryGetValue(_selectedHash, out CharacterAnalyzer.FileDataEntry? item))
        {
            var gamepaths = item.GamePaths;
            ImGui.TextUnformatted("Used by game path:");
            ImGui.SameLine();
            UiSharedService.TextWrapped(gamepaths[0]);
            if (gamepaths.Count > 1)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"(and {gamepaths.Count - 1} more)");
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.InfoCircle);
                UiSharedService.AttachToolTip(string.Join(Environment.NewLine, gamepaths.Skip(1)));
            }
        }
    }

    private void DrawTable(IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup)
    {
        var tableColumns = string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal)
            ? 5
            : (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) ? 5 : 4);
        using var table = ImRaii.Table("Analysis", tableColumns, ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, 300));
        if (!table.Success) return;
        ImGui.TableSetupColumn("Hash");
        ImGui.TableSetupColumn("Gamepaths", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("File Size", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Download Size", ImGuiTableColumnFlags.PreferSortDescending);
        if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
        {
            ImGui.TableSetupColumn("Format");
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
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.GamePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 1 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.GamePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 2 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.OriginalSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 2 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.OriginalSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 3 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.CompressedSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 3 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.CompressedSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
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
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, UiSharedService.Color(ImGuiColors.DalamudYellow));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, UiSharedService.Color(ImGuiColors.DalamudYellow));
            }
            ImGui.TextUnformatted(item.Hash);
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
            }
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(UiSharedService.TrisToString(item.Triangles));
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            }
        }
    }
}
