using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Colors;
using Dalamud.Utility;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.ServerConfiguration;

namespace Snowcloak.UI.Components.Account;

/// <summary>
/// Secret-key / Snowcloak backup import + export flow shared by onboarding and Settings (P30).
/// Owns the status message and the remembered last-used directory; the surfaces only invoke the
/// begin-methods and render the status line.
/// </summary>
public sealed class SecretKeyBackupFlow
{
    private readonly ILogger _logger;
    private readonly SecretKeyBackupService _backupService;
    private readonly FileDialogManager _fileDialogManager;
    private string? _message;
    private bool _success;
    private string _lastDirectory = string.Empty;

    public SecretKeyBackupFlow(ILogger logger, SecretKeyBackupService backupService, FileDialogManager fileDialogManager)
    {
        _logger = logger;
        _backupService = backupService;
        _fileDialogManager = fileDialogManager;
    }

    public bool HasMessage => !_message.IsNullOrEmpty();

    public void DrawStatus()
    {
        if (_message.IsNullOrEmpty())
            return;
        ElezenImgui.ColouredWrappedText(_message, _success ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
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
                    $"Snowcloak backup exported: {backup.SecretKeyCount} key(s), {backup.CharacterAssignmentCount} assignment(s), {backup.UserNoteCount} user note(s).",
                    success: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to export Snowcloak backup");
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
                    $"Secret key backup restored: {imported.SecretKeyCount} key(s), {imported.CharacterAssignmentCount} assignment(s), {imported.UserNoteCount} user note(s).",
                    success: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore secret key backup");
                SetStatus("Secret key backup restore failed. Ensure the file is a valid backup JSON.", success: false);
            }
        }, 1, initialDirectory);
    }

    /// <summary>
    /// Imports a backup during first-run onboarding. On success the surface's <paramref name="onImported"/>
    /// callback receives the import result so it can reset in-flight registration state and (when the current
    /// character was assigned a key) trigger a connection.
    /// </summary>
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
                SetStatus(
                    imported.CurrentCharacterAssigned
                        ? imported.AutoAssignedCurrentCharacter
                            ? $"Backup imported for {imported.ServiceName}: {imported.SecretKeyCount} key(s), {imported.CharacterAssignmentCount} assignment(s), {imported.UserNoteCount} user note(s). This character was assigned to the only key in the backup. Attempting to connect."
                            : $"Backup imported for {imported.ServiceName}: {imported.SecretKeyCount} key(s), {imported.CharacterAssignmentCount} assignment(s), {imported.UserNoteCount} user note(s). Attempting to connect."
                        : $"Backup imported for {imported.ServiceName}: {imported.SecretKeyCount} key(s), {imported.CharacterAssignmentCount} assignment(s), {imported.UserNoteCount} user note(s). This character is not assigned to a key in the backup.",
                    success: imported.CurrentCharacterAssigned);

                onImported(imported);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import secret key backup during initial setup");
                SetStatus("Secret key backup import failed. Ensure the file is a valid backup JSON.", success: false);
            }
        }, 1, initialDirectory);
    }

    private void SetStatus(string message, bool success)
    {
        _message = message;
        _success = success;
    }
}
