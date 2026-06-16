using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Pairs;
using System.Text;

namespace Snowcloak.Services.ServerConfiguration;

public sealed class NotesStore
{
    private const string NotesEnd = "##SNOWCLOAK_USER_NOTES_END##";
    private const string NotesStart = "##SNOWCLOAK_USER_NOTES_START##";
    private readonly NotesConfigService _notesConfig;
    private readonly ServerRegistry _serverRegistry;
    private readonly SnowcloakConfigService _snowcloakConfigService;

    public NotesStore(ServerRegistry serverRegistry, NotesConfigService notesConfig, SnowcloakConfigService snowcloakConfigService)
    {
        _serverRegistry = serverRegistry;
        _notesConfig = notesConfig;
        _snowcloakConfigService = snowcloakConfigService;
    }

    public void AutofillNoteWithCharacterName(string uid, string note)
    {
        if (!_snowcloakConfigService.Current.AutofillEmptyNotesFromCharaName || GetNoteForUid(uid) != null)
        {
            return;
        }

        SetNoteForUid(uid, note, save: true);
    }

    public bool ApplyNotesFromClipboard(string notes, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(notes);

        var splitNotes = notes.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
        var splitNotesStart = splitNotes.FirstOrDefault();
        var splitNotesEnd = splitNotes.LastOrDefault();
        if (!string.Equals(splitNotesStart, NotesStart, StringComparison.Ordinal) || !string.Equals(splitNotesEnd, NotesEnd, StringComparison.Ordinal))
        {
            return false;
        }

        splitNotes.RemoveAll(n => string.Equals(n, NotesStart, StringComparison.Ordinal) || string.Equals(n, NotesEnd, StringComparison.Ordinal));

        foreach (var note in splitNotes)
        {
            var splittedEntry = note.Split(":", 2, StringSplitOptions.RemoveEmptyEntries);
            if (splittedEntry.Length < 2)
            {
                continue;
            }

            var uid = splittedEntry[0];
            var comment = splittedEntry[1].Trim('"');
            if (GetNoteForUid(uid) != null && !overwrite)
            {
                continue;
            }

            SetNoteForUid(uid, comment, save: false);
        }

        Save();

        return true;
    }

    public static string ExportNotes(IEnumerable<Pair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        StringBuilder sb = new();
        sb.AppendLine(NotesStart);
        foreach (var entry in pairs)
        {
            var note = entry.GetNote();
            if (string.IsNullOrEmpty(note))
            {
                continue;
            }

            sb.Append(entry.UserData.UID).Append(":\"").Append(entry.GetNote()).AppendLine("\"");
        }
        sb.AppendLine(NotesEnd);

        return sb.ToString();
    }

    public string? GetNameForUid(string uid)
    {
        if (CurrentNotesStorage().UidLastSeenNames.TryGetValue(uid, out var name))
        {
            return string.IsNullOrEmpty(name) ? null : name;
        }

        return null;
    }

    public string? GetNoteForGid(string gid)
    {
        if (CurrentNotesStorage().GidServerComments.TryGetValue(gid, out var note))
        {
            return string.IsNullOrEmpty(note) ? null : note;
        }

        return null;
    }

    public string? GetNoteForUid(string uid)
    {
        if (CurrentNotesStorage().UidServerComments.TryGetValue(uid, out var note))
        {
            return string.IsNullOrEmpty(note) ? null : note;
        }

        return null;
    }

    public ServerNotesStorage GetNotesForServer(string serverUri)
    {
        if (string.IsNullOrWhiteSpace(serverUri))
        {
            return new ServerNotesStorage();
        }

        if (!_notesConfig.Current.ServerNotes.TryGetValue(serverUri, out var notes))
        {
            return new ServerNotesStorage();
        }

        return CloneNotesStorage(notes);
    }

    public ServerNotesStorage GetNotesForServer(Uri serverUri)
    {
        ArgumentNullException.ThrowIfNull(serverUri);

        return GetNotesForServer(serverUri.ToString());
    }

    public void ReplaceNotesForServer(string serverUri, ServerNotesStorage notes, bool save = true)
    {
        if (string.IsNullOrWhiteSpace(serverUri))
        {
            return;
        }

        _notesConfig.Current.ServerNotes[serverUri] = CloneNotesStorage(notes ?? new ServerNotesStorage());

        if (save)
        {
            Save();
        }
    }

    public void ReplaceNotesForServer(Uri serverUri, ServerNotesStorage notes, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(serverUri);

        ReplaceNotesForServer(serverUri.ToString(), notes, save);
    }

    public void Save()
    {
        _notesConfig.Update(_ => { });
    }

    public void SetNameForUid(string uid, string name)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return;
        }

        if (CurrentNotesStorage().UidLastSeenNames.TryGetValue(uid, out var currentName) && currentName.Equals(name, StringComparison.Ordinal))
        {
            return;
        }

        CurrentNotesStorage().UidLastSeenNames[uid] = name;
        Save();
    }

    public void SetNoteForGid(string gid, string note, bool save = true)
    {
        if (string.IsNullOrEmpty(gid))
        {
            return;
        }

        CurrentNotesStorage().GidServerComments[gid] = note;
        if (save)
        {
            Save();
        }
    }

    public void SetNoteForUid(string uid, string note, bool save = true)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return;
        }

        CurrentNotesStorage().UidServerComments[uid] = note;
        if (save)
        {
            Save();
        }
    }

    private static ServerNotesStorage CloneNotesStorage(ServerNotesStorage notes)
    {
        var gidComments = notes.GidServerComments ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var uidComments = notes.UidServerComments ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var uidNames = notes.UidLastSeenNames ?? new Dictionary<string, string>(StringComparer.Ordinal);

        return new ServerNotesStorage
        {
            GidServerComments = gidComments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
            UidServerComments = uidComments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
            UidLastSeenNames = uidNames.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal)
        };
    }

    private ServerNotesStorage CurrentNotesStorage()
    {
        if (!_notesConfig.Current.ServerNotes.TryGetValue(_serverRegistry.CurrentApiUrl, out var notes))
        {
            notes = new ServerNotesStorage();
            _notesConfig.Current.ServerNotes[_serverRegistry.CurrentApiUrl] = notes;
        }

        return notes;
    }
}
