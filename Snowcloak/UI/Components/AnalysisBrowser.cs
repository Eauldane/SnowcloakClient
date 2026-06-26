using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Snowcloak.Core.Analysis;
using Snowcloak.Core.Performance;
using Snowcloak.Services;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI.Components;

internal sealed class AnalysisBrowserColumn
{
    public AnalysisBrowserColumn(string header, float width, ImGuiTableColumnFlags flags,
        Func<AnalysisFileEntry, IComparable>? sortSelector, Action<AnalysisFileEntry> drawCell)
    {
        Header = header;
        Width = width;
        Flags = flags;
        SortSelector = sortSelector;
        DrawCell = drawCell;
    }

    public string Header { get; }
    public float Width { get; }
    public ImGuiTableColumnFlags Flags { get; }
    public Func<AnalysisFileEntry, IComparable>? SortSelector { get; }
    public Action<AnalysisFileEntry> DrawCell { get; }
}

internal sealed class AnalysisBrowser
{
    private static readonly Vector4 PanelBg = new(0.030f, 0.075f, 0.108f, 0.62f);

    private AnalysisSnapshot? _cachedAnalysis;
    private bool _hasUpdate = true;
    private string _selectedFileTypeTab = string.Empty;
    private string _selectedHash = string.Empty;
    private ObjectKind _selectedObjectTab;
    private string _fileFilter = string.Empty;
    private DateTime? _lastAnalysisTime;

    public void MarkDirty()
    {
        _hasUpdate = true;
        _lastAnalysisTime = DateTime.Now;
    }

    public void Reset(Action? onSelectionReset = null)
    {
        _selectedHash = string.Empty;
        _selectedFileTypeTab = string.Empty;
        onSelectionReset?.Invoke();
    }

    public void Draw(IAnalysisSource source, string introText, IReadOnlyList<AnalysisBrowserColumn>? extraColumns = null,
        Action? drawOptionsPanel = null, Action? onSelectionReset = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var scale = ImGuiHelpers.GlobalScale;
        using var palette = ImRaii.PushColor(ImGuiCol.WindowBg, SnowcloakColours.CompactBg)
            .Push(ImGuiCol.ChildBg, PanelBg)
            .Push(ImGuiCol.FrameBg, new Vector4(0.050f, 0.090f, 0.125f, 0.86f))
            .Push(ImGuiCol.FrameBgHovered, new Vector4(0.070f, 0.125f, 0.175f, 0.92f))
            .Push(ImGuiCol.FrameBgActive, new Vector4(0.080f, 0.150f, 0.225f, 1f))
            .Push(ImGuiCol.CheckMark, SnowcloakColours.OnlineBlue)
            .Push(ImGuiCol.TableHeaderBg, new Vector4(0.035f, 0.080f, 0.120f, 0.94f))
            .Push(ImGuiCol.Border, SnowcloakColours.CompactBorderSubtle);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 3f * scale);
        using var childRounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 4f * scale);

        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
            ImGui.TextWrapped(introText);

        if (_hasUpdate)
        {
            _cachedAnalysis = source.GetLastAnalysisSnapshot();
            _hasUpdate = false;
        }

        if (_cachedAnalysis == null || _cachedAnalysis.IsEmpty)
            return;

        var isAnalyzing = source.IsAnalysisRunning;
        var needAnalysis = _cachedAnalysis.Files.Any(f => !f.IsComputed);

        ImGuiHelpers.ScaledDummy(new Vector2(0, 6));
        DrawStatusAndStats(source, needAnalysis, isAnalyzing);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 8));

        var kinds = _cachedAnalysis.Objects.Keys.ToList();
        if (!_cachedAnalysis.Objects.ContainsKey(_selectedObjectTab))
        {
            _selectedObjectTab = kinds[0];
            Reset(onSelectionReset);
        }

        DrawObjectKindTabs(kinds, onSelectionReset);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 8));

        var kindData = _cachedAnalysis.Objects[_selectedObjectTab].Files;
        var groupedFiles = kindData.Values
            .GroupBy(f => f.FileType, StringComparer.Ordinal)
            .OrderBy(k => k.Key, StringComparer.Ordinal)
            .ToList();

        if (drawOptionsPanel != null)
        {
            DrawOverviewAndOptions(kindData, groupedFiles, needAnalysis, isAnalyzing, drawOptionsPanel);
            ImGuiHelpers.ScaledDummy(new Vector2(0, 8));
        }

        DrawCategoryRow(groupedFiles, kindData.Count);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 4));

        var columns = BuildColumns(_selectedFileTypeTab, extraColumns);
        DrawAnalysisTable(columns, kindData);

        DrawSelectedFileDetail(kindData);
        DrawBottomBar();
    }

    private static List<AnalysisBrowserColumn> BuildColumns(string selectedFileType, IReadOnlyList<AnalysisBrowserColumn>? extraColumns)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var isAll = string.IsNullOrEmpty(selectedFileType);
        var isTex = string.Equals(selectedFileType, "tex", StringComparison.Ordinal);
        var isMdl = string.Equals(selectedFileType, "mdl", StringComparison.Ordinal);

        var columns = new List<AnalysisBrowserColumn>
        {
            new("Hash", 0f, ImGuiTableColumnFlags.WidthStretch,
                f => f.Hash, f => ImGui.TextUnformatted(f.Hash)),
            new("Filepaths", 75f * scale, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending,
                f => f.FilePaths.Count, f => ImGui.TextUnformatted(f.FilePaths.Count.ToString(CultureInfo.InvariantCulture))),
            new("Gamepaths", 85f * scale, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending,
                f => f.GamePaths.Count, f => ImGui.TextUnformatted(f.GamePaths.Count.ToString(CultureInfo.InvariantCulture))),
            new("File Size", 95f * scale, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending,
                f => f.OriginalSize, f => ImGui.TextUnformatted(ElezenImgui.ByteToString(f.OriginalSize))),
            new("Download Size", 115f * scale, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending,
                f => f.CompressedSize, DrawDownloadSizeCell),
        };

        if (isAll || isTex)
            columns.Add(new AnalysisBrowserColumn("Format", 180f * scale, ImGuiTableColumnFlags.WidthFixed,
                f => f.FormatSummary, DrawFormatCell));
        if (isMdl)
            columns.Add(new AnalysisBrowserColumn("Triangles", 95f * scale, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending,
                f => f.Triangles, f => ImGui.TextUnformatted(ElezenImgui.TrisToString(f.Triangles))));

        if (extraColumns != null && (isAll || isTex))
            columns.AddRange(extraColumns);

        return columns;
    }

    private static void DrawDownloadSizeCell(AnalysisFileEntry item)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, !item.IsComputed))
            ImGui.TextUnformatted(ElezenImgui.ByteToString(item.CompressedSize));
    }

    private static void DrawFormatCell(AnalysisFileEntry item)
    {
        if (string.Equals(item.FileType, "tex", StringComparison.Ordinal))
            ImGui.TextUnformatted(item.FormatSummary);
        else
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, "-");
    }

    private void DrawStatusAndStats(IAnalysisSource source, bool needAnalysis, bool isAnalyzing)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var fullWidth = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var gap = 10f * scale;
        var topHeight = 112f * scale;
        var leftWidth = fullWidth * 0.44f;

        DrawStatusCard(source, origin, new Vector2(leftWidth, topHeight), needAnalysis, isAnalyzing);

        var tilesStart = origin.X + leftWidth + gap;
        var tilesWidth = fullWidth - leftWidth - gap;
        var tilesMin = new Vector2(tilesStart, origin.Y);
        var tilesMax = tilesMin + new Vector2(tilesWidth, topHeight);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(tilesMin, tilesMax, Colour.Vector4ToColour(PanelBg), 5f * scale);
        drawList.AddRect(tilesMin, tilesMax, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.55f)), 5f * scale, ImDrawFlags.None, 1f * scale);

        var cellWidth = tilesWidth / 4f;
        var dividerColor = Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.45f));
        for (var i = 1; i < 4; i++)
        {
            var dividerX = tilesStart + cellWidth * i;
            drawList.AddLine(new Vector2(dividerX, origin.Y + 12f * scale), new Vector2(dividerX, origin.Y + topHeight - 12f * scale), dividerColor, 1f * scale);
        }

        var totalFiles = _cachedAnalysis!.Files.Count();
        var totalVramBytes = _cachedAnalysis.Files.Where(f => string.Equals(f.FileType, "tex", StringComparison.Ordinal)).Sum(f => f.OriginalSize);
        var totalTriangles = _cachedAnalysis.Files.Sum(f => f.Triangles);
        var actualSize = ElezenImgui.ByteToString(_cachedAnalysis.Files.Sum(f => f.OriginalSize));
        var downloadSize = ElezenImgui.ByteToString(_cachedAnalysis.Files.Sum(f => f.CompressedSize));
        var triangles = ElezenImgui.TrisToString(totalTriangles);

        var exceedsVramAutoPause = ExceedsLegacyVramThreshold(totalVramBytes);
        var exceedsTrianglesAutoPause = ExceedsLegacyTrianglesThreshold(totalTriangles);
        var exceedsAutoPause = exceedsVramAutoPause || exceedsTrianglesAutoPause;

        var cellSize = new Vector2(cellWidth, topHeight);
        DrawStatCell(new Vector2(tilesStart, origin.Y), cellSize, FontAwesomeIcon.FolderOpen, "Total files", totalFiles.ToString(CultureInfo.InvariantCulture), Vector4.One, false);
        DrawStatCell(new Vector2(tilesStart + cellWidth, origin.Y), cellSize, FontAwesomeIcon.Save, "Total size (actual)", actualSize, Vector4.One, false);
        DrawStatCell(new Vector2(tilesStart + cellWidth * 2f, origin.Y), cellSize, FontAwesomeIcon.Download, "Total size (download size)", downloadSize,
            needAnalysis ? ImGuiColors.DalamudYellow : Vector4.One, needAnalysis && !isAnalyzing);
        DrawStatCell(new Vector2(tilesStart + cellWidth * 3f, origin.Y), cellSize, FontAwesomeIcon.Cube, "Total modded triangles", triangles,
            exceedsTrianglesAutoPause ? ImGuiColors.DalamudOrange : Vector4.One, false);

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + topHeight));

        if (exceedsAutoPause)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(ImGuiColors.DalamudOrange, FontAwesomeIcon.ExclamationTriangle.ToIconString());
            ImGui.SameLine(0f, 6f * scale);
            ImGui.TextColored(ImGuiColors.DalamudOrange,
                string.Format(CultureInfo.InvariantCulture, "This exceeds the default auto-pause thresholds ({0} MiB VRAM / {1}k triangles) other clients use.",
                    PerformanceBudgetPolicy.LegacyAutoBlockVramThresholdMiB, PerformanceBudgetPolicy.LegacyAutoBlockTrianglesThresholdThousands));
            ImGuiHelpers.ScaledDummy(new Vector2(0, 4));
        }
    }

    private static void DrawStatusCard(IAnalysisSource source, Vector2 min, Vector2 size, bool needAnalysis, bool isAnalyzing)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        var accent = isAnalyzing || needAnalysis ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen;

        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(PanelBg), 5f * scale);
        drawList.AddRectFilled(min with { X = min.X + 5f * scale }, new Vector2(min.X + 5f * scale + 2.5f * scale, max.Y), Colour.Vector4ToColour(accent), 0f);
        drawList.AddRect(min, max, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.55f)), 5f * scale, ImDrawFlags.None, 1f * scale);

        var padding = new Vector2(16f, 12f) * scale;
        var contentWidth = size.X - padding.X * 2f;
        var buttonHeight = 30f * scale;

        FontAwesomeIcon icon;
        string title;
        string body;
        Vector4 titleColor;
        if (isAnalyzing)
        {
            icon = FontAwesomeIcon.Sync;
            titleColor = ImGuiColors.DalamudYellow;
            title = string.Format(CultureInfo.InvariantCulture, "Analyzing {0}/{1}", source.CurrentFile, source.TotalFiles);
            body = "Reading files and computing sizes. This can take a moment.";
        }
        else if (needAnalysis)
        {
            icon = FontAwesomeIcon.ExclamationTriangle;
            titleColor = ImGuiColors.DalamudYellow;
            title = "Analysis incomplete";
            body = "Run the analysis to find missing file sizes and calculate accurate download totals.";
        }
        else
        {
            icon = FontAwesomeIcon.CheckCircle;
            titleColor = ImGuiColors.HealerGreen;
            title = "Analysis complete";
            body = "All file sizes and download totals are up to date.";
        }

        ImGui.SetCursorScreenPos(min + padding);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(titleColor, icon.ToIconString());
        ImGui.SameLine(0f, 8f * scale);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(titleColor, title);

        ImGui.SetCursorScreenPos(new Vector2(min.X + padding.X, min.Y + padding.Y + ImGui.GetTextLineHeightWithSpacing()));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + contentWidth);
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
            ImGui.TextWrapped(body);
        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(new Vector2(min.X + padding.X, max.Y - padding.Y - buttonHeight));
        if (isAnalyzing)
        {
            if (DrawAccentButton(FontAwesomeIcon.StopCircle, "Cancel analysis", "cancel-analysis", new Vector4(0.420f, 0.180f, 0.220f, 1f)))
                source.CancelAnalyze();
        }
        else if (needAnalysis)
        {
            if (DrawAccentButton(FontAwesomeIcon.PlayCircle, "Start analysis (missing entries)", "start-missing"))
                _ = source.ComputeAnalysis(print: false);
        }
        else
        {
            if (DrawAccentButton(FontAwesomeIcon.PlayCircle, "Start analysis (recalculate all entries)", "start-all"))
                _ = source.ComputeAnalysis(print: false, recalculate: true);
        }
    }

    private static void DrawStatCell(Vector2 min, Vector2 size, FontAwesomeIcon icon, string label, string value, Vector4 valueColor, bool warning)
    {
        const float valueScale = 1.35f;
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();

        var padding = new Vector2(14f, 12f) * scale;
        var innerWidth = size.X - padding.X * 2f;

        var labelLineHeight = ImGui.GetTextLineHeight();
        var labelLines = WrapLabel(label, innerWidth, 2);
        for (var i = 0; i < labelLines.Count; i++)
            drawList.AddText(new Vector2(min.X + padding.X, min.Y + padding.Y + i * labelLineHeight), Colour.Vector4ToColour(SnowcloakColours.CompactTextMuted), labelLines[i]);

        ImGui.PushFont(UiBuilder.IconFont);
        var iconStr = icon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();

        var valuePx = ImGui.GetFontSize() * valueScale;
        Vector2 valueSize;
        using (ElezenFonts.Push(valuePx))
            valueSize = ImGui.CalcTextSize(value);

        var valueTop = min.Y + padding.Y + labelLines.Count * labelLineHeight + 9f * scale;
        var rowHeight = MathF.Max(iconSize.Y, valueSize.Y);

        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(new Vector2(min.X + padding.X, valueTop + (rowHeight - iconSize.Y) * 0.5f), Colour.Vector4ToColour(SnowcloakColours.OnlineBlue), iconStr);
        ImGui.PopFont();

        var valueX = min.X + padding.X + iconSize.X + 10f * scale;
        ImGui.SetCursorScreenPos(new Vector2(valueX, valueTop + (rowHeight - valueSize.Y) * 0.5f));
        using (ElezenFonts.Push(valuePx))
        using (ImRaii.PushColor(ImGuiCol.Text, valueColor))
            ImGui.TextUnformatted(value);

        if (warning)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            drawList.AddText(new Vector2(valueX + valueSize.X + 8f * scale, valueTop + (rowHeight - iconSize.Y) * 0.5f), Colour.Vector4ToColour(ImGuiColors.DalamudYellow), FontAwesomeIcon.ExclamationCircle.ToIconString());
            ImGui.PopFont();
        }
    }

    private static List<string> WrapLabel(string text, float maxWidth, int maxLines)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && ImGui.CalcTextSize(candidate).X > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0)
            lines.Add(current);

        if (lines.Count > maxLines)
        {
            var trimmed = lines.Take(maxLines).ToList();
            trimmed[maxLines - 1] = string.Join(" ", lines.Skip(maxLines - 1));
            return trimmed;
        }
        return lines;
    }

    private void DrawObjectKindTabs(List<ObjectKind> kinds, Action? onSelectionReset)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var min = ImGui.GetCursorScreenPos();
        var fullWidth = ImGui.GetContentRegionAvail().X;
        var height = 30f * scale;
        var drawList = ImGui.GetWindowDrawList();
        var x = min.X;

        foreach (var kind in kinds)
        {
            var label = kind.ToString();
            var textSize = ImGui.CalcTextSize(label);
            var tabWidth = textSize.X + 28f * scale;
            var active = _selectedObjectTab == kind;

            ImGui.SetCursorScreenPos(new Vector2(x, min.Y));
            ImGui.InvisibleButton($"##kind-{kind}", new Vector2(tabWidth, height));
            var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
            var hovered = ImGui.IsItemHovered();

            var color = active || hovered ? Vector4.One : SnowcloakColours.CompactTextMuted;
            drawList.AddText(new Vector2(x + (tabWidth - textSize.X) * 0.5f, min.Y + (height - textSize.Y) * 0.5f), Colour.Vector4ToColour(color), label);
            if (active)
                drawList.AddRectFilled(new Vector2(x + 6f * scale, min.Y + height - 2.5f * scale), new Vector2(x + tabWidth - 6f * scale, min.Y + height), Colour.Vector4ToColour(SnowcloakColours.OnlineBlue), 0f);

            if (clicked && !active)
            {
                _selectedObjectTab = kind;
                Reset(onSelectionReset);
            }

            x += tabWidth;
        }

        drawList.AddLine(new Vector2(min.X, min.Y + height), new Vector2(min.X + fullWidth, min.Y + height),
            Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.45f)), 1f * scale);
        ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + height));
    }

    private void DrawOverviewAndOptions(IReadOnlyDictionary<string, AnalysisFileEntry> kindData,
        List<IGrouping<string, AnalysisFileEntry>> groupedFiles, bool needAnalysis, bool isAnalyzing, Action drawOptionsPanel)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var fullWidth = ImGui.GetContentRegionAvail().X;
        var gap = 10f * scale;
        var panelHeight = 168f * scale;
        var leftWidth = fullWidth * 0.5f - gap * 0.5f;
        var rightWidth = fullWidth - leftWidth - gap;
        var kindLabel = _selectedObjectTab.ToString();

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f) * scale))
        {
            using (var overview = ImRaii.Child("overview", new Vector2(leftWidth, panelHeight), border: true))
            {
                if (overview)
                {
                    DrawPanelTitle(string.Format(CultureInfo.InvariantCulture, "{0} overview", kindLabel));

                    var vramGroup = groupedFiles.SingleOrDefault(v => string.Equals(v.Key, "tex", StringComparison.Ordinal));
                    var vramBytes = vramGroup?.Sum(f => f.OriginalSize) ?? 0;
                    var vram = vramGroup != null ? ElezenImgui.ByteToString(vramBytes) : "-";
                    var triangleCount = kindData.Sum(f => f.Value.Triangles);
                    var vramColor = ExceedsLegacyVramThreshold(vramBytes) ? ImGuiColors.DalamudOrange : (Vector4?)null;
                    var triangleColor = ExceedsLegacyTrianglesThreshold(triangleCount) ? ImGuiColors.DalamudOrange : (Vector4?)null;

                    using (ImRaii.Group())
                    {
                        DrawStat("Files for " + kindLabel, kindData.Count.ToString(CultureInfo.InvariantCulture),
                            string.Join(Environment.NewLine, groupedFiles.Select(f => string.Format(CultureInfo.InvariantCulture, "{0}: {1} files, size: {2}, compressed: {3}",
                                f.Key, f.Count(), ElezenImgui.ByteToString(f.Sum(v => v.OriginalSize)), ElezenImgui.ByteToString(f.Sum(v => v.CompressedSize))))));
                        DrawStat(kindLabel + " size (actual)", ElezenImgui.ByteToString(kindData.Sum(c => c.Value.OriginalSize)));
                        DrawStat(kindLabel + " size (download size)", ElezenImgui.ByteToString(kindData.Sum(c => c.Value.CompressedSize)),
                            null, needAnalysis && !isAnalyzing ? ImGuiColors.DalamudYellow : (Vector4?)null, needAnalysis && !isAnalyzing);
                    }
                    ImGui.SameLine(0f, 40f * scale);
                    using (ImRaii.Group())
                    {
                        DrawStat(kindLabel + " VRAM usage", vram, null, vramColor);
                        DrawStat(kindLabel + " modded model triangles", ElezenImgui.TrisToString(triangleCount), null, triangleColor);
                    }
                }
            }

            ImGui.SameLine(0f, gap);

            using (var options = ImRaii.Child("options", new Vector2(rightWidth, panelHeight), border: true))
            {
                if (options)
                {
                    DrawPanelTitle("Options");
                    drawOptionsPanel();
                }
            }
        }
    }

    private void DrawCategoryRow(List<IGrouping<string, AnalysisFileEntry>> groupedFiles, int totalCount)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var rowStartY = ImGui.GetCursorScreenPos().Y;

        if (DrawChip(string.Format(CultureInfo.InvariantCulture, "All [{0}]", totalCount), string.IsNullOrEmpty(_selectedFileTypeTab), "chip-all"))
        {
            _selectedFileTypeTab = string.Empty;
            _selectedHash = string.Empty;
        }

        foreach (var group in groupedFiles)
        {
            ImGui.SameLine(0f, 6f * scale);
            var label = string.Format(CultureInfo.InvariantCulture, "{0} [{1}]", FriendlyTypeName(group.Key), group.Count());
            if (DrawChip(label, string.Equals(_selectedFileTypeTab, group.Key, StringComparison.Ordinal), "chip-" + group.Key))
            {
                _selectedFileTypeTab = group.Key;
                _selectedHash = string.Empty;
            }
        }

        var searchWidth = MathF.Min(280f * scale, ImGui.GetContentRegionAvail().X);
        var rightEdge = ImGui.GetWindowContentRegionMax().X;
        ImGui.SameLine();
        ImGui.SetCursorPosX(rightEdge - searchWidth);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (30f * scale - ImGui.GetFrameHeight()) * 0.5f);
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##analysis-filter", "Filter by filename or path...", ref _fileFilter, 256);

        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X, rowStartY + 30f * scale + 4f * scale));
    }

    private void DrawAnalysisTable(List<AnalysisBrowserColumn> columns, IReadOnlyDictionary<string, AnalysisFileEntry> kindData)
    {
        var scale = ImGuiHelpers.GlobalScale;

        var bottomReserve = 30f * scale + 20f * scale;
        var detailLines = 0;
        if (kindData.TryGetValue(_selectedHash, out var selectedItem))
            detailLines = string.Equals(selectedItem.FileType, "tex", StringComparison.Ordinal) && selectedItem.TextureTraits != null ? 4 : 3;
        var detailReserve = detailLines > 0 ? detailLines * ImGui.GetTextLineHeightWithSpacing() + 8f * scale : 0f;
        var tableHeight = MathF.Max(160f * scale, ImGui.GetContentRegionAvail().Y - bottomReserve - detailReserve - ImGui.GetStyle().ItemSpacing.Y * 3f);

        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(ImGui.GetStyle().CellPadding.X, 6f * scale));
        using var table = ImRaii.Table("Analysis", columns.Count,
            ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
            new Vector2(0, tableHeight));
        if (!table.Success)
            return;

        foreach (var col in columns)
            ImGui.TableSetupColumn(col.Header, col.Flags, col.Width);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var rows = GetFilteredSortedFiles(columns, kindData);

        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawRow(rows[i], columns);
        }
        clipper.End();
    }

    private void DrawRow(AnalysisFileEntry item, List<AnalysisBrowserColumn> columns)
    {
        var selected = string.Equals(item.Hash, _selectedHash, StringComparison.Ordinal);
        ImGui.TableNextRow();
        if (selected)
        {
            var highlight = Colour.Vector4ToColour(new Vector4(SnowcloakColours.OnlineBlue.X, SnowcloakColours.OnlineBlue.Y, SnowcloakColours.OnlineBlue.Z, 0.22f));
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, highlight);
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, highlight);
        }

        foreach (var col in columns)
        {
            ImGui.TableNextColumn();
            col.DrawCell(item);
            if (ImGui.IsItemClicked())
                _selectedHash = item.Hash;
        }
    }

    private bool MatchesFilter(AnalysisFileEntry item)
    {
        if (string.IsNullOrWhiteSpace(_fileFilter))
            return true;

        var filter = _fileFilter;
        return item.Hash.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.FormatSummary.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.FilePaths.Any(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase))
            || item.GamePaths.Any(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private List<AnalysisFileEntry> GetFilteredSortedFiles(List<AnalysisBrowserColumn> columns, IReadOnlyDictionary<string, AnalysisFileEntry> kindData)
    {
        var cat = _selectedFileTypeTab;
        var isAll = string.IsNullOrEmpty(cat);

        IEnumerable<AnalysisFileEntry> files = kindData.Values
            .Where(f => (isAll || string.Equals(f.FileType, cat, StringComparison.Ordinal)) && MatchesFilter(f));

        var sortSpecs = ImGui.TableGetSortSpecs();
        var idx = sortSpecs.Specs.ColumnIndex;
        if (idx >= 0 && idx < columns.Count && columns[idx].SortSelector != null)
        {
            var asc = sortSpecs.Specs.SortDirection != ImGuiSortDirection.Descending;
            var selector = columns[idx].SortSelector!;
            files = asc ? files.OrderBy(selector) : files.OrderByDescending(selector);
        }
        sortSpecs.SpecsDirty = false;

        return files.ToList();
    }

    private void DrawSelectedFileDetail(IReadOnlyDictionary<string, AnalysisFileEntry> kindData)
    {
        if (string.IsNullOrEmpty(_selectedHash) || !kindData.TryGetValue(_selectedHash, out var item))
            return;

        ImGuiHelpers.ScaledDummy(new Vector2(0, 2));
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Selected file:");
        ImGui.SameLine();
        ElezenImgui.ColouredText(_selectedHash, SnowcloakColours.OnlineBlue);

        DrawPathLine("Local file path:", item.FilePaths);
        DrawPathLine("Used by game path:", item.GamePaths);

        if (string.Equals(item.FileType, "tex", StringComparison.OrdinalIgnoreCase) && item.TextureTraits != null)
        {
            var traits = item.TextureTraits;
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Texture traits:");
            ImGui.SameLine();
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "{0}   •   Channel variance R/G/B {1}/{2}/{3}   •   Alpha transitions {4}",
                traits.FormatSummary,
                traits.RedVariance.ToString("0.0", CultureInfo.InvariantCulture),
                traits.GreenVariance.ToString("0.0", CultureInfo.InvariantCulture),
                traits.BlueVariance.ToString("0.0", CultureInfo.InvariantCulture),
                traits.AlphaTransitionDensity.ToString("P1", CultureInfo.InvariantCulture)));
            if (item.IsRiskyTexture)
            {
                ImGui.SameLine();
                ElezenImgui.ColouredText("  (risky for compression)", ImGuiColors.DalamudOrange);
            }
        }
    }

    private static void DrawPathLine(string label, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        ImGui.TextColored(SnowcloakColours.CompactTextMuted, label);
        ImGui.SameLine();
        ImGui.TextUnformatted(paths[0]);
        if (paths.Count > 1)
        {
            ImGui.SameLine();
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, string.Format(CultureInfo.InvariantCulture, "(and {0} more)", paths.Count - 1));
            ImGui.SameLine();
            ElezenImgui.ShowIcon(FontAwesomeIcon.InfoCircle, SnowcloakColours.CompactTextMuted);
            ElezenImgui.AttachTooltip(string.Join(Environment.NewLine, paths.Skip(1)));
        }
    }

    private void DrawBottomBar()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var avail = ImGui.GetContentRegionAvail().Y;
        var barHeight = 30f * scale;
        if (avail > barHeight + 12f * scale)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (avail - barHeight - 10f * scale));

        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(min, min with { X = min.X + width },
            Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.40f)), 1f * scale);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 4));

        var lastAnalysis = _lastAnalysisTime.HasValue ? _lastAnalysisTime.Value.ToString("g", CultureInfo.InvariantCulture) : "Never";
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
            ImGui.TextUnformatted("Last analysis: " + lastAnalysis);

        ImGui.SameLine();
        var refreshWidth = ImGui.CalcTextSize("Refresh").X + 30f * scale + 8f * scale;
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - refreshWidth);
        if (DrawAccentButton(FontAwesomeIcon.SyncAlt, "Refresh", "refresh"))
            MarkDirty();
    }

    private static void DrawPanelTitle(string title)
    {
        ImGui.TextColored(SnowcloakColours.OnlineBlue, title);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 2));
    }

    private static bool ExceedsLegacyVramThreshold(long vramBytes)
    {
        return vramBytes > PerformanceBudgetPolicy.LegacyAutoBlockVramThresholdMiB * 1024L * 1024L;
    }

    private static bool ExceedsLegacyTrianglesThreshold(long triangleCount)
    {
        return triangleCount > PerformanceBudgetPolicy.LegacyAutoBlockTrianglesThresholdThousands * 1000L;
    }

    private static void DrawStat(string label, string value, string? tooltip = null, Vector4? valueColor = null, bool warning = false)
    {
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, label);
        ImGui.SameLine();
        ImGui.TextColored(valueColor ?? Vector4.One, value);
        if (warning)
        {
            ImGui.SameLine();
            ElezenImgui.ShowIcon(FontAwesomeIcon.ExclamationCircle, ImGuiColors.DalamudYellow);
            ElezenImgui.AttachTooltip("Click \"Start analysis\" to calculate download size");
        }
        if (!string.IsNullOrEmpty(tooltip))
        {
            ImGui.SameLine();
            ElezenImgui.ShowIcon(FontAwesomeIcon.InfoCircle, SnowcloakColours.CompactTextMuted);
            ElezenImgui.AttachTooltip(tooltip);
        }
    }

    private static bool DrawChip(string label, bool active, string id)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var textSize = ImGui.CalcTextSize(label);
        var padX = 16f * scale;
        var size = new Vector2(textSize.X + padX * 2f, 30f * scale);
        var min = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton("##" + id, size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        Vector4 fill, border, textColor;
        if (active)
        {
            fill = new Vector4(0.145f, 0.290f, 0.470f, 1f);
            border = new Vector4(0.230f, 0.410f, 0.620f, 1f);
            textColor = Vector4.One;
        }
        else if (hovered)
        {
            fill = new Vector4(0.075f, 0.130f, 0.185f, 0.85f);
            border = SnowcloakColours.OnlineBlue;
            textColor = Vector4.One;
        }
        else
        {
            fill = new Vector4(0.045f, 0.090f, 0.125f, 0.85f);
            border = SnowcloakColours.CompactBorderSubtle;
            textColor = SnowcloakColours.CompactTextMuted;
        }

        drawList.AddRectFilled(min, min + size, Colour.Vector4ToColour(fill), 5f * scale);
        drawList.AddRect(min, min + size, Colour.Vector4ToColour(border), 5f * scale, ImDrawFlags.None, 1f * scale);
        drawList.AddText(min + new Vector2(padX, (size.Y - textSize.Y) * 0.5f), Colour.Vector4ToColour(textColor), label);

        return clicked;
    }

    internal static bool DrawAccentButton(FontAwesomeIcon icon, string label, string id, Vector4? tint = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushFont(UiBuilder.IconFont);
        var iconStr = icon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();
        var textSize = ImGui.CalcTextSize(label);
        var gap = 8f * scale;
        var padX = 14f * scale;
        var size = new Vector2(padX * 2f + iconSize.X + gap + textSize.X, 30f * scale);
        var min = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton("##accent-" + id, size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var drawList = ImGui.GetWindowDrawList();

        var baseColor = tint ?? new Vector4(0.145f, 0.290f, 0.470f, 1f);
        var fill = active
            ? new Vector4(MathF.Min(1f, baseColor.X * 1.45f), MathF.Min(1f, baseColor.Y * 1.45f), MathF.Min(1f, baseColor.Z * 1.45f), 1f)
            : hovered
                ? new Vector4(MathF.Min(1f, baseColor.X * 1.25f), MathF.Min(1f, baseColor.Y * 1.25f), MathF.Min(1f, baseColor.Z * 1.25f), 1f)
                : baseColor;
        drawList.AddRectFilled(min, min + size, Colour.Vector4ToColour(fill), 4f * scale);

        var contentWidth = iconSize.X + gap + textSize.X;
        var iconPos = min + new Vector2((size.X - contentWidth) * 0.5f, (size.Y - iconSize.Y) * 0.5f);
        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(iconPos, Colour.Vector4ToColour(Vector4.One), iconStr);
        ImGui.PopFont();
        drawList.AddText(new Vector2(iconPos.X + iconSize.X + gap, min.Y + (size.Y - textSize.Y) * 0.5f), Colour.Vector4ToColour(Vector4.One), label);

        return clicked;
    }

    private static string FriendlyTypeName(string fileType)
    {
        return fileType switch
        {
            "mdl" => "Model",
            "tex" => "Texture",
            "mtrl" => "Material",
            "sklb" => "Skeleton",
            "skp" => "Skeleton Params",
            "shpk" => "Shader",
            "shcd" => "Shader Code",
            "pbd" => "Bone Data",
            "phyb" => "Physics",
            "eid" => "Bind Points",
            "atch" => "Attach",
            "avfx" => "VFX",
            "scd" => "Sound",
            _ => fileType.ToUpperInvariant(),
        };
    }
}
