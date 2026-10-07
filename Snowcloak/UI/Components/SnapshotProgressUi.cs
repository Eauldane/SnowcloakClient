using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Core.EnvironmentSnapshots;

namespace Snowcloak.UI.Components;

internal static class SnapshotProgressUi
{
    public static string Label(SnapshotPhase phase) => phase switch
    {
        SnapshotPhase.Ready => "Ready", SnapshotPhase.Enumerating => "Inventory", SnapshotPhase.Capturing => "Capture",
        SnapshotPhase.Submitting => "Metadata", SnapshotPhase.Checking => "Server check", SnapshotPhase.Staging => "Staging",
        SnapshotPhase.Compressing => "Compressing", SnapshotPhase.Uploading => "Uploading", SnapshotPhase.Verifying => "Verifying",
        SnapshotPhase.Finalizing => "Finalizing", SnapshotPhase.Complete => "Complete", SnapshotPhase.Failed => "Failed",
        SnapshotPhase.Cancelled => "Cancelled", SnapshotPhase.Preview => "Review", SnapshotPhase.Downloading => "Downloading",
        SnapshotPhase.Unload => "Disabling plugins", SnapshotPhase.Installing => "Restoring files", SnapshotPhase.Reload => "Re-enabling plugins",
        SnapshotPhase.Reconciling => "Reconciliation", SnapshotPhase.RolledBack => "Rolled back", _ => "Ready"
    };
    public static void Text(string value, Vector4? colour = null) => ElezenImgui.ColouredText(value, colour ?? ModernTheme.Palette.CompactTextMuted);
    public static void Path(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        ImGui.TextUnformatted(Fit(value, ImGui.GetContentRegionAvail().X));
        ElezenImgui.AttachTooltip(value);
    }
    public static string Fit(string value, float width)
    {
        if (ImGui.CalcTextSize(value).X <= width) return value;
        int low = 0, high = value.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(value[..mid] + "…").X <= width) low = mid; else high = mid - 1;
        }
        return value[..low] + "…";
    }
    public static void Bar(float ratio, string label, bool known = true, float height = 17, string heading = "Progress")
    {
        using var colour = ImRaii.PushColor(ImGuiCol.PlotHistogram, SnowcloakColours.OnlineBlue);
        var size = new Vector2(-1, height * ImGuiHelpers.GlobalScale);
        if (known) ElezenImgui.DrawProgressBarOption(heading, ratio, size, barText: label);
        else
        {
            ImGui.ProgressBar(-(float)ImGui.GetTime() * 0.5f, size, label);
            ElezenImgui.AttachTooltip(heading);
        }
    }
    public static void Steps(SnapshotPhase phase, bool restore = false)
    {
        string[] steps = restore ? ["Review", "Download", "Restore", "Reload", "Verify"] : ["Capture", "Check", "Upload", "Verify"];
        int active = restore ? phase switch
        {
            SnapshotPhase.Downloading or SnapshotPhase.Staging => 1, SnapshotPhase.Unload or SnapshotPhase.Installing => 2,
            SnapshotPhase.Reload => 3, SnapshotPhase.Verifying or SnapshotPhase.Reconciling => 4,
            SnapshotPhase.Complete or SnapshotPhase.RolledBack => 5, _ => 0
        } : phase switch
        {
            SnapshotPhase.Submitting or SnapshotPhase.Checking => 1, SnapshotPhase.Staging or SnapshotPhase.Compressing or SnapshotPhase.Uploading or SnapshotPhase.Verifying => 2,
            SnapshotPhase.Finalizing => 3, SnapshotPhase.Complete => 4, _ => 0
        };
        for (int i = 0; i < steps.Length; i++)
        {
            if (i > 0) { ImGui.SameLine(); Text("›"); ImGui.SameLine(); }
            Text(steps[i], i <= active ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactTextMuted);
        }
    }
    public static void Draw(SnapshotProgressView progress, bool paused, bool waiting, bool restore = false)
    {
        var label = paused ? "Paused" : waiting ? "Waiting for sync" : Label(progress.Phase);
        ElezenImgui.ColouredText(label, progress.Phase == SnapshotPhase.Failed ? SnowcloakColours.BooleanFalse : ModernTheme.Palette.Accent);
        ImGui.SameLine(); Text(progress.Attempt > 1 ? $"Attempt {progress.Attempt}/3" : "");
        if (progress.Phase == SnapshotPhase.Enumerating) Text($"{progress.Done:N0} files discovered");
        if (!restore && progress.Objects > 0 && progress.Phase is not (SnapshotPhase.Submitting or SnapshotPhase.Checking or SnapshotPhase.Compressing))
            Bar(progress.ObjectFraction, $"{progress.ObjectsDone:N0} / {progress.Objects:N0} unique files verified", heading: "Files");
        else
            Bar(progress.Phase == SnapshotPhase.Complete ? 1 : progress.PhaseFraction, progress.Total > 0 ? $"{progress.Done:N0} / {progress.Total:N0}" : label,
                known: progress.Total > 0 || progress.Phase is SnapshotPhase.Complete or SnapshotPhase.Cancelled or SnapshotPhase.Failed or SnapshotPhase.Unload or SnapshotPhase.Reload);
        if (progress.CurrentTotal > 0)
        {
            string bytes = $"{ElezenImgui.ByteToString(progress.CurrentSent)} / {ElezenImgui.ByteToString(progress.CurrentTotal)}";
            Bar(progress.CurrentFraction, bytes, height: 14, heading: restore ? "Download" : "Upload");
        }
        if (!restore && progress.Files > 0) Text($"{progress.Files:N0} files · {ElezenImgui.ByteToString(progress.ContentBytes)} content");
        if (restore) Text($"Received {ElezenImgui.ByteToString(progress.SentBytes)}");
        else
        {
            Text($"Reused {ElezenImgui.ByteToString(progress.ReusedBytes)}");
            ImGui.SameLine(); Text($"  Sent {ElezenImgui.ByteToString(progress.SentBytes)}");
            if (progress.Uploads > 0) Text($"{progress.UploadedObjects:N0}/{progress.Uploads:N0} uploads");
        }
        if (!paused && !waiting && progress.BytesPerSecond > 0 && progress.Phase is SnapshotPhase.Uploading or SnapshotPhase.Downloading) { ImGui.SameLine(); Text($"  {ElezenImgui.ByteToString((long)progress.BytesPerSecond)}/s"); }
        Path(progress.CurrentPath);
    }
}
