using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Colors;
using Dalamud.Utility;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.ServerConfiguration;

namespace Snowcloak.UI.Components.Account;

public sealed partial class SecretKeyBackupFlow
{
    private readonly ILogger _logger;
    private readonly SecretKeyBackupService _backupService;
    private readonly FileDialogManager _fileDialogManager;
    private string? _message;
    private bool _success;
    private string _lastDirectory = string.Empty;
    private SecretKeyBackupImportResult? _pendingInitialSetupImport;
    private Action<SecretKeyBackupImportResult>? _pendingInitialSetupCallback;
    private int? _selectedKeyIndex;

    public SecretKeyBackupFlow(ILogger logger, SecretKeyBackupService backupService, FileDialogManager fileDialogManager)
    {
        _logger = logger;
        _backupService = backupService;
        _fileDialogManager = fileDialogManager;
    }

    public bool HasMessage => !_message.IsNullOrEmpty();

    public void DrawStatus()
    {
        ElezenImgui.ColouredWrappedText(
            "Snowcloak backup JSON files contain unencrypted login keys. Anyone with a copy can access those identities. Please store backups securely.",
            ImGuiColors.DalamudYellow);

        if (_message.IsNullOrEmpty())
            return;
        ElezenImgui.ColouredWrappedText(_message, _success ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
    }

    public void DrawInitialSetupAssignment()
    {
        if (_pendingInitialSetupImport == null || _pendingInitialSetupImport.KeyChoices.Count == 0)
            return;

        ImGui.TextUnformatted("Choose which imported key this character should use.");
        var selected = _pendingInitialSetupImport.KeyChoices.FirstOrDefault(choice => choice.KeyIndex == _selectedKeyIndex);
        if (ImGui.BeginCombo("Imported key", selected == null ? "Choose a key..." : GetChoiceLabel(selected)))
        {
            foreach (var choice in _pendingInitialSetupImport.KeyChoices)
            {
                bool isSelected = choice.KeyIndex == _selectedKeyIndex;
                if (ImGui.Selectable($"{GetChoiceLabel(choice)}##backup-key-{choice.KeyIndex}", isSelected))
                {
                    _selectedKeyIndex = choice.KeyIndex;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.BeginDisabled(!_selectedKeyIndex.HasValue);
        if (ImGui.Button("Assign key and connect"))
        {
            AssignSelectedKey();
        }
        ImGui.EndDisabled();
    }

    public void BeginExport(ServerStorage selectedServer)
    {
        string defaultFileName = string.Join('_', $"Snowcloak-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json".Split(Path.GetInvalidFileNameChars()));
        string? initialDirectory = Directory.Exists(_lastDirectory) ? _lastDirectory : null;

        _fileDialogManager.SaveFileDialog("Export backup", ".json", defaultFileName, ".json", (success, path) =>
        {
            if (!success) return;

            try
            {
                var backup = _backupService.Export(selectedServer, path);
                _lastDirectory = Path.GetDirectoryName(path) ?? string.Empty;
                SetStatus(
                    $"Snowcloak backup exported: {backup.SecretKeyCount} key(s), {backup.CharacterAssignmentCount} assignment(s), {backup.UserNoteCount} user note(s). It contains unencrypted login keys; protect the file as you would a password.",
                    success: true);
            }
            catch (Exception ex)
            {
                LogExportFailure(_logger, ex);
                SetStatus("Snowcloak backup export failed. Check plugin logs for details.", success: false);
            }
        }, initialDirectory);
    }

    public void BeginImportIntoServer(ServerStorage selectedServer)
    {
        string? initialDirectory = Directory.Exists(_lastDirectory) ? _lastDirectory : null;
        _fileDialogManager.OpenFileDialog("Restore backup", ".json", (success, paths) =>
        {
            if (!success) return;
            if (paths.FirstOrDefault() is not string path) return;

            try
            {
                var imported = _backupService.ImportIntoServer(path, selectedServer);
                _lastDirectory = Path.GetDirectoryName(path) ?? string.Empty;
                SetStatus(
                    $"Backup merged without replacing existing data: added {imported.AddedSecretKeyCount} key(s), {imported.AddedCharacterAssignmentCount} assignment(s), and {imported.AddedUserNoteCount} user note(s).",
                    success: true);
            }
            catch (Exception ex)
            {
                LogRestoreFailure(_logger, ex);
                SetStatus("Secret key backup restore failed. Ensure the file is a valid backup JSON.", success: false);
            }
        }, 1, initialDirectory);
    }
    
    public void BeginImportForInitialSetup(Action<SecretKeyBackupImportResult> onImported)
    {
        string? initialDirectory = Directory.Exists(_lastDirectory) ? _lastDirectory : null;
        _fileDialogManager.OpenFileDialog("Import backup", ".json", (success, paths) =>
        {
            if (!success) return;
            if (paths.FirstOrDefault() is not string path) return;

            try
            {
                var imported = _backupService.ImportForInitialSetup(path);
                _lastDirectory = Path.GetDirectoryName(path) ?? string.Empty;
                _pendingInitialSetupImport = imported.CurrentCharacterAssigned || imported.KeyChoices.Count == 0 ? null : imported;
                _pendingInitialSetupCallback = _pendingInitialSetupImport == null ? null : onImported;
                _selectedKeyIndex = null;
                SetStatus(
                    imported.CurrentCharacterAssigned
                        ? imported.AutoAssignedCurrentCharacter
                            ? $"Backup merged for {imported.ServiceName}. This character was assigned to the only key in the backup. Attempting to connect."
                            : $"Backup merged for {imported.ServiceName}. This character already has an assignment. Attempting to connect."
                        : imported.KeyChoices.Count > 0
                            ? $"Backup merged for {imported.ServiceName}. Select an imported key for this character below."
                            : $"Backup merged for {imported.ServiceName}, but it contains no usable keys for this character.",
                    success: true);

                onImported(imported);
            }
            catch (Exception ex)
            {
                LogInitialImportFailure(_logger, ex);
                SetStatus("Secret key backup import failed. Ensure the file is a valid backup JSON.", success: false);
            }
        }, 1, initialDirectory);
    }

    private void AssignSelectedKey()
    {
        if (_pendingInitialSetupImport == null || !_selectedKeyIndex.HasValue)
            return;

        try
        {
            var assigned = _backupService.AssignCurrentCharacter(_pendingInitialSetupImport, _selectedKeyIndex.Value);
            var callback = _pendingInitialSetupCallback;
            _pendingInitialSetupImport = null;
            _pendingInitialSetupCallback = null;
            _selectedKeyIndex = null;
            SetStatus($"Assigned the imported key for {assigned.ServiceName}. Attempting to connect.", success: true);
            callback?.Invoke(assigned);
        }
        catch (InvalidOperationException ex)
        {
            LogAssignmentFailure(_logger, ex);
            SetStatus("The imported key could not be assigned. Check plugin logs for details.", success: false);
        }
    }

    private static string GetChoiceLabel(SecretKeyBackupKeyChoice choice)
    {
        return string.IsNullOrWhiteSpace(choice.FriendlyName) ? $"Secret key {choice.KeyIndex + 1}" : choice.FriendlyName;
    }

    private void SetStatus(string message, bool success)
    {
        _message = message;
        _success = success;
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to export Snowcloak backup")]
    private static partial void LogExportFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to restore secret key backup")]
    private static partial void LogRestoreFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to import secret key backup during initial setup")]
    private static partial void LogInitialImportFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to assign imported secret key during initial setup")]
    private static partial void LogAssignmentFailure(ILogger logger, Exception exception);
}
