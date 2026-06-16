using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.Core.Async;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Snowcloak.Core.Analysis;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services;
using System.Globalization;

namespace Snowcloak.UI.Components;

internal sealed class TextureOptimizerFlow : IDisposable
{
    // BC7 is 1 byte/pixel vs. 4 bytes/pixel for the uncompressed source most modded textures
    // ship as; no mip/dimension data is available on AnalysisFileEntry for an exact figure.
    private const double EstimatedBc7CompressionRatio = 0.25d;

    private readonly ILogger _logger;
    private readonly IpcManager _ipcManager;
    private readonly Dictionary<string, (TextureType TextureType, string[] Duplicates)> _plan = new(StringComparer.Ordinal);
    private readonly SingleFlightCts _cts = new();

    private bool _enabled;
    private int _conversionTotal;

    public TextureOptimizerFlow(ILogger logger, IpcManager ipcManager)
    {
        _logger = logger;
        _ipcManager = ipcManager;
    }

    public AsyncOp Conversion { get; } = new();
    public ValueProgress<(string FileName, int Index)>? Progress { get; private set; }

    public void ResetPlan()
    {
        _enabled = false;
        _plan.Clear();
    }

    public AnalysisBrowserColumn BuildColumn() => new(
        "BC7",
        50f * ImGuiHelpers.GlobalScale,
        ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort,
        sortSelector: null,
        drawCell: DrawCell);

    public void DrawOptionsPanel(IAnalysisSource source)
    {
        if (Conversion.IsRunning)
        {
            DrawProgress();
            return;
        }

        if (Conversion.IsCompleted)
        {
            // Reset() puts the op back to idle so this branch only fires once; AsyncOp has no
            // separate "consumed" flag, IsCompleted would otherwise stay true on every frame.
            _ = source.ComputeAnalysis(print: false, recalculate: true);
            ResetPlan();
            Conversion.Reset();
            Progress = null;
        }

        ImGui.Checkbox("Enable BC7 compression mode", ref _enabled);

        if (!_enabled)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
                ImGui.TextWrapped("Enable BC7 compression mode to pick textures for conversion. A BC7 column appears in the table below for eligible textures.");
            return;
        }

        ImGui.SameLine();
        ElezenImgui.ColouredText("Converting textures is irreversible!", ImGuiColors.DalamudRed);
        ElezenImgui.ColouredText("WARNING REGARDING TEXTURE COMPRESSION:", ImGuiColors.DalamudYellow);
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
            ImGui.TextWrapped("Converting to BC7 compression can reduce file size and VRAM use, especially for large textures."
                + Environment.NewLine + "• Compression can cause some visual quality loss (colorsets, normal/greyscale maps especially)."
                + Environment.NewLine + "• Not all textures benefit equally. Keep originals so you can reimport."
                + Environment.NewLine + "• Texture compression is expensive and can take a while.");

        if (_plan.Count == 0)
            return;

        if (AnalysisBrowser.DrawAccentButton(FontAwesomeIcon.PlayCircle,
            string.Format(CultureInfo.InvariantCulture, "Start compression of {0} texture(s)", _plan.Count), "start-compression"))
        {
            StartConversion();
        }
    }

    private void DrawCell(AnalysisFileEntry item)
    {
        if (!string.Equals(item.FileType, "tex", StringComparison.Ordinal) || IsAlreadyBlockCompressed(item))
            return;

        var filePath = item.FilePaths[0];
        var toConvert = _plan.ContainsKey(filePath);
        if (ImGui.Checkbox("###convert" + item.Hash, ref toConvert))
        {
            if (toConvert)
                _plan[filePath] = (TextureType.Bc7Tex, item.FilePaths.Skip(1).ToArray());
            else
                _plan.Remove(filePath);
        }

        var risky = item.IsRiskyTexture;
        if (toConvert && !risky)
        {
            ImGui.SameLine();
            var estimated = (long)(item.OriginalSize * EstimatedBc7CompressionRatio);
            using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
                ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "~{0}", ElezenImgui.ByteToString(estimated)));
            ElezenImgui.AttachTooltip(string.Format(CultureInfo.InvariantCulture, "Estimated size after BC7 conversion: {0} -> ~{1}",
                ElezenImgui.ByteToString(item.OriginalSize), ElezenImgui.ByteToString(estimated)));
        }

        if (risky)
        {
            ImGui.SameLine();
            ElezenImgui.ShowIcon(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudOrange);
            ElezenImgui.AttachTooltip("Texture flagged as risky for BC7 compression. Proceed with caution.");
            if (!toConvert)
                _plan.Remove(filePath);
        }
    }

    private void DrawProgress()
    {
        var current = Progress?.Value ?? (string.Empty, 0);
        ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "Texture compression in progress: {0}/{1}", current.Index, _conversionTotal));
        ImGui.TextWrapped(string.Format(CultureInfo.InvariantCulture, "Current file: {0}", current.FileName));
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.StopCircle, "Cancel compression"))
            _cts.Cancel();
    }

    private void StartConversion()
    {
        var progress = new ValueProgress<(string FileName, int Index)>();
        Progress = progress;
        var plan = new Dictionary<string, (TextureType TextureType, string[] Duplicates)>(_plan, StringComparer.Ordinal);
        _conversionTotal = plan.Count;

        _ = Conversion.Run(async () =>
        {
            using var scope = _cts.Begin();
            await _ipcManager.Penumbra.ConvertTextureFiles(_logger, plan, progress, scope.Token).ConfigureAwait(false);
        });
    }

    private static bool IsAlreadyBlockCompressed(AnalysisFileEntry item)
    {
        var format = item.FormatSummary;
        return format.StartsWith("BC", StringComparison.OrdinalIgnoreCase)
            || format.StartsWith("DXT", StringComparison.OrdinalIgnoreCase)
            || format.StartsWith("24864", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
