using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Backups;
using Snowcloak.Core.EnvironmentSnapshots;
using Snowcloak.EnvironmentSnapshots;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;

namespace Snowcloak.UI;

public sealed class EnvironmentBackupUi : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly SnapshotService _service;
    private readonly SnapshotRestoreService _restore;
    private string[] _mods = [], _plugins = [];
    private Task<string[]>? _review;
    private Task? _action;
    private bool _reviewStarted, _automatic, _compact, _openRestoreTab, _historyLoaded, _discardRequested;
    private string? _reviewError, _actionError;
    private static readonly string[] Tabs = ["Backup", "Restore"];
    private string _activeTab = "Backup";
    private bool _showPreview, _restoreOptions;
    private string _reviewedRoot = "";
    private Guid? _delete;
    private bool ActionBusy => _action?.IsCompleted == false || _restore.Busy || _restore.Previewing;

    public EnvironmentBackupUi(ILogger<EnvironmentBackupUi> logger, SnowMediator mediator, PerformanceCollectorService performance, SnapshotService service, SnapshotRestoreService restore)
        : base(logger, mediator, "Snowcloak backups###EnvironmentBackups", performance)
    {
        _service = service; _restore = restore; IsOpen = false;
        if (_restore.NeedsRecovery) _activeTab = "Restore";
        Flags = ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus;
        Size = new Vector2(620, 480); SizeCondition = ImGuiCond.FirstUseEver;
        SetScaledSizeConstraints(new Vector2(430, 260), new Vector2(1400, 1200));
    }
    private void BeginReview()
    {
        _reviewStarted = true; _reviewError = null; _mods = []; _plugins = [];
        _review = _service.InstalledModsAsync();
    }
    private void Run(Func<Task> operation)
    {
        if (ActionBusy) return;
        _actionError = null;
        _action = RunActionAsync(operation);
    }
    private async Task RunActionAsync(Func<Task> operation)
    { try { await operation().ConfigureAwait(false); } catch (Exception ex) { _actionError = ex.Message; } }
    private static string PluginName(string value) => value == "CustomizePlus" ? "Customize+" : value;
    private async Task ReviewRestoreAsync(Guid id)
    {
        _showPreview = true;
        await _restore.PreviewAsync(id).ConfigureAwait(false);
        _reviewedRoot = _restore.ModRoot;
    }
    private async Task FinishRestoreAsync()
    {
        await _restore.FinishAsync().ConfigureAwait(false);
        if (!_restore.AwaitingReload) _showPreview = false;
    }
    private async Task RollbackRestoreAsync()
    {
        await _restore.RollbackAsync().ConfigureAwait(false);
        if (!_restore.NeedsRecovery) _showPreview = false;
    }
    private static void Error(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        ElezenImgui.ColouredWrappedText(message, SnowcloakColours.BooleanFalse);
    }
    protected override void DrawInternal()
    {
        float scale = ImGuiHelpers.GlobalScale;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(9, 8) * scale);
        if (!_reviewStarted) BeginReview();
        if (_review?.IsCompleted == true)
        {
            if (_review.IsCompletedSuccessfully) { _mods = _review.Result; _plugins = _service.InstalledPlugins(); }
            else _reviewError = _review.Exception?.GetBaseException().Message ?? "Inventory cancelled";
            _review = null;
        }
        bool restoreActive = _restore.Busy || _restore.Previewing || _restore.AwaitingUnload || _restore.AwaitingReload;
        ModernSection.Header(restoreActive ? FontAwesomeIcon.Download : FontAwesomeIcon.Archive, restoreActive ? "Restoring backup" : _service.Busy ? "Backing up" : "Backups");
        var viewIcon = _compact ? FontAwesomeIcon.Expand : FontAwesomeIcon.Compress;
        var viewLabel = _compact ? "Expand" : "Compact";
        float viewWidth = ElezenImgui.GetIconButtonTextSize(viewIcon, viewLabel);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - viewWidth));
        if (ElezenImgui.ShowIconButton(viewIcon, viewLabel))
        {
            _compact = !_compact;
            if (!_compact && restoreActive) _openRestoreTab = true;
            ImGui.SetWindowSize((_compact ? new Vector2(460, 390) : new Vector2(620, 480)) * scale);
        }
        ModernSection.SoftSeparator();
        var progress = _compact && restoreActive ? _restore.Progress.Read() : _service.Progress.Read();
        if (_service.Busy || _compact)
        {
            SnapshotProgressUi.Steps(progress.Phase, _compact && restoreActive);
            using var progressSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(9, 4) * scale);
            using (var panel = ImRaii.Child("BackupProgress", new Vector2(-1, 240 * scale), true))
                if (panel) SnapshotProgressUi.Draw(progress, _service.Paused, _service.WaitingForSync, _compact && restoreActive);
            if (_service.Busy)
            {
                if (ElezenImgui.ShowIconButton(_service.Paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause, _service.Paused ? "Resume" : "Pause")) _service.TogglePause();
                ImGui.SameLine(); if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Times, "Cancel backup")) ImGui.OpenPopup("CancelBackup");
            }
            if (_compact && _restore.Busy && ElezenImgui.ShowIconButton(FontAwesomeIcon.Times, "Cancel restore")) _restore.Cancel();
            Error(_compact && restoreActive ? _restore.Error : _service.Error);
        }
        if (_compact)
        {
            if (!_service.Busy) { if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Cog, restoreActive ? "Open restore" : "Open backups")) { _compact = false; _openRestoreTab = restoreActive; ImGui.SetWindowSize(new Vector2(620, 480) * scale); } }
            DrawDialogs(); return;
        }
        Error(_actionError);
        if (_openRestoreTab) { _activeTab = "Restore"; _openRestoreTab = false; }
        _activeTab = ModernTabBar.Draw("BackupTabs", Tabs, _activeTab);
        ImGuiHelpers.ScaledDummy(6f);
        using (ImRaii.PushId("BackupTab:" + _activeTab))
        {
            switch (_activeTab)
            {
                case "Backup": DrawSetup(); break;
                case "Restore": DrawRestore(); break;
            }
        }
        DrawDialogs();
    }
    private void DrawSetup()
    {
        using var disabled = ImRaii.Disabled(_service.Busy || ActionBusy || SnapshotExclusion.Blocked);
        if (!_service.Busy && _service.Progress.Read().Phase != SnapshotPhase.Ready)
        {
            var result = _service.Progress.Read(); SnapshotProgressUi.Text(SnapshotProgressUi.Label(result.Phase), result.Phase == SnapshotPhase.Failed ? SnowcloakColours.BooleanFalse : SnowcloakColours.OnlineBlue);
            if (result.Files > 0) SnapshotProgressUi.Text($"{result.Files:N0} files · Reused {ElezenTools.UI.ElezenImgui.ByteToString(result.ReusedBytes)} · Sent {ElezenTools.UI.ElezenImgui.ByteToString(result.SentBytes)}");
            Error(_service.Error); ModernSection.SoftSeparator();
        }
        ModernSection.Header(FontAwesomeIcon.Archive, "Back up everything");
        SnapshotProgressUi.Text(_review != null ? "Checking installed mods..." : $"All {_mods.Length:N0} installed mods");
        if (_plugins.Length > 0) SnapshotProgressUi.Text(string.Join(" · ", _plugins.Select(PluginName)));
        ElezenImgui.AttachTooltip("Includes all installed mods (including unselected options), as well as your configs for them.");
        SnapshotProgressUi.Path(_service.ModRoot ?? "Can't read Penumbra folder!");
        ModernSection.SoftSeparator();
        if (_service.Current.Snapshot.HasValue) _automatic = _service.Current.Enabled;
        if (ImGui.Checkbox("Back up automatically each day", ref _automatic) && _service.Current.Snapshot.HasValue) _service.SetAutomatic(_automatic);
        ElezenImgui.AttachTooltip("When enabled, Snowcloak will check for newly installed mods or config changes every 24 hours and back them up for you.");
        if (_service.Current.Snapshot.HasValue)
        {
            SnapshotProgressUi.Text(_service.Current.Verified is { } verified ? $"Last checked  {verified.ToLocalTime():dd MMM HH:mm}" : "Not checked yet");
        }
        Error(_reviewError);
        string? reason = _service.UnavailableReason;
        if (reason != null) SnapshotProgressUi.Text(reason, SnowcloakColours.BooleanFalse);
        bool hasContents = _mods.Length > 0 || _plugins.Length > 0;
        using (ImRaii.Disabled(_review != null || _reviewError != null || !hasContents || reason != null))
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Upload, "Back up now")) { _historyLoaded = false; _ = _service.BackupAsync(_plugins, _automatic); }
        ImGui.SameLine();
        using (ImRaii.Disabled(_review != null)) if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Sync, "Refresh")) BeginReview();
        if (SnapshotExclusion.Blocked) SnapshotProgressUi.Text("Finish or roll back the restore first");
    }
    private void DrawHistory()
    {
        ModernSection.Header(FontAwesomeIcon.History, "Saved backups");
        if (!_historyLoaded && !ActionBusy && !_service.Busy) { _historyLoaded = true; Run(_service.RefreshAsync); }
        using (ImRaii.Disabled(ActionBusy || _service.Busy)) if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Sync, "Refresh")) Run(_service.RefreshAsync);
        Error(_service.Error);
        if (_service.Snapshots.Count == 0) { SnapshotProgressUi.Text(ActionBusy ? "Loading backups..." : "No saved backups"); return; }
        using var child = ImRaii.Child("SavedBackups", new Vector2(-1, -1), false);
        if (!child || !ImGui.BeginTable("SavedBackupList", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Backup");
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 175 * ImGuiHelpers.GlobalScale); ImGui.TableHeadersRow();
        foreach (var snapshot in _service.Snapshots.OrderByDescending(s => s.CreatedAtUtc))
        {
            using var id = ImRaii.PushId(snapshot.Id.ToString()); ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(snapshot.CreatedAtUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm", CultureInfo.CurrentCulture));
            ElezenImgui.AttachTooltip(snapshot.Label);
            if (!snapshot.Complete) SnapshotProgressUi.Text("Incomplete");
            ImGui.TableNextColumn();
            using var disabled = ImRaii.Disabled(ActionBusy || _service.Busy || SnapshotExclusion.Blocked);
            if (snapshot.Complete && ElezenImgui.ShowIconButton(FontAwesomeIcon.Download, "Restore")) Run(() => ReviewRestoreAsync(snapshot.Id));
            if (snapshot.Complete) ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete")) _delete = snapshot.Id;
        }
        ImGui.EndTable();
    }
    private void DrawRestore()
    {
        Error(_restore.Error);
        if (_restore.Busy || _restore.Previewing || ActionBusy && _restore.Progress.Read().Phase is SnapshotPhase.Verifying or SnapshotPhase.Reconciling)
        {
            SnapshotProgressUi.Draw(_restore.Progress.Read(), false, false, true);
            if (_restore.Busy && ElezenImgui.ShowIconButton(FontAwesomeIcon.Times, "Cancel restore")) _restore.Cancel();
            return;
        }
        if (_restore.AwaitingUnload || _restore.AwaitingReload)
        {
            if (_restore.AwaitingUnload)
            {
                using var disabled = ImRaii.Disabled(ActionBusy);
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Play, "Resume restore")) Run(_restore.InstallAsync);
            }
            else
            {
                ModernSection.Header(FontAwesomeIcon.Check, "Check your restored setup");
                if (_restore.RequiredPlugins.Any(p => _restore.PluginShouldBeLoaded(p) && !_restore.PluginLoaded(p)))
                {
                    DrawPluginStates();
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Sync, "Retry reloading plugins")) Run(_restore.ReloadPluginsAsync);
                }
                bool verified = _restore.ManualVerified;
                if (ImGui.Checkbox("Restored setup looks correct", ref verified)) _restore.ManualVerified = verified;
                using var disabled = ImRaii.Disabled(ActionBusy || !verified);
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Check, "Finish restore")) Run(FinishRestoreAsync);
            }
            ModernSection.SoftSeparator();
            using (ImRaii.Disabled(ActionBusy)) if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Undo, "Roll back")) Run(RollbackRestoreAsync);
            return;
        }
        if (_restore.NeedsRecovery) { DrawRecovery(); return; }
        if (_restore.Progress.Read().Phase is SnapshotPhase.Complete or SnapshotPhase.RolledBack)
        {
            SnapshotProgressUi.Text("Restore complete", SnowcloakColours.OnlineBlue);
        }
        if (_restore.HasRecoveryData)
        {
            using (ImRaii.Disabled(ActionBusy || _service.Busy))
                _discardRequested = true;
            ModernSection.SoftSeparator();
        }
        if (!_showPreview) { DrawHistory(); return; }
        using (ImRaii.Disabled(ActionBusy || _service.Busy))
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowLeft, "Saved backups")) { _showPreview = false; _restoreOptions = false; }
        ModernSection.Header(FontAwesomeIcon.Download, "Restore this backup");
        var snapshot = _service.Snapshots.FirstOrDefault(s => s.Id == _restore.Snapshot);
        if (snapshot != null) SnapshotProgressUi.Text(snapshot.CreatedAtUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm", CultureInfo.CurrentCulture));
        if (_restore.Choices.Count == 0) { SnapshotProgressUi.Text("Nothing to restore"); return; }
        var mods = _restore.Choices.Where(c => c.File is { } f && !f.Mod.StartsWith('@')).Select(c => c.File!.Mod).Distinct().Count();
        var plugins = _restore.Choices.Where(c => c.Document != null).Select(c => PluginName(c.Document!.Plugin)).Distinct().ToArray();
        if (mods > 0) SnapshotProgressUi.Text($"All {mods:N0} mods in this backup");
        if (plugins.Length > 0) SnapshotProgressUi.Text(string.Join(" · ", plugins));
        SnapshotProgressUi.Path(_restore.ModRoot);
        SnapshotProgressUi.Text("Replaces matching saved setup");
        using (ImRaii.Disabled(ActionBusy || _service.Busy))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FolderOpen, "Change destination")) _restoreOptions = !_restoreOptions;
            if (_restoreOptions)
            {
                var root = _restore.ModRoot;
                var roots = new[] { _service.ModRoot, _restore.ModRoot }.OfType<string>().Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);
                ElezenImgui.InputComboHybrid("##BackupRestoreModRoot", "##BackupRestoreModRootOptions", ref root,
                    roots, path => (path, null, path, null), ImGui.GetContentRegionAvail().X, 2048);
                _restore.ModRoot = root;
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Sync, "Check destination")) Run(() => ReviewRestoreAsync(_restore.Snapshot));
            }
            bool missing = _restore.Choices.Any(c => !c.Unchanged && c.Available == false);
            if (missing)
            {
                SnapshotProgressUi.Text("Some files are unavailable", SnowcloakColours.BooleanFalse);
                bool partial = _restore.AllowPartial;
                if (ImGui.Checkbox("Restore available files only", ref partial)) _restore.AllowPartial = partial;
            }
            bool destinationChanged = !string.Equals(_reviewedRoot, _restore.ModRoot, StringComparison.Ordinal);
            using (ImRaii.Disabled(destinationChanged || missing && !_restore.AllowPartial && !_restore.CanRepair || _restore.HasRecoveryData || _restore.Error != null && !_restore.RepairUnavailable))
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Download, _restore.RepairUnavailable ? "Retry restore" : "Restore everything")) Run(_restore.StageAsync);
        }
    }
    private void DrawPluginStates()
    {
        ModernSection.Header(FontAwesomeIcon.Cog, "Plugins");
        foreach (var plugin in _restore.RequiredPlugins)
        {
            bool loaded = _restore.PluginLoaded(plugin);
            ElezenImgui.GetBooleanIcon(_restore.AwaitingReload ? loaded == _restore.PluginShouldBeLoaded(plugin) : !loaded, inline: false); ImGui.SameLine();
            ImGui.TextUnformatted(PluginName(plugin)); ImGui.SameLine();
            SnapshotProgressUi.Text(loaded ? "Loaded" : "Unloaded", loaded ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactTextMuted);
        }
    }
    private void DrawRecovery()
    {
        ModernSection.Header(FontAwesomeIcon.Undo, "Interrupted restore");
        if (!_restore.HasRecoveryData) { SnapshotProgressUi.Text("No recovery data"); return; }
        SnapshotProgressUi.Text(_restore.NeedsRecovery ? "Restore incomplete - Sync suspended" : "Original files saved", _restore.NeedsRecovery ? SnowcloakColours.BooleanFalse : SnowcloakColours.OnlineBlue);

        using var disabled = ImRaii.Disabled(ActionBusy || _service.Busy);
        if (_restore.NeedsRecovery && !_restore.AwaitingUnload && !_restore.AwaitingReload && ElezenImgui.ShowIconButton(FontAwesomeIcon.Play, "Resume restore")) { _restore.ResumeInterruptedRestore(); _openRestoreTab = true; }
        if (_restore.NeedsRecovery || _restore.AwaitingUnload || _restore.AwaitingReload)
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Undo, "Roll back")) Run(RollbackRestoreAsync);
        }
        if (!_restore.NeedsRecovery && ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Discard recovery data")) _discardRequested = true;
    }
    private void DrawDialogs()
    {
        if (_discardRequested) { ImGui.OpenPopup("DiscardRecovery"); _discardRequested = false; }
        if (_delete.HasValue && !ImGui.IsPopupOpen("DeleteBackup")) ImGui.OpenPopup("DeleteBackup");
        if (ImGui.BeginPopupModal("DeleteBackup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Delete backup?");
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete")) { var id = _delete; _delete = null; ImGui.CloseCurrentPopup(); if (id.HasValue) Run(() => _service.DeleteAsync(id.Value)); }
            ImGui.SameLine(); if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Check, "Keep")) { _delete = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopupModal("CancelBackup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Cancel backup?");
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Times, "Cancel backup")) { _service.Cancel(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine(); if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Play, "Continue")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopupModal("DiscardRecovery", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Discard saved originals?");
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Discard")) { _restore.DiscardRecovery(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine(); if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Check, "Keep")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }
}
