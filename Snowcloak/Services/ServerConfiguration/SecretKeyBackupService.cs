using Snowcloak.API.Data;
using Snowcloak.Configuration.Models;
using System.Text.Json;

namespace Snowcloak.Services.ServerConfiguration;

public sealed class SecretKeyBackupService
{
    private const int SecretKeyBackupVersion = 1;
    private readonly ServerConfigurationManager _serverConfigurationManager;

    public SecretKeyBackupService(ServerConfigurationManager serverConfigurationManager)
    {
        _serverConfigurationManager = serverConfigurationManager;
    }

    public SecretKeyBackupExportResult Export(ServerStorage selectedServer, string path)
    {
        var notes = _serverConfigurationManager.GetNotesForServer(selectedServer.ServerUri);
        var backup = new SecretKeyBackupFile()
        {
            Version = SecretKeyBackupVersion,
            ExportedAtUtc = DateTime.UtcNow,
            ServiceName = selectedServer.ServerName,
            ServiceUri = selectedServer.ServerUri,
            SecretKeys = CloneSecretKeys(selectedServer.SecretKeys),
            CharacterAssignments = CloneAuthentications(selectedServer.Authentications),
            Notes = CloneNotes(notes)
        };

        File.WriteAllText(path, JsonSerializer.Serialize(backup, new JsonSerializerOptions() { WriteIndented = true }));
        return new SecretKeyBackupExportResult(backup.SecretKeys.Count, backup.CharacterAssignments.Count, backup.Notes.UidServerComments.Count);
    }

    public SecretKeyBackupImportResult ImportIntoServer(string path, ServerStorage selectedServer)
    {
        var imported = LoadBackup(path);
        ApplyBackupToServer(imported, selectedServer);
        int serverIndex = Array.IndexOf(_serverConfigurationManager.GetServerApiUrls(), selectedServer.ServerUri);
        return CreateImportResult(selectedServer, serverIndex, imported,
            currentCharacterAssigned: serverIndex >= 0 && _serverConfigurationManager.HasCurrentCharacterAssignment(serverIndex));
    }

    public SecretKeyBackupImportResult ImportForInitialSetup(string path)
    {
        var imported = LoadBackup(path);
        int targetServerIndex = ResolveServerIndex(imported);
        var targetServer = _serverConfigurationManager.GetServerByIndex(targetServerIndex);

        ApplyBackupToServer(imported, targetServer);

        bool autoAssignedCurrentCharacter = false;
        bool currentCharacterAssigned = _serverConfigurationManager.HasCurrentCharacterAssignment(targetServerIndex);
        if (!currentCharacterAssigned && targetServer.SecretKeys.Count == 1)
        {
            _serverConfigurationManager.AddCurrentCharacterToServer(targetServerIndex, targetServer.SecretKeys.Single().Key, save: true);
            autoAssignedCurrentCharacter = true;
            currentCharacterAssigned = true;
        }

        return CreateImportResult(targetServer, targetServerIndex, imported, currentCharacterAssigned, autoAssignedCurrentCharacter);
    }

    private int ResolveServerIndex(SecretKeyBackupFile imported)
    {
        if (string.IsNullOrWhiteSpace(imported.ServiceUri))
        {
            return _serverConfigurationManager.CurrentServerIndex;
        }

        var serverApiUrls = _serverConfigurationManager.GetServerApiUrls();
        int existingIndex = Array.FindIndex(serverApiUrls,
            uri => string.Equals(uri, imported.ServiceUri, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _serverConfigurationManager.SelectServer(existingIndex);
            return existingIndex;
        }

        _serverConfigurationManager.AddServer(new ServerStorage()
        {
            ServerName = string.IsNullOrWhiteSpace(imported.ServiceName) ? imported.ServiceUri : imported.ServiceName,
            ServerUri = imported.ServiceUri,
        });

        serverApiUrls = _serverConfigurationManager.GetServerApiUrls();
        int createdIndex = Array.FindIndex(serverApiUrls,
            uri => string.Equals(uri, imported.ServiceUri, StringComparison.OrdinalIgnoreCase));
        if (createdIndex < 0)
            throw new InvalidOperationException($"Could not create service entry for {imported.ServiceUri}.");

        _serverConfigurationManager.SelectServer(createdIndex);
        return createdIndex;
    }

    private void ApplyBackupToServer(SecretKeyBackupFile imported, ServerStorage selectedServer)
    {
        selectedServer.SecretKeys = CloneSecretKeys(imported.SecretKeys);
        selectedServer.Authentications = CloneAuthentications(imported.CharacterAssignments);
        _serverConfigurationManager.ReplaceNotesForServer(selectedServer.ServerUri, CloneNotes(imported.Notes), save: true);
        _serverConfigurationManager.Save();
    }

    private static SecretKeyBackupImportResult CreateImportResult(ServerStorage selectedServer, int serverIndex, SecretKeyBackupFile imported,
        bool currentCharacterAssigned, bool autoAssignedCurrentCharacter = false)
    {
        return new SecretKeyBackupImportResult(
            selectedServer.ServerName,
            selectedServer.ServerUri,
            serverIndex,
            selectedServer.SecretKeys.Count,
            selectedServer.Authentications.Count,
            imported.Notes.UidServerComments.Count,
            currentCharacterAssigned,
            autoAssignedCurrentCharacter);
    }

    private static SecretKeyBackupFile LoadBackup(string path)
    {
        var fileContent = File.ReadAllText(path);
        var imported = JsonSerializer.Deserialize<SecretKeyBackupFile>(fileContent, new JsonSerializerOptions()
        {
            PropertyNameCaseInsensitive = true
        });
        if (imported == null)
        {
            throw new InvalidDataException("Backup file could not be parsed.");
        }
        if (imported.Version > SecretKeyBackupVersion)
        {
            throw new InvalidDataException($"Backup version {imported.Version} is not supported by this client.");
        }

        imported.SecretKeys ??= [];
        imported.CharacterAssignments ??= [];
        imported.Notes ??= new ServerNotesStorage();

        if (imported.CharacterAssignments.Any(a =>
                a.SecretKeyIdx != -1 && !imported.SecretKeys.ContainsKey(a.SecretKeyIdx)))
        {
            throw new InvalidDataException("Backup contains character assignments that reference missing secret keys.");
        }

        return imported;
    }

    private static Dictionary<int, SecretKey> CloneSecretKeys(Dictionary<int, SecretKey> source)
    {
        return source.ToDictionary(
            kvp => kvp.Key,
            kvp => new SecretKey()
            {
                FriendlyName = kvp.Value.FriendlyName,
                Key = kvp.Value.Key
            });
    }

    private static List<Authentication> CloneAuthentications(IEnumerable<Authentication> source)
    {
        return source.Select(a => new Authentication()
        {
            CharacterName = a.CharacterName,
            WorldId = a.WorldId,
            SecretKeyIdx = a.SecretKeyIdx
        }).ToList();
    }

    private static ServerNotesStorage CloneNotes(ServerNotesStorage notes)
    {
        var gidComments = notes.GidServerComments ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var uidComments = notes.UidServerComments ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var uidNames = notes.UidLastSeenNames ?? new Dictionary<string, string>(StringComparer.Ordinal);

        return new ServerNotesStorage()
        {
            GidServerComments = gidComments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
            UidServerComments = uidComments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
            UidLastSeenNames = uidNames.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal)
        };
    }

    [Serializable]
    private sealed class SecretKeyBackupFile
    {
        public int Version { get; set; } = SecretKeyBackupVersion;
        public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
        public string ServiceName { get; set; } = string.Empty;
        public string ServiceUri { get; set; } = string.Empty;
        public Dictionary<int, SecretKey> SecretKeys { get; set; } = [];
        public List<Authentication> CharacterAssignments { get; set; } = [];
        public ServerNotesStorage Notes { get; set; } = new();
    }
}

public sealed class SecretKeyBackupExportResult
{
    public SecretKeyBackupExportResult(int secretKeyCount, int characterAssignmentCount, int userNoteCount)
    {
        SecretKeyCount = secretKeyCount;
        CharacterAssignmentCount = characterAssignmentCount;
        UserNoteCount = userNoteCount;
    }

    public int SecretKeyCount { get; }
    public int CharacterAssignmentCount { get; }
    public int UserNoteCount { get; }
}

public sealed class SecretKeyBackupImportResult
{
    public SecretKeyBackupImportResult(string serviceName, string serviceUri, int serverIndex, int secretKeyCount, int characterAssignmentCount,
        int userNoteCount, bool currentCharacterAssigned, bool autoAssignedCurrentCharacter)
    {
        ServiceName = serviceName;
        ServiceUri = serviceUri;
        ServerIndex = serverIndex;
        SecretKeyCount = secretKeyCount;
        CharacterAssignmentCount = characterAssignmentCount;
        UserNoteCount = userNoteCount;
        CurrentCharacterAssigned = currentCharacterAssigned;
        AutoAssignedCurrentCharacter = autoAssignedCurrentCharacter;
    }

    public string ServiceName { get; }
    public string ServiceUri { get; }
    public int ServerIndex { get; }
    public int SecretKeyCount { get; }
    public int CharacterAssignmentCount { get; }
    public int UserNoteCount { get; }
    public bool CurrentCharacterAssigned { get; }
    public bool AutoAssignedCurrentCharacter { get; }
}
