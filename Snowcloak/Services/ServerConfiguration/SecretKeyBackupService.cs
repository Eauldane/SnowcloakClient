using Snowcloak.API.Data;
using Snowcloak.Configuration.Models;
using System.Text.Json;

namespace Snowcloak.Services.ServerConfiguration;

public sealed class SecretKeyBackupService
{
    private const int SecretKeyBackupVersion = 1;
    private static readonly JsonSerializerOptions ExportOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ImportOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly NotesStore _notesStore;
    private readonly ServerRegistry _serverRegistry;

    public SecretKeyBackupService(ServerRegistry serverRegistry, NotesStore notesStore)
    {
        _serverRegistry = serverRegistry;
        _notesStore = notesStore;
    }

    public SecretKeyBackupExportResult Export(ServerStorage selectedServer, string path)
    {
        ArgumentNullException.ThrowIfNull(selectedServer);

        var notes = _notesStore.GetNotesForServer(new Uri(selectedServer.ServerUri, UriKind.Absolute));
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

        File.WriteAllText(path, JsonSerializer.Serialize(backup, ExportOptions));
        return new SecretKeyBackupExportResult(backup.SecretKeys.Count, backup.CharacterAssignments.Count, backup.Notes.UidServerComments.Count);
    }

    public SecretKeyBackupImportResult ImportIntoServer(string path, ServerStorage selectedServer)
    {
        ArgumentNullException.ThrowIfNull(selectedServer);

        var imported = LoadBackup(path);
        var merge = MergeBackupIntoServer(imported, selectedServer);
        int serverIndex = Array.IndexOf(_serverRegistry.GetServerApiUrls(), selectedServer.ServerUri);
        return CreateImportResult(selectedServer, serverIndex, merge,
            currentCharacterAssigned: serverIndex >= 0 && _serverRegistry.HasCurrentCharacterAssignment(serverIndex));
    }

    public SecretKeyBackupImportResult ImportForInitialSetup(string path)
    {
        var imported = LoadBackup(path);
        int targetServerIndex = ResolveServerIndex(imported);
        var targetServer = _serverRegistry.GetServerByIndex(targetServerIndex);

        var merge = MergeBackupIntoServer(imported, targetServer);

        bool autoAssignedCurrentCharacter = false;
        bool currentCharacterAssigned = _serverRegistry.HasCurrentCharacterAssignment(targetServerIndex);
        if (!currentCharacterAssigned && merge.ImportedKeyChoices.Count == 1)
        {
            _serverRegistry.AddCurrentCharacterToServer(targetServerIndex, merge.ImportedKeyChoices[0].KeyIndex, save: true);
            autoAssignedCurrentCharacter = true;
            currentCharacterAssigned = _serverRegistry.HasCurrentCharacterAssignment(targetServerIndex);
        }

        return CreateImportResult(targetServer, targetServerIndex, merge, currentCharacterAssigned, autoAssignedCurrentCharacter);
    }

    public SecretKeyBackupImportResult AssignCurrentCharacter(SecretKeyBackupImportResult imported, int keyIndex)
    {
        ArgumentNullException.ThrowIfNull(imported);

        if (imported.ServerIndex < 0 || imported.KeyChoices.All(choice => choice.KeyIndex != keyIndex))
        {
            throw new InvalidOperationException("The selected backup key is not available for assignment.");
        }

        _serverRegistry.AddCurrentCharacterToServer(imported.ServerIndex, keyIndex, save: true);
        if (!_serverRegistry.HasCurrentCharacterAssignment(imported.ServerIndex))
        {
            throw new InvalidOperationException("The current character could not be assigned to the selected backup key.");
        }

        return imported.WithCurrentCharacterAssignment();
    }

    private int ResolveServerIndex(SecretKeyBackupFile imported)
    {
        if (string.IsNullOrWhiteSpace(imported.ServiceUri))
        {
            return _serverRegistry.CurrentServerIndex;
        }

        var serverApiUrls = _serverRegistry.GetServerApiUrls();
        int existingIndex = Array.FindIndex(serverApiUrls,
            uri => string.Equals(uri, imported.ServiceUri, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _serverRegistry.SelectServer(existingIndex);
            return existingIndex;
        }

        _serverRegistry.AddServer(new ServerStorage()
        {
            ServerName = string.IsNullOrWhiteSpace(imported.ServiceName) ? imported.ServiceUri : imported.ServiceName,
            ServerUri = imported.ServiceUri,
        });

        serverApiUrls = _serverRegistry.GetServerApiUrls();
        int createdIndex = Array.FindIndex(serverApiUrls,
            uri => string.Equals(uri, imported.ServiceUri, StringComparison.OrdinalIgnoreCase));
        if (createdIndex < 0)
            throw new InvalidOperationException($"Could not create service entry for {imported.ServiceUri}.");

        _serverRegistry.SelectServer(createdIndex);
        return createdIndex;
    }

    private BackupMergeResult MergeBackupIntoServer(SecretKeyBackupFile imported, ServerStorage selectedServer)
    {
        var keyIndices = new Dictionary<int, int>();
        var keyChoices = new List<SecretKeyBackupKeyChoice>();
        int addedSecretKeys = 0;
        int nextKeyIndex = selectedServer.SecretKeys.Count == 0 ? 0 : selectedServer.SecretKeys.Keys.Max() + 1;

        foreach (var importedKey in imported.SecretKeys.OrderBy(kvp => kvp.Key))
        {
            var normalisedKey = importedKey.Value.Key.Trim().ToUpperInvariant();
            int? existingKeyIndex = selectedServer.SecretKeys
                .Where(kvp => string.Equals(kvp.Value.Key.Trim(), normalisedKey, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => (int?)kvp.Key)
                .FirstOrDefault();
            int localKeyIndex;
            if (existingKeyIndex.HasValue)
            {
                localKeyIndex = existingKeyIndex.Value;
            }
            else
            {
                while (selectedServer.SecretKeys.ContainsKey(nextKeyIndex))
                {
                    nextKeyIndex++;
                }

                localKeyIndex = nextKeyIndex++;
                selectedServer.SecretKeys.Add(localKeyIndex, new SecretKey
                {
                    FriendlyName = importedKey.Value.FriendlyName,
                    Key = normalisedKey
                });
                addedSecretKeys++;
            }

            keyIndices[importedKey.Key] = localKeyIndex;
            if (normalisedKey.Length == 64
                && normalisedKey.All(char.IsAsciiHexDigit)
                && keyChoices.All(choice => choice.KeyIndex != localKeyIndex))
            {
                keyChoices.Add(new SecretKeyBackupKeyChoice(localKeyIndex, selectedServer.SecretKeys[localKeyIndex].FriendlyName));
            }
        }

        int addedAssignments = 0;
        foreach (var importedAssignment in imported.CharacterAssignments)
        {
            if (importedAssignment.SecretKeyIdx == -1
                || string.IsNullOrWhiteSpace(importedAssignment.CharacterName)
                || !keyIndices.TryGetValue(importedAssignment.SecretKeyIdx, out int localKeyIndex)
                || selectedServer.Authentications.Any(existing =>
                    string.Equals(existing.CharacterName, importedAssignment.CharacterName, StringComparison.OrdinalIgnoreCase)
                    && existing.WorldId == importedAssignment.WorldId))
            {
                continue;
            }

            selectedServer.Authentications.Add(new Authentication
            {
                CharacterName = importedAssignment.CharacterName,
                WorldId = importedAssignment.WorldId,
                SecretKeyIdx = localKeyIndex
            });
            addedAssignments++;
        }

        var serverUri = new Uri(selectedServer.ServerUri, UriKind.Absolute);
        var notes = _notesStore.GetNotesForServer(serverUri);
        int addedUserNotes = MergeMissing(notes.UidServerComments, imported.Notes.UidServerComments);
        MergeMissing(notes.GidServerComments, imported.Notes.GidServerComments);
        MergeMissing(notes.UidLastSeenNames, imported.Notes.UidLastSeenNames);
        _notesStore.ReplaceNotesForServer(serverUri, notes, save: true);
        _serverRegistry.Save();

        return new BackupMergeResult(addedSecretKeys, addedAssignments, addedUserNotes, keyChoices);
    }

    private static int MergeMissing(Dictionary<string, string> destination, Dictionary<string, string>? source)
    {
        int added = 0;
        foreach (var item in source ?? [])
        {
            if (destination.TryAdd(item.Key, item.Value))
            {
                added++;
            }
        }

        return added;
    }

    private static SecretKeyBackupImportResult CreateImportResult(ServerStorage selectedServer, int serverIndex, BackupMergeResult merge,
        bool currentCharacterAssigned, bool autoAssignedCurrentCharacter = false)
    {
        return new SecretKeyBackupImportResult(
            selectedServer.ServerName,
            serverIndex,
            selectedServer.SecretKeys.Count,
            selectedServer.Authentications.Count,
            merge.AddedSecretKeyCount,
            merge.AddedCharacterAssignmentCount,
            merge.AddedUserNoteCount,
            currentCharacterAssigned,
            autoAssignedCurrentCharacter,
            merge.ImportedKeyChoices);
    }

    private static SecretKeyBackupFile LoadBackup(string path)
    {
        var fileContent = File.ReadAllText(path);
        var imported = JsonSerializer.Deserialize<SecretKeyBackupFile>(fileContent, ImportOptions);
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

    private sealed record BackupMergeResult(
        int AddedSecretKeyCount,
        int AddedCharacterAssignmentCount,
        int AddedUserNoteCount,
        IReadOnlyList<SecretKeyBackupKeyChoice> ImportedKeyChoices);

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
    public SecretKeyBackupImportResult(string serviceName, int serverIndex, int secretKeyCount, int characterAssignmentCount,
        int addedSecretKeyCount, int addedCharacterAssignmentCount, int addedUserNoteCount, bool currentCharacterAssigned,
        bool autoAssignedCurrentCharacter, IReadOnlyList<SecretKeyBackupKeyChoice> keyChoices)
    {
        ServiceName = serviceName;
        ServerIndex = serverIndex;
        SecretKeyCount = secretKeyCount;
        CharacterAssignmentCount = characterAssignmentCount;
        AddedSecretKeyCount = addedSecretKeyCount;
        AddedCharacterAssignmentCount = addedCharacterAssignmentCount;
        AddedUserNoteCount = addedUserNoteCount;
        CurrentCharacterAssigned = currentCharacterAssigned;
        AutoAssignedCurrentCharacter = autoAssignedCurrentCharacter;
        KeyChoices = keyChoices;
    }

    public string ServiceName { get; }
    public int ServerIndex { get; }
    public int SecretKeyCount { get; }
    public int CharacterAssignmentCount { get; }
    public int AddedSecretKeyCount { get; }
    public int AddedCharacterAssignmentCount { get; }
    public int AddedUserNoteCount { get; }
    public bool CurrentCharacterAssigned { get; }
    public bool AutoAssignedCurrentCharacter { get; }
    public IReadOnlyList<SecretKeyBackupKeyChoice> KeyChoices { get; }

    public SecretKeyBackupImportResult WithCurrentCharacterAssignment()
    {
        return new SecretKeyBackupImportResult(
            ServiceName,
            ServerIndex,
            SecretKeyCount,
            CharacterAssignmentCount + 1,
            AddedSecretKeyCount,
            AddedCharacterAssignmentCount + 1,
            AddedUserNoteCount,
            currentCharacterAssigned: true,
            autoAssignedCurrentCharacter: false,
            KeyChoices);
    }
}

public sealed record SecretKeyBackupKeyChoice(int KeyIndex, string FriendlyName);
