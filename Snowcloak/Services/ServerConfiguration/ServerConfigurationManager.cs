using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.SnowcloakConfiguration;
using Snowcloak.WebAPI;
using System.Diagnostics;

namespace Snowcloak.Services.ServerConfiguration;

public class ServerConfigurationManager
{
    private readonly ServerConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ILogger<ServerConfigurationManager> _logger;
    private readonly NotesConfigService _notesConfig;
    private readonly ServerBlockConfigService _blockConfig;
    private readonly ServerTagConfigService _serverTagConfig;
    private readonly SyncshellConfigService _syncshellConfig;
    private readonly SnowcloakConfigService _snowcloakConfigService;

    private HashSet<string>? _cachedWhitelistedUIDs = null;
    private HashSet<string>? _cachedBlacklistedUIDs = null;
    private string? _realApiUrl = null;

    public ServerConfigurationManager(ILogger<ServerConfigurationManager> logger, ServerConfigService configService,
        ServerTagConfigService serverTagConfig, SyncshellConfigService syncshellConfig, NotesConfigService notesConfig,
        ServerBlockConfigService blockConfig, DalamudUtilService dalamudUtil, SnowcloakConfigService snowcloakConfigService)
    {
        _logger = logger;
        _configService = configService;
        _serverTagConfig = serverTagConfig;
        _syncshellConfig = syncshellConfig;
        _notesConfig = notesConfig;
        _blockConfig = blockConfig;
        _dalamudUtil = dalamudUtil;
        _snowcloakConfigService = snowcloakConfigService;

        EnsureMainExists();
    }

    public string CurrentApiUrl => CurrentServer.ServerUri;
    public string CurrentRealApiUrl
    {
        get
        {
            return _realApiUrl ?? CurrentApiUrl;
        }
    }
    public ServerStorage CurrentServer => _configService.Current.ServerStorage[CurrentServerIndex];

    public IReadOnlyList<string> Whitelist => CurrentBlockStorage().Whitelist;
    public IReadOnlyList<string> Blacklist => CurrentBlockStorage().Blacklist;

    public int CurrentServerIndex
    {
        set
        {
            _configService.Current.CurrentServer = value;
            _cachedWhitelistedUIDs = null;
            _cachedBlacklistedUIDs = null;
            _realApiUrl = null;
            _configService.Save();
        }
        get
        {
            if (_configService.Current.CurrentServer < 0)
            {
                _configService.Current.CurrentServer = 0;
                _configService.Save();
            }

            return _configService.Current.CurrentServer;
        }
    }

    public string? GetSecretKey(out bool hasMulti, int serverIdx = -1)
    {
        ServerStorage? currentServer;
        currentServer = serverIdx == -1 ? CurrentServer : GetServerByIndex(serverIdx);
        if (currentServer == null)
        {
            currentServer = new();
            Save();
        }
        hasMulti = false;

        var charaName = _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult();
        var worldId = _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult();
        if (!currentServer.Authentications.Any() && currentServer.SecretKeys.Any())
        {
            currentServer.Authentications.Add(new Authentication()
            {
                CharacterName = charaName,
                WorldId = worldId,
                SecretKeyIdx = currentServer.SecretKeys.Last().Key,
            });

            Save();
        }

        var auth = currentServer.Authentications.FindAll(f => string.Equals(f.CharacterName, charaName, StringComparison.Ordinal) && f.WorldId == worldId);
        if (auth.Count >= 2)
        {
            _logger.LogTrace("GetSecretKey accessed, returning null because multiple ({count}) identical characters.", auth.Count);
            hasMulti = true;
            return null;
        }

        if (auth.Count == 0)
        {
            _logger.LogTrace("GetSecretKey accessed, returning null because no set up characters for {chara} on {world}", charaName, worldId);
            return null;
        }

        if (currentServer.SecretKeys.TryGetValue(auth.Single().SecretKeyIdx, out var secretKey))
        {
            _logger.LogTrace("GetSecretKey accessed, returning {key} ({keyValue}) for {chara} on {world}", secretKey.FriendlyName, string.Join("", secretKey.Key.Take(10)), charaName, worldId);
            return secretKey.Key;
        }

        _logger.LogTrace("GetSecretKey accessed, returning null because no fitting key found for {chara} on {world} for idx {idx}.", charaName, worldId, auth.Single().SecretKeyIdx);

        return null;
    }

    public string[] GetServerApiUrls()
    {
        return _configService.Current.ServerStorage.Select(v => v.ServerUri).ToArray();
    }

    public ServerStorage GetServerByIndex(int idx)
    {
        try
        {
            return _configService.Current.ServerStorage[idx];
        }
        catch
        {
            _configService.Current.CurrentServer = 0;
            EnsureMainExists();
            return CurrentServer!;
        }
    }

    public string[] GetServerNames()
    {
        return _configService.Current.ServerStorage.Select(v => v.ServerName).ToArray();
    }

    public bool HasValidConfig()
    {
        return CurrentServer != null && CurrentServer.SecretKeys.Any();
    }

    public void Save()
    {
        var caller = new StackTrace().GetFrame(1)?.GetMethod()?.ReflectedType?.Name ?? "Unknown";
        _logger.LogDebug("{caller} Calling config save", caller);
        _configService.Save();
    }

    public void SelectServer(int idx)
    {
        _configService.Current.CurrentServer = idx;
        CurrentServer!.FullPause = false;
        Save();
    }

    internal void AddCurrentCharacterToServer(int serverSelectionIndex = -1, bool save = true)
    {
        if (serverSelectionIndex == -1) serverSelectionIndex = CurrentServerIndex;
        var server = GetServerByIndex(serverSelectionIndex);
        if (server.Authentications.Any(c => string.Equals(c.CharacterName, _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult(), StringComparison.Ordinal)
                && c.WorldId == _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult()))
            return;

        server.Authentications.Add(new Authentication()
        {
            CharacterName = _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult(),
            WorldId = _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult(),
            SecretKeyIdx = server.SecretKeys.Last().Key,
        });

        if (save)
            Save();
    }

    internal void AddEmptyCharacterToServer(int serverSelectionIndex)
    {
        var server = GetServerByIndex(serverSelectionIndex);
        server.Authentications.Add(new Authentication()
        {
            SecretKeyIdx = server.SecretKeys.Any() ? server.SecretKeys.First().Key : -1,
        });
        Save();
    }

    internal void AddOpenPairTag(string tag)
    {
        CurrentServerTagStorage().OpenPairTags.Add(tag);
        _serverTagConfig.Save();
    }

    internal void AddServer(ServerStorage serverStorage)
    {
        _configService.Current.ServerStorage.Add(serverStorage);
        Save();
    }

    internal void AddTag(string tag)
    {
        CurrentServerTagStorage().ServerAvailablePairTags.Add(tag);
        _serverTagConfig.Save();
    }

    internal void AddTagForUid(string uid, string tagName)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            tags.Add(tagName);
        }
        else
        {
            CurrentServerTagStorage().UidServerPairedUserTags[uid] = [tagName];
        }

        _serverTagConfig.Save();
    }

    internal bool ContainsOpenPairTag(string tag)
    {
        return CurrentServerTagStorage().OpenPairTags.Contains(tag);
    }

    internal bool ContainsTag(string uid, string tag)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            return tags.Contains(tag, StringComparer.Ordinal);
        }

        return false;
    }

    internal void DeleteServer(ServerStorage selectedServer)
    {
        if (Array.IndexOf(_configService.Current.ServerStorage.ToArray(), selectedServer) <
            _configService.Current.CurrentServer)
        {
            _configService.Current.CurrentServer--;
        }

        _configService.Current.ServerStorage.Remove(selectedServer);
        Save();
    }

    internal string? GetNoteForGid(string gID)
    {
        if (CurrentNotesStorage().GidServerComments.TryGetValue(gID, out var note))
        {
            if (string.IsNullOrEmpty(note)) return null;
            return note;
        }

        return null;
    }

    internal string? GetNoteForUid(string uid)
    {
        if (CurrentNotesStorage().UidServerComments.TryGetValue(uid, out var note))
        {
            if (string.IsNullOrEmpty(note)) return null;
            return note;
        }
        return null;
    }

    internal string? GetNameForUid(string uid)
    {
        if (CurrentNotesStorage().UidLastSeenNames.TryGetValue(uid, out var name))
        {
            if (string.IsNullOrEmpty(name)) return null;
            return name;
        }
        return null;
    }

    internal HashSet<string> GetServerAvailablePairTags()
    {
        return CurrentServerTagStorage().ServerAvailablePairTags;
    }

    internal ShellConfig GetShellConfigForGid(string gid)
    {
        if (CurrentSyncshellStorage().GidShellConfig.TryGetValue(gid, out var config))
            return config;

        // Pick the next higher syncshell number that is available
        int newShellNumber = CurrentSyncshellStorage().GidShellConfig.Count > 0 ? CurrentSyncshellStorage().GidShellConfig.Select(x => x.Value.ShellNumber).Max() + 1 : 1;

        var shellConfig = new ShellConfig{
            ShellNumber = newShellNumber
        };

        // Save config to avoid auto-generated numbers shuffling around
        SaveShellConfigForGid(gid, shellConfig);

        return CurrentSyncshellStorage().GidShellConfig[gid];
    }

    internal int GetShellNumberForGid(string gid)
    {
        return GetShellConfigForGid(gid).ShellNumber;
    }

    internal Dictionary<string, List<string>> GetUidServerPairedUserTags()
    {
        return CurrentServerTagStorage().UidServerPairedUserTags;
    }

    internal HashSet<string> GetUidsForTag(string tag)
    {
        return CurrentServerTagStorage().UidServerPairedUserTags.Where(p => p.Value.Contains(tag, StringComparer.Ordinal)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }

    internal bool HasTags(string uid)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            return tags.Any();
        }

        return false;
    }

    internal void RemoveCharacterFromServer(int serverSelectionIndex, Authentication item)
    {
        var server = GetServerByIndex(serverSelectionIndex);
        server.Authentications.Remove(item);
        Save();
    }

    internal void RemoveOpenPairTag(string tag)
    {
        CurrentServerTagStorage().OpenPairTags.Remove(tag);
        _serverTagConfig.Save();
    }

    internal void RemoveTag(string tag)
    {
        CurrentServerTagStorage().ServerAvailablePairTags.Remove(tag);
        foreach (var uid in GetUidsForTag(tag))
        {
            RemoveTagForUid(uid, tag, save: false);
        }
        _serverTagConfig.Save();
    }

    internal void RemoveTagForUid(string uid, string tagName, bool save = true)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            tags.Remove(tagName);

            if (save)
            {
                _serverTagConfig.Save();
            }
        }
    }

    internal void RenameTag(string oldName, string newName)
    {
        CurrentServerTagStorage().ServerAvailablePairTags.Remove(oldName);
        CurrentServerTagStorage().ServerAvailablePairTags.Add(newName);
        foreach (var existingTags in CurrentServerTagStorage().UidServerPairedUserTags.Select(k => k.Value))
        {
            if (existingTags.Remove(oldName))
                existingTags.Add(newName);
        }
    }

    internal void SaveNotes()
    {
        _notesConfig.Save();
    }

    internal void SetNoteForGid(string gid, string note, bool save = true)
    {
        if (string.IsNullOrEmpty(gid)) return;

        CurrentNotesStorage().GidServerComments[gid] = note;
        if (save)
            _notesConfig.Save();
    }

    internal void AutofillNoteWithCharacterName(string uid, string note)
    {
        if (!_snowcloakConfigService.Current.AutofillEmptyNotesFromCharaName
            || GetNoteForUid(uid) != null)
            return;

        SetNoteForUid(uid, note, save: true);
    }
    
    internal void SetNoteForUid(string uid, string note, bool save = true)
    {
        if (string.IsNullOrEmpty(uid)) return;

        CurrentNotesStorage().UidServerComments[uid] = note;
        if (save)
            _notesConfig.Save();
    }

    internal void SetNameForUid(string uid, string name)
    {
        if (string.IsNullOrEmpty(uid)) return;

        if (CurrentNotesStorage().UidLastSeenNames.TryGetValue(uid, out var currentName) && currentName.Equals(name, StringComparison.Ordinal))
            return;

        CurrentNotesStorage().UidLastSeenNames[uid] = name;
        _notesConfig.Save();
    }

    internal void SaveShellConfigForGid(string gid, ShellConfig config)
    {
        if (string.IsNullOrEmpty(gid)) return;

        // This is somewhat pointless because ShellConfig is a ref type we returned to the caller anyway...
        CurrentSyncshellStorage().GidShellConfig[gid] = config;

        _syncshellConfig.Save();
    }

    internal bool IsUidWhitelisted(string uid)
    {
        _cachedWhitelistedUIDs ??= [.. CurrentBlockStorage().Whitelist];
        return _cachedWhitelistedUIDs.Contains(uid);
    }

    internal bool IsUidBlacklisted(string uid)
    {
        _cachedBlacklistedUIDs ??= [.. CurrentBlockStorage().Blacklist];
        return _cachedBlacklistedUIDs.Contains(uid);
    }

    internal void AddWhitelistUid(string uid)
    {
        if (IsUidWhitelisted(uid))
            return;
        if (CurrentBlockStorage().Blacklist.RemoveAll(u => u.Equals(uid, StringComparison.Ordinal)) > 0)
            _cachedBlacklistedUIDs = null;
        CurrentBlockStorage().Whitelist.Add(uid);
        _cachedWhitelistedUIDs = null;
        _blockConfig.Save();
    }

    internal void AddBlacklistUid(string uid)
    {
        if (IsUidBlacklisted(uid))
            return;
        if (CurrentBlockStorage().Whitelist.RemoveAll(u => u.Equals(uid, StringComparison.Ordinal)) > 0)
            _cachedWhitelistedUIDs = null;
        CurrentBlockStorage().Blacklist.Add(uid);
        _cachedBlacklistedUIDs = null;
        _blockConfig.Save();
    }

    internal void RemoveWhitelistUid(string uid)
    {
        if (CurrentBlockStorage().Whitelist.RemoveAll(u => u.Equals(uid, StringComparison.Ordinal)) > 0)
            _cachedWhitelistedUIDs = null;
        _blockConfig.Save();
    }

    internal void RemoveBlacklistUid(string uid)
    {
        if (CurrentBlockStorage().Blacklist.RemoveAll(u => u.Equals(uid, StringComparison.Ordinal)) > 0)
            _cachedBlacklistedUIDs = null;
        _blockConfig.Save();
    }

    private ServerNotesStorage CurrentNotesStorage()
    {
        TryCreateCurrentNotesStorage();
        return _notesConfig.Current.ServerNotes[CurrentApiUrl];
    }

    private ServerTagStorage CurrentServerTagStorage()
    {
        TryCreateCurrentServerTagStorage();
        return _serverTagConfig.Current.ServerTagStorage[CurrentApiUrl];
    }

    private ServerShellStorage CurrentSyncshellStorage()
    {
        TryCreateCurrentSyncshellStorage();
        return _syncshellConfig.Current.ServerShellStorage[CurrentApiUrl];
    }

    private ServerBlockStorage CurrentBlockStorage()
    {
        TryCreateCurrentBlockStorage();
        return _blockConfig.Current.ServerBlocks[CurrentApiUrl];
    }

    private void EnsureMainExists()
    {
        bool elfExists = false;
        for (int i = 0; i < _configService.Current.ServerStorage.Count; ++i)
        {
            var x = _configService.Current.ServerStorage[i];
            if (x.ServerUri.Equals(ApiController.SnowcloakServiceUri, StringComparison.OrdinalIgnoreCase))
                elfExists = true;
        }
        if (!elfExists)
        {
            _logger.LogDebug("Re-adding missing server {uri}", ApiController.SnowcloakServiceUri);
            _configService.Current.ServerStorage.Insert(0, new ServerStorage() { ServerUri = ApiController.SnowcloakServiceUri, ServerName = ApiController.SnowcloakServer });
            if (_configService.Current.CurrentServer >= 0)
                _configService.Current.CurrentServer++;
        }
        Save();
    }

    private void TryCreateCurrentNotesStorage()
    {
        if (!_notesConfig.Current.ServerNotes.ContainsKey(CurrentApiUrl))
        {
            _notesConfig.Current.ServerNotes[CurrentApiUrl] = new();
        }
    }

    private void TryCreateCurrentServerTagStorage()
    {
        if (!_serverTagConfig.Current.ServerTagStorage.ContainsKey(CurrentApiUrl))
        {
            _serverTagConfig.Current.ServerTagStorage[CurrentApiUrl] = new();
        }
    }

    private void TryCreateCurrentSyncshellStorage()
    {
        if (!_syncshellConfig.Current.ServerShellStorage.ContainsKey(CurrentApiUrl))
        {
            _syncshellConfig.Current.ServerShellStorage[CurrentApiUrl] = new();
        }
    }

    private void TryCreateCurrentBlockStorage()
    {
        if (!_blockConfig.Current.ServerBlocks.ContainsKey(CurrentApiUrl))
        {
            _blockConfig.Current.ServerBlocks[CurrentApiUrl] = new();
        }
    }
}