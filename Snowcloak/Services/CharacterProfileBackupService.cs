using System.Text.Json;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;

namespace Snowcloak.Services;

public sealed class CharacterProfileBackupService
{
    private readonly ILogger<CharacterProfileBackupService> _logger;
    private readonly string _backupPath;
    private readonly Lock _syncRoot = new();
    private CharacterProfileBackupFile _file;

    public CharacterProfileBackupService(ILogger<CharacterProfileBackupService> logger, SnowcloakConfigService configService)
    {
        _logger = logger;
        _backupPath = Path.Combine(configService.ConfigurationDirectory, "rp-profile-backups.json");
        _file = Load();
    }

    public IReadOnlyList<CharacterProfileBackup> GetBackups()
    {
        lock (_syncRoot)
        {
            return _file.Profiles
                .OrderByDescending(p => p.UpdatedAtUtc)
                .Select(Clone)
                .ToList();
        }
    }

    public CharacterProfileDraft? GetDraft(string ident)
    {
        if (string.IsNullOrWhiteSpace(ident)) return null;

        lock (_syncRoot)
        {
            var draft = _file.Drafts.FirstOrDefault(d => d.Ident.Equals(ident, StringComparison.Ordinal));
            return draft == null ? null : Clone(draft);
        }
    }

    public void SaveDraft(string ident, string characterLabel, CharacterProfileDocumentDto document)
    {
        if (string.IsNullOrWhiteSpace(ident)) return;

        lock (_syncRoot)
        {
            var draft = _file.Drafts.FirstOrDefault(d => d.Ident.Equals(ident, StringComparison.Ordinal));
            if (draft == null)
            {
                draft = new CharacterProfileDraft { Ident = ident };
                _file.Drafts.Add(draft);
            }

            if (!string.IsNullOrWhiteSpace(characterLabel))
                draft.CharacterLabel = characterLabel.Trim();
            draft.Document = document;
            draft.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Persist();
        }
    }

    public void ClearDraft(string ident)
    {
        if (string.IsNullOrWhiteSpace(ident)) return;

        lock (_syncRoot)
        {
            if (_file.Drafts.RemoveAll(d => d.Ident.Equals(ident, StringComparison.Ordinal)) > 0)
                Persist();
        }
    }

    public void Save(string ident, string characterLabel, CharacterProfileDocumentDto document)
    {
        if (string.IsNullOrWhiteSpace(ident)) return;

        lock (_syncRoot)
        {
            var backup = _file.Profiles.FirstOrDefault(p => p.KnownIdents.Contains(ident, StringComparer.Ordinal));
            if (backup == null)
            {
                backup = new CharacterProfileBackup();
                _file.Profiles.Add(backup);
            }

            if (!backup.KnownIdents.Contains(ident, StringComparer.Ordinal))
                backup.KnownIdents.Add(ident);
            if (!string.IsNullOrWhiteSpace(characterLabel))
                backup.CharacterLabel = characterLabel.Trim();

            backup.Profile = document;
            backup.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Persist();
        }
    }

    public CharacterProfileDocumentDto? GetDocument(Guid backupId)
    {
        lock (_syncRoot)
        {
            var backup = _file.Profiles.SingleOrDefault(p => p.Id == backupId);
            return backup?.Profile ?? backup?.PrivateProfile ?? backup?.PublicProfile;
        }
    }

    private CharacterProfileBackupFile Load()
    {
        try
        {
            if (!File.Exists(_backupPath)) return new();
            return JsonSerializer.Deserialize<CharacterProfileBackupFile>(File.ReadAllText(_backupPath)) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load RP profile backups from {path}", _backupPath);
            return new();
        }
    }

    private void Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(_backupPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _backupPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _backupPath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save RP profile backups to {path}", _backupPath);
        }
    }

    private static CharacterProfileBackup Clone(CharacterProfileBackup profile)
    {
        return new CharacterProfileBackup
        {
            Id = profile.Id,
            CharacterLabel = profile.CharacterLabel,
            KnownIdents = [.. profile.KnownIdents],
            Profile = profile.Profile,
            PublicProfile = profile.PublicProfile,
            PrivateProfile = profile.PrivateProfile,
            UpdatedAtUtc = profile.UpdatedAtUtc,
        };
    }

    private static CharacterProfileDraft Clone(CharacterProfileDraft draft)
    {
        return new CharacterProfileDraft
        {
            Ident = draft.Ident,
            CharacterLabel = draft.CharacterLabel,
            Document = draft.Document,
            UpdatedAtUtc = draft.UpdatedAtUtc,
        };
    }

    private sealed class CharacterProfileBackupFile
    {
        public int Version { get; set; } = 2;
        public List<CharacterProfileBackup> Profiles { get; set; } = [];
        public List<CharacterProfileDraft> Drafts { get; set; } = [];
    }
}

public sealed class CharacterProfileBackup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CharacterLabel { get; set; } = string.Empty;
    public List<string> KnownIdents { get; set; } = [];
    public CharacterProfileDocumentDto? Profile { get; set; }
    public CharacterProfileDocumentDto? PublicProfile { get; set; }
    public CharacterProfileDocumentDto? PrivateProfile { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CharacterProfileDraft
{
    public string Ident { get; set; } = string.Empty;
    public string CharacterLabel { get; set; } = string.Empty;
    public CharacterProfileDocumentDto Document { get; set; } = new();
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
