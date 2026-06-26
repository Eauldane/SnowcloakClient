using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Core.Performance;
using Snowcloak.FileCache;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Performance;
using Snowcloak.Services.ServerConfiguration;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI.Components;

public sealed class PerformanceSettingsPanel
{
    private const string PerfTabGeneral = "General";
    private const string PerfTabCrowd = "Crowd Control";
    private const string PerfTabAutoBlock = "Auto-Block";
    private const string PerfTabStatistics = "Statistics";
    private const string PerfTabNullification = "Mod Nullification";
    private const string PerfTabLists = "Lists";
    private const float MiBPerGiB = 1024f;

    private readonly BlockListStore _blockListStore;
    private readonly CacheMonitor _cacheMonitor;
    private readonly FileCacheManager _fileCacheManager;
    private readonly GpuMemoryBudgetService _gpuMemoryBudgetService;
    private readonly NotesStore _notesStore;
    private readonly PairManager _pairManager;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly SnowMediator _mediator;
    private readonly UiFontService _fontService;
    private readonly UsageStatisticsService _usageStatisticsService;
    private string _performanceActiveTab = PerfTabGeneral;
    private bool _perfUnapplied;
    private string _uidToAddForIgnore = string.Empty;
    private int _selectedEntry = -1;
    private string _uidToAddForIgnoreBlacklist = string.Empty;
    private int _selectedEntryBlacklist = -1;

    public PerformanceSettingsPanel(
        BlockListStore blockListStore,
        CacheMonitor cacheMonitor,
        FileCacheManager fileCacheManager,
        GpuMemoryBudgetService gpuMemoryBudgetService,
        NotesStore notesStore,
        PairManager pairManager,
        PlayerPerformanceConfigService playerPerformanceConfigService,
        PlayerPerformanceService playerPerformanceService,
        SnowMediator mediator,
        UiFontService fontService,
        UsageStatisticsService usageStatisticsService)
    {
        _blockListStore = blockListStore;
        _cacheMonitor = cacheMonitor;
        _fileCacheManager = fileCacheManager;
        _gpuMemoryBudgetService = gpuMemoryBudgetService;
        _notesStore = notesStore;
        _pairManager = pairManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _playerPerformanceService = playerPerformanceService;
        _mediator = mediator;
        _fontService = fontService;
        _usageStatisticsService = usageStatisticsService;
    }

    public void Draw()
    {
        _performanceActiveTab = ModernTabBar.Draw("performanceTabs",
            new[] { PerfTabGeneral, PerfTabCrowd, PerfTabAutoBlock, PerfTabStatistics, PerfTabNullification, PerfTabLists }, _performanceActiveTab);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        var recalculatePerformance = false;
        string? recalculatePerformanceUID = null;

        if (string.Equals(_performanceActiveTab, PerfTabGeneral, StringComparison.Ordinal))
        {
            DrawPerformanceGeneral(ref recalculatePerformance);
        }
        else if (string.Equals(_performanceActiveTab, PerfTabCrowd, StringComparison.Ordinal))
        {
            DrawPerformanceCrowdControl(ref recalculatePerformance);
        }
        else if (string.Equals(_performanceActiveTab, PerfTabAutoBlock, StringComparison.Ordinal))
        {
            DrawPerformanceAutoBlock(ref recalculatePerformance);
        }
        else if (string.Equals(_performanceActiveTab, PerfTabStatistics, StringComparison.Ordinal))
        {
            DrawPerformanceStatistics();
        }
        else if (string.Equals(_performanceActiveTab, PerfTabNullification, StringComparison.Ordinal))
        {
            DrawModNullificationSettings();
        }
        else if (string.Equals(_performanceActiveTab, PerfTabLists, StringComparison.Ordinal))
        {
            DrawPerformanceLists(ref recalculatePerformance, ref recalculatePerformanceUID);
        }

        if (recalculatePerformance)
        {
            _mediator.Publish(new RecalculatePerformanceMessage(recalculatePerformanceUID));
        }
    }

    private void DrawPerformanceGeneral(ref bool recalculatePerformance)
    {
        ElezenImgui.WrappedText("These settings control local-only performance protection. Per-player auto-blocking applies to direct pairs and syncshell members, while crowd control only manages visible syncshell members.");
        ImGui.Separator();

        _fontService.BigText("Global Configuration");

        var alwaysShrinkTextures = _playerPerformanceConfigService.Current.TextureShrinkMode == TextureShrinkMode.Always;
        var deleteOriginalTextures = _playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal;

        using (ImRaii.Disabled(deleteOriginalTextures))
        {
            if (ImGui.Checkbox("Shrink downloaded textures", ref alwaysShrinkTextures))
            {
                _playerPerformanceConfigService.Update(c => c.TextureShrinkMode = alwaysShrinkTextures ? TextureShrinkMode.Always : TextureShrinkMode.Never);
                recalculatePerformance = true;
                _cacheMonitor.ClearSubstStorage();
            }
        }
        ElezenImgui.DrawHelpText("Automatically shrinks texture resolution of synced players to reduce VRAM utilization."
            + ElezenImgui.TooltipSeparator + "Texture Size Limit (DXT/BC5/BC7 Compressed): 2048x2048" + Environment.NewLine
            + "Texture Size Limit (A8R8G8B8 Uncompressed): 1024x1024" + ElezenImgui.TooltipSeparator
            + "Enable to reduce lag in large crowds." + Environment.NewLine
            + "Disable this for higher quality during GPose.");

        using (ImRaii.Disabled(!alwaysShrinkTextures || _cacheMonitor.FileCacheSize < 0))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox("Delete original textures from disk", ref deleteOriginalTextures))
            {
                _playerPerformanceConfigService.Update(c => c.TextureShrinkDeleteOriginal = deleteOriginalTextures);
                _ = Task.Run(() =>
                {
                    _cacheMonitor.DeleteSubstOriginals();
                    _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                });
            }
            ElezenImgui.DrawHelpText("Deletes original, full-sized, textures from disk after downloading and shrinking." + ElezenImgui.TooltipSeparator
                + "Caution!!! This will cause a re-download of all textures when the shrink option is disabled.");
        }

        var totalVramBytes = _pairManager.GetOnlineUserPairs().Where(p => p.IsVisible && p.LastAppliedApproximateVRAMBytes > 0).Sum(p => p.LastAppliedApproximateVRAMBytes);
        var gpuBudget = _gpuMemoryBudgetService.GetCurrentBudget();
        var currentVramColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        if (gpuBudget != null)
        {
            var totalGpuBytes = gpuBudget.TotalBytes > 0 ? gpuBudget.TotalBytes : gpuBudget.BudgetBytes;
            var usageRatio = totalGpuBytes <= 0
                ? totalVramBytes > 0 ? 1f : 0f
                : Math.Clamp((float)totalVramBytes / totalGpuBytes, 0f, 1f);
            currentVramColor = SyncshellBudgetService.GetUsageColor(usageRatio);
        }

        ImGui.TextUnformatted("Current visible VRAM utilization across nearby synced players:");
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, currentVramColor))
        {
            ImGui.TextUnformatted(FormatGiB(totalVramBytes));
        }

        if (gpuBudget != null)
        {
            ImGui.TextUnformatted("Detected local GPU VRAM:");
            ImGui.SameLine();
            ImGui.TextUnformatted(FormatGiB(gpuBudget.TotalBytes > 0 ? gpuBudget.TotalBytes : gpuBudget.BudgetBytes));
            ElezenImgui.ColouredWrappedText($"Adapter: {gpuBudget.AdapterName} (Snowcloak defaults use 75% of total VRAM to leave headroom for the game and desktop)", ImGuiColors.DalamudGrey);
        }
        else
        {
            ElezenImgui.ColouredWrappedText("Local GPU VRAM detection is unavailable on this system.", ImGuiColors.DalamudGrey);
        }
    }

    private void DrawPerformanceCrowdControl(ref bool recalculatePerformance)
    {
        _fontService.BigText("Crowd Control");
        var crowdPrioritySnapshot = _playerPerformanceService.GetCrowdPrioritySnapshot();
        ImGui.TextUnformatted("Visible syncshell members in range:");
        ImGui.SameLine();
        ImGui.TextUnformatted(crowdPrioritySnapshot.VisibleMembers.ToString(CultureInfo.InvariantCulture));
        ImGui.TextUnformatted("Currently applying after local holds:");
        ImGui.SameLine();
        ImGui.TextUnformatted(crowdPrioritySnapshot.ActiveMembers.ToString(CultureInfo.InvariantCulture));
        ImGui.TextUnformatted("Current shell-visible VRAM:");
        ImGui.SameLine();
        ImGui.TextUnformatted(FormatGiB(crowdPrioritySnapshot.ActiveVramBytes));
        ImGui.TextUnformatted("Current shell-visible triangles:");
        ImGui.SameLine();
        ImGui.TextUnformatted(SyncshellBudgetService.FormatTriangles(crowdPrioritySnapshot.ActiveTriangleCount));
        if (crowdPrioritySnapshot.CrowdPausedMembers > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
            {
                ImGui.TextUnformatted($"{crowdPrioritySnapshot.CrowdPausedMembers} member(s) are currently locally held by crowd priority.");
            }
        }

        var crowdPriorityModeEnabled = _playerPerformanceConfigService.Current.CrowdPriorityModeEnabled;
        if (ImGui.Checkbox("Enable syncshell crowd control", ref crowdPriorityModeEnabled))
        {
            _playerPerformanceConfigService.Update(c => c.CrowdPriorityModeEnabled = crowdPriorityModeEnabled);
            recalculatePerformance = true;
        }
        ElezenImgui.DrawHelpText("Local only. When crowd pressure exceeds the thresholds below, Snowcloak temporarily holds the lowest-priority visible syncshell members first."
            + Environment.NewLine + "Priority order: direct pairs, party members, syncshell owners, moderators, pinned members, then normal syncshell members."
            + Environment.NewLine + "Set any threshold to 0 to disable that specific limit.");

        using (ImRaii.Disabled(!crowdPriorityModeEnabled))
        {
            using var indent = ImRaii.PushIndent();
            var visibleCrowdThreshold = _playerPerformanceConfigService.Current.CrowdPriorityVisibleMembersThreshold;
            var crowdVramThreshold = _playerPerformanceConfigService.Current.CrowdPriorityVRAMThresholdMiB;
            var crowdTriangleThreshold = _playerPerformanceConfigService.Current.CrowdPriorityTrianglesThresholdThousands;

            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Visible member threshold", ref visibleCrowdThreshold))
            {
                _playerPerformanceConfigService.Update(c => c.CrowdPriorityVisibleMembersThreshold = Math.Max(0, visibleCrowdThreshold));
                recalculatePerformance = true;
            }

            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (InputGiBThreshold("Shell-visible VRAM threshold", ref crowdVramThreshold))
            {
                _playerPerformanceConfigService.Update(c => c.CrowdPriorityVRAMThresholdMiB = Math.Max(0, crowdVramThreshold));
                recalculatePerformance = true;
            }
            ImGui.SameLine();
            ImGui.Text("(GiB)");
            ElezenImgui.DrawHelpText("Suggested default: 75% of your detected local GPU VRAM, leaving headroom for the game and desktop, with an 8 GiB fallback.");

            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Shell-visible triangle threshold", ref crowdTriangleThreshold))
            {
                _playerPerformanceConfigService.Update(c => c.CrowdPriorityTrianglesThresholdThousands = Math.Max(0, crowdTriangleThreshold));
                recalculatePerformance = true;
            }
            ImGui.SameLine();
            ImGui.Text("(thousand triangles)");
            ElezenImgui.DrawHelpText($"Suggested default: {PerformanceBudgetPolicy.RecommendedTrianglesThresholdThousands / 1000} million triangles.");
        }
    }

    private void DrawPerformanceAutoBlock(ref bool recalculatePerformance)
    {
        _fontService.BigText("Global Auto-Block Thresholds");
        var autoPause = _playerPerformanceConfigService.Current.AutoPausePlayersExceedingThresholds;
        if (ImGui.Checkbox("Automatically block players exceeding thresholds", ref autoPause))
        {
            _playerPerformanceConfigService.Update(c => c.AutoPausePlayersExceedingThresholds = autoPause);
            recalculatePerformance = true;
        }
        ElezenImgui.DrawHelpText("When enabled, Snowcloak automatically blocks any synced player whose per-player load exceeds the thresholds below. This applies to direct pairs and syncshell members unless they are whitelisted.");

        using (ImRaii.Disabled(!autoPause))
        {
            using var indent = ImRaii.PushIndent();
            var notifyDirectPairs = _playerPerformanceConfigService.Current.NotifyAutoPauseDirectPairs;
            var notifyGroupPairs = _playerPerformanceConfigService.Current.NotifyAutoPauseGroupPairs;
            if (ImGui.Checkbox("Display auto-block warnings for individual pairs", ref notifyDirectPairs))
            {
                _playerPerformanceConfigService.Update(c => c.NotifyAutoPauseDirectPairs = notifyDirectPairs);
            }
            if (ImGui.Checkbox("Display auto-block warnings for syncshell pairs", ref notifyGroupPairs))
            {
                _playerPerformanceConfigService.Update(c => c.NotifyAutoPauseGroupPairs = notifyGroupPairs);
            }

            var vramAuto = _playerPerformanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB;
            var trisAuto = _playerPerformanceConfigService.Current.TrisAutoPauseThresholdThousands;
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (InputGiBThreshold("Auto Block VRAM threshold", ref vramAuto))
            {
                _playerPerformanceConfigService.Update(c => c.VRAMSizeAutoPauseThresholdMiB = vramAuto);
                _perfUnapplied = true;
            }
            ImGui.SameLine();
            ImGui.Text("(GiB)");
            ElezenImgui.DrawHelpText("When a player's individual VRAM load exceeds this amount, Snowcloak automatically blocks them." + ElezenImgui.TooltipSeparator
                + $"Suggested default: {FormatMiBAsGiB(500)} per player.");

            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Auto Block Triangle threshold", ref trisAuto))
            {
                _playerPerformanceConfigService.Update(c => c.TrisAutoPauseThresholdThousands = trisAuto);
                _perfUnapplied = true;
            }
            ImGui.SameLine();
            ImGui.Text("(thousand triangles)");
            ElezenImgui.DrawHelpText("When a player's individual triangle count exceeds this amount, Snowcloak automatically blocks them." + ElezenImgui.TooltipSeparator
                + "Suggested default: 0.4 million per player.");

            using (ImRaii.Disabled(!_perfUnapplied))
            {
                if (ImGui.Button("Apply Changes Now"))
                {
                    recalculatePerformance = true;
                    _perfUnapplied = false;
                }
            }
        }
    }

    private void DrawModNullificationSettings()
    {
        _fontService.BigText("Mod Nullification [EXPERIMENTAL]");
        ElezenImgui.WrappedText("These options allow you to selectively nullify certain annoying aspects without outright pausing a user.");

        var settingsChanged = false;
        var config = _playerPerformanceConfigService.Current;

        var nullifyVfx = config.NullifyVfx;
        if (ImGui.Checkbox("Nullify custom VFX replacements", ref nullifyVfx))
        {
            config.NullifyVfx = nullifyVfx;
            settingsChanged = true;
        }

        var nullifySfx = config.NullifySfx;
        if (ImGui.Checkbox("Nullify custom SFX replacements", ref nullifySfx))
        {
            config.NullifySfx = nullifySfx;
            settingsChanged = true;
        }

        var nullifyAllHeightMods = config.NullifyAllHeightMods;
        if (ImGui.Checkbox("Nullify all height mods", ref nullifyAllHeightMods))
        {
            config.NullifyAllHeightMods = nullifyAllHeightMods;
            settingsChanged = true;
        }
        ElezenImgui.DrawHelpText("Disables ALL height mods. Options below are disabled when this is enabled.");

        using (ImRaii.Disabled(nullifyAllHeightMods))
        {
            var nullifyHeightAboveNormalMaxPercent = config.NullifyHeightAboveNormalMaxPercent;
            if (ImGui.Checkbox("Nullify height above a percentage of the vanilla maximum", ref nullifyHeightAboveNormalMaxPercent))
            {
                config.NullifyHeightAboveNormalMaxPercent = nullifyHeightAboveNormalMaxPercent;
                settingsChanged = true;
            }

            using (ImRaii.Disabled(!nullifyHeightAboveNormalMaxPercent))
            {
                using var indent = ImRaii.PushIndent();
                var normalMaxPercent = config.HeightNormalMaxPercent;
                ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
                if (ImGui.SliderFloat("Vanilla maximum threshold", ref normalMaxPercent, 100f, 500f, "%.0f%%"))
                {
                    config.HeightNormalMaxPercent = normalMaxPercent;
                    settingsChanged = true;
                }
            }

            var nullifyHeightAboveEstimatedCentimeters = config.NullifyHeightAboveEstimatedCentimeters;
            if (ImGui.Checkbox("Nullify height above an estimated physical height", ref nullifyHeightAboveEstimatedCentimeters))
            {
                config.NullifyHeightAboveEstimatedCentimeters = nullifyHeightAboveEstimatedCentimeters;
                settingsChanged = true;
            }

            using (ImRaii.Disabled(!nullifyHeightAboveEstimatedCentimeters))
            {
                using var indent = ImRaii.PushIndent();
                var estimatedCentimeters = config.HeightEstimatedCentimeters;
                ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputFloat("Estimated physical height threshold", ref estimatedCentimeters, 1f, 10f, "%.0f"))
                {
                    config.HeightEstimatedCentimeters = Math.Max(0f, estimatedCentimeters);
                    settingsChanged = true;
                }
                ImGui.SameLine();
                ImGui.Text("(cm)");
                ElezenImgui.DrawHelpText("Estimated from standard racial proportions."
                    + Environment.NewLine + "This does not measure Customize+ scaling, heels, or replacement model dimensions.");
            }
        }

        var showModNullificationMoodles = config.ShowModNullificationMoodles;
        if (ImGui.Checkbox("Show local Moodle markers for applied nullifications", ref showModNullificationMoodles))
        {
            config.ShowModNullificationMoodles = showModNullificationMoodles;
            settingsChanged = true;
        }

        if (settingsChanged)
        {
            _playerPerformanceConfigService.Update(_ => { });
            _mediator.Publish(new RecalculatePerformanceMessage(null));
        }
    }

    private void DrawPerformanceStatistics()
    {
        var snapshot = _usageStatisticsService.GetSnapshot();
        var visiblePairs = _pairManager.GetOnlineUserPairs().Where(pair => pair.IsVisible).ToList();
        var currentVisibleVram = visiblePairs.Sum(GetDisplayVramBytes);
        var currentVisibleTriangles = visiblePairs.Sum(GetDisplayTriangleCount);
        var cacheEntries = _fileCacheManager.GetAllFileCaches().Where(entry => entry.IsCacheEntry).ToList();
        var cacheRawBytes = cacheEntries.Sum(entry => PositiveMetric(entry.Size));
        var cacheCompressedBytes = cacheEntries.Sum(entry => PositiveMetric(entry.CompressedSize));
        var compressionSavings = Math.Max(0, cacheRawBytes - cacheCompressedBytes);

        _fontService.BigText("Usage Statistics");
        DrawUsageStatisticsTable(snapshot);

        ImGui.Spacing();
        _fontService.BigText("Current Load");
        if (ImGui.BeginTable("CurrentPerformanceStatistics", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Metric", ImGuiTableColumnFlags.WidthStretch, 0.45f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.55f);
            ImGui.TableHeadersRow();

            DrawStatisticRow("Visible synced players", visiblePairs.Count.ToString(CultureInfo.InvariantCulture));
            DrawStatisticRow("Visible VRAM", FormatGiB(currentVisibleVram));
            DrawStatisticRow("Visible triangles", SyncshellBudgetService.FormatTriangles(currentVisibleTriangles));
            DrawStatisticRow("Cached files", cacheEntries.Count.ToString(CultureInfo.InvariantCulture));
            DrawStatisticRow("Cache disk usage", FormatGiB(cacheRawBytes));
            DrawStatisticRow("Stored compressed size", FormatGiB(cacheCompressedBytes));
            DrawStatisticRow("Compression saving", FormatGiB(compressionSavings));

            ImGui.EndTable();
        }
    }

    private static void DrawUsageStatisticsTable(UsageStatisticsSnapshot snapshot)
    {
        if (!ImGui.BeginTable("UsageStatistics", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        ImGui.TableSetupColumn("Metric", ImGuiTableColumnFlags.WidthStretch, 0.34f);
        ImGui.TableSetupColumn("Lifetime", ImGuiTableColumnFlags.WidthStretch, 0.33f);
        ImGui.TableSetupColumn("Session", ImGuiTableColumnFlags.WidthStretch, 0.33f);
        ImGui.TableHeadersRow();

        DrawStatisticComparisonRow("Downloaded", FormatGiB(snapshot.Lifetime.DownloadedBytes), FormatGiB(snapshot.Session.DownloadedBytes));
        DrawStatisticComparisonRow("Uploaded", FormatGiB(snapshot.Lifetime.UploadedBytes), FormatGiB(snapshot.Session.UploadedBytes));
        DrawStatisticComparisonRow("Texture data viewed", FormatGiB(snapshot.Lifetime.AppliedDataBytes), FormatGiB(snapshot.Session.AppliedDataBytes));
        DrawStatisticComparisonRow("Total VRAM consumed", FormatGiB(snapshot.Lifetime.ViewedVramBytes), FormatGiB(snapshot.Session.ViewedVramBytes));
        DrawStatisticComparisonRow("Triangles viewed", SyncshellBudgetService.FormatTriangles(snapshot.Lifetime.ViewedTriangles), SyncshellBudgetService.FormatTriangles(snapshot.Session.ViewedTriangles));

        ImGui.EndTable();
    }

    private static void DrawStatisticComparisonRow(string label, string lifetime, string session)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(lifetime);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(session);
    }

    private static void DrawStatisticRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private static long PositiveMetric(long? value)
    {
        return value is > 0 ? value.Value : 0;
    }

    private static bool InputGiBThreshold(string label, ref int thresholdMiB)
    {
        var thresholdGiB = thresholdMiB / MiBPerGiB;
        if (!ImGui.InputFloat(label, ref thresholdGiB, 0.01f, 0.10f, "%.2f"))
        {
            return false;
        }

        thresholdMiB = Math.Max(0, (int)Math.Round(thresholdGiB * MiBPerGiB));
        return true;
    }

    private static string FormatGiB(long bytes)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:0.00} GiB", bytes / 1024d / 1024d / 1024d);
    }

    private static string FormatMiBAsGiB(int mib)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:0.00} GiB", mib / MiBPerGiB);
    }

    private static long GetDisplayVramBytes(Pair pair)
    {
        return pair.LastAppliedApproximateVRAMBytes >= 0
            ? pair.LastAppliedApproximateVRAMBytes
            : pair.LastReportedApproximateVRAMBytes ?? 0;
    }

    private static long GetDisplayTriangleCount(Pair pair)
    {
        return pair.LastAppliedDataTris >= 0
            ? pair.LastAppliedDataTris
            : pair.LastReportedTriangles ?? 0;
    }

    private void DrawPerformanceLists(ref bool recalculatePerformance, ref string? recalculatePerformanceUID)
    {
        DrawWhitelist(ref recalculatePerformance, ref recalculatePerformanceUID);
        ImGui.Separator();
        DrawBlacklist(ref recalculatePerformance, ref recalculatePerformanceUID);
    }

    private void DrawWhitelist(ref bool recalculatePerformance, ref string? recalculatePerformanceUID)
    {
        _fontService.BigText("Whitelisted UIDs");
        var ignoreDirectPairs = _playerPerformanceConfigService.Current.IgnoreDirectPairs;
        if (ImGui.Checkbox("Whitelist all individual pairs", ref ignoreDirectPairs))
        {
            _playerPerformanceConfigService.Update(c => c.IgnoreDirectPairs = ignoreDirectPairs);
            recalculatePerformance = true;
        }
        ElezenImgui.DrawHelpText("Individual pairs will never be affected by auto blocks.");
        ImGui.Dummy(new Vector2(5));
        ElezenImgui.WrappedText("The entries in the list below will not have auto block thresholds or mod nullification enforced.");

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var whitelistPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##whitelistuid", ref _uidToAddForIgnore, 25);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnore)))
        {
            ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add UID/Vanity ID to whitelist"))
            {
                if (!_blockListStore.IsUidWhitelisted(_uidToAddForIgnore))
                {
                    _blockListStore.AddWhitelistUid(_uidToAddForIgnore);
                    recalculatePerformance = true;
                    recalculatePerformanceUID = _uidToAddForIgnore;
                }
                _uidToAddForIgnore = string.Empty;
            }
        }
        ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
        ElezenImgui.DrawHelpText("Hint: UIDs are case sensitive.\nVanity IDs are also acceptable.");
        ImGui.Dummy(new Vector2(10));

        var playerList = _blockListStore.Whitelist;
        if (_selectedEntry > playerList.Count - 1)
        {
            _selectedEntry = -1;
        }

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.SetCursorPosY(whitelistPos.Y);
        using (var listBox = ImRaii.ListBox("##whitelist"))
        {
            if (listBox)
            {
                for (var index = 0; index < playerList.Count; index++)
                {
                    var shouldBeSelected = _selectedEntry == index;
                    if (ImGui.Selectable(playerList[index] + "##" + index, shouldBeSelected))
                    {
                        _selectedEntry = index;
                    }

                    var lastSeenName = _notesStore.GetNameForUid(playerList[index]);
                    if (lastSeenName != null)
                    {
                        ImGui.SameLine();
                        ElezenImgui.ShowIcon(FontAwesomeIcon.InfoCircle);
                        ElezenImgui.AttachTooltip(string.Format(CultureInfo.InvariantCulture, "Last seen name: {0}", lastSeenName));
                    }
                }
            }
        }

        using (ImRaii.Disabled(_selectedEntry == -1))
        {
            using var pushId = ImRaii.PushId("deleteSelectedWhitelist");
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete selected UID"))
            {
                _blockListStore.RemoveWhitelistUid(_blockListStore.Whitelist[_selectedEntry]);
                if (_selectedEntry > playerList.Count - 1)
                {
                    --_selectedEntry;
                }
                _playerPerformanceConfigService.Update(_ => { });
                recalculatePerformance = true;
            }
        }
    }

    private void DrawBlacklist(ref bool recalculatePerformance, ref string? recalculatePerformanceUID)
    {
        _fontService.BigText("Blacklisted UIDs");
        ElezenImgui.WrappedText("The entries in the list below will never have their characters displayed.");

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var blacklistPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##uid", ref _uidToAddForIgnoreBlacklist, 25);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnoreBlacklist)))
        {
            ImGui.SetCursorPosX(240 * ImGuiHelpers.GlobalScale);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add UID/Vanity ID to blacklist"))
            {
                if (!_blockListStore.IsUidBlacklisted(_uidToAddForIgnoreBlacklist))
                {
                    _blockListStore.AddBlacklistUid(_uidToAddForIgnoreBlacklist);
                    recalculatePerformance = true;
                    recalculatePerformanceUID = _uidToAddForIgnoreBlacklist;
                }
                _uidToAddForIgnoreBlacklist = string.Empty;
            }
        }
        ElezenImgui.DrawHelpText("Hint: UIDs are case sensitive.\nVanity IDs are also acceptable.");
        ImGui.Dummy(new Vector2(10));

        var blacklist = _blockListStore.Blacklist;
        if (_selectedEntryBlacklist > blacklist.Count - 1)
        {
            _selectedEntryBlacklist = -1;
        }

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.SetCursorPosY(blacklistPos.Y);
        using (var listBox = ImRaii.ListBox("##blacklist"))
        {
            if (listBox)
            {
                for (var index = 0; index < blacklist.Count; index++)
                {
                    var shouldBeSelected = _selectedEntryBlacklist == index;
                    if (ImGui.Selectable(blacklist[index] + "##BL" + index, shouldBeSelected))
                    {
                        _selectedEntryBlacklist = index;
                    }

                    var lastSeenName = _notesStore.GetNameForUid(blacklist[index]);
                    if (lastSeenName != null)
                    {
                        ImGui.SameLine();
                        ElezenImgui.ShowIcon(FontAwesomeIcon.InfoCircle);
                        ElezenImgui.AttachTooltip(string.Format(CultureInfo.InvariantCulture, "Last seen name: {0}", lastSeenName));
                    }
                }
            }
        }

        using (ImRaii.Disabled(_selectedEntryBlacklist == -1))
        {
            using var pushId = ImRaii.PushId("deleteSelectedBlacklist");
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete selected UID"))
            {
                _blockListStore.RemoveBlacklistUid(_blockListStore.Blacklist[_selectedEntryBlacklist]);
                if (_selectedEntryBlacklist > blacklist.Count - 1)
                {
                    --_selectedEntryBlacklist;
                }
                _playerPerformanceConfigService.Update(_ => { });
                recalculatePerformance = true;
            }
        }
    }
}
