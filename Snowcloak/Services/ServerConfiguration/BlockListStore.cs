using Snowcloak.API.Data;
using Snowcloak.Configuration;

namespace Snowcloak.Services.ServerConfiguration;

public sealed class BlockListStore
{
    private readonly ServerBlockConfigService _blockConfig;
    private readonly ServerRegistry _serverRegistry;
    private HashSet<string>? _cachedBlacklistedUids;
    private string? _cachedForApiUrl;
    private HashSet<string>? _cachedWhitelistedUids;

    public BlockListStore(ServerRegistry serverRegistry, ServerBlockConfigService blockConfig)
    {
        _serverRegistry = serverRegistry;
        _blockConfig = blockConfig;
    }

    public IReadOnlyList<string> Blacklist => CurrentBlockStorage().Blacklist;

    public IReadOnlyList<string> Whitelist => CurrentBlockStorage().Whitelist;

    public void AddBlacklistUid(string uid)
    {
        RefreshCacheScope();
        if (IsUidBlacklisted(uid))
        {
            return;
        }

        if (CurrentBlockStorage().Whitelist.RemoveAll(u => u.Equals(uid, StringComparison.Ordinal)) > 0)
        {
            _cachedWhitelistedUids = null;
        }

        CurrentBlockStorage().Blacklist.Add(uid);
        _cachedBlacklistedUids = null;
        Save();
    }

    public void AddBlacklistUser(UserData userData)
    {
        RefreshCacheScope();
        if (IsUserBlacklisted(userData))
        {
            return;
        }

        if (RemoveMatchingUserEntries(CurrentBlockStorage().Whitelist, userData) > 0)
        {
            _cachedWhitelistedUids = null;
        }

        CurrentBlockStorage().Blacklist.Add(userData.UID);
        _cachedBlacklistedUids = null;
        Save();
    }

    public void AddWhitelistUid(string uid)
    {
        RefreshCacheScope();
        if (IsUidWhitelisted(uid))
        {
            return;
        }

        if (CurrentBlockStorage().Blacklist.RemoveAll(u => u.Equals(uid, StringComparison.Ordinal)) > 0)
        {
            _cachedBlacklistedUids = null;
        }

        CurrentBlockStorage().Whitelist.Add(uid);
        _cachedWhitelistedUids = null;
        Save();
    }

    public void AddWhitelistUser(UserData userData)
    {
        RefreshCacheScope();
        if (IsUserWhitelisted(userData))
        {
            return;
        }

        if (RemoveMatchingUserEntries(CurrentBlockStorage().Blacklist, userData) > 0)
        {
            _cachedBlacklistedUids = null;
        }

        CurrentBlockStorage().Whitelist.Add(userData.UID);
        _cachedWhitelistedUids = null;
        Save();
    }

    public bool IsUidBlacklisted(string uid)
    {
        RefreshCacheScope();
        _cachedBlacklistedUids ??= [.. CurrentBlockStorage().Blacklist];
        return _cachedBlacklistedUids.Contains(uid);
    }

    public bool IsUidWhitelisted(string uid)
    {
        RefreshCacheScope();
        _cachedWhitelistedUids ??= [.. CurrentBlockStorage().Whitelist];
        return _cachedWhitelistedUids.Contains(uid);
    }

    public bool IsUserBlacklisted(UserData userData)
    {
        RefreshCacheScope();
        _cachedBlacklistedUids ??= [.. CurrentBlockStorage().Blacklist];
        return IsUserInBlockList(userData, _cachedBlacklistedUids);
    }

    public bool IsUserWhitelisted(UserData userData)
    {
        RefreshCacheScope();
        _cachedWhitelistedUids ??= [.. CurrentBlockStorage().Whitelist];
        return IsUserInBlockList(userData, _cachedWhitelistedUids);
    }

    public void RemoveBlacklistUid(string uid)
    {
        RefreshCacheScope();
        if (CurrentBlockStorage().Blacklist.RemoveAll(u => u.Equals(uid, StringComparison.Ordinal)) > 0)
        {
            _cachedBlacklistedUids = null;
        }

        Save();
    }

    public void RemoveBlacklistUser(UserData userData)
    {
        RefreshCacheScope();
        if (RemoveMatchingUserEntries(CurrentBlockStorage().Blacklist, userData) > 0)
        {
            _cachedBlacklistedUids = null;
        }

        Save();
    }

    public void RemoveWhitelistUid(string uid)
    {
        RefreshCacheScope();
        if (CurrentBlockStorage().Whitelist.RemoveAll(u => u.Equals(uid, StringComparison.Ordinal)) > 0)
        {
            _cachedWhitelistedUids = null;
        }

        Save();
    }

    public void RemoveWhitelistUser(UserData userData)
    {
        RefreshCacheScope();
        if (RemoveMatchingUserEntries(CurrentBlockStorage().Whitelist, userData) > 0)
        {
            _cachedWhitelistedUids = null;
        }

        Save();
    }

    private static IEnumerable<string> GetUserBlockListIdentifiers(UserData userData)
    {
        if (!string.IsNullOrWhiteSpace(userData.UID))
        {
            yield return userData.UID;
        }

        if (!string.IsNullOrWhiteSpace(userData.Alias) && !string.Equals(userData.Alias, userData.UID, StringComparison.Ordinal))
        {
            yield return userData.Alias;
        }
    }

    private static bool IsUserInBlockList(UserData userData, HashSet<string> blockList)
    {
        return GetUserBlockListIdentifiers(userData).Any(blockList.Contains);
    }

    private static int RemoveMatchingUserEntries(List<string> entries, UserData userData)
    {
        var identifiers = GetUserBlockListIdentifiers(userData).ToHashSet(StringComparer.Ordinal);
        return entries.RemoveAll(identifiers.Contains);
    }

    private Configuration.Models.ServerBlockStorage CurrentBlockStorage()
    {
        if (!_blockConfig.Current.ServerBlocks.ContainsKey(_serverRegistry.CurrentApiUrl))
        {
            _blockConfig.Current.ServerBlocks[_serverRegistry.CurrentApiUrl] = new Configuration.Models.ServerBlockStorage();
        }

        return _blockConfig.Current.ServerBlocks[_serverRegistry.CurrentApiUrl];
    }

    private void RefreshCacheScope()
    {
        if (string.Equals(_cachedForApiUrl, _serverRegistry.CurrentApiUrl, StringComparison.Ordinal))
        {
            return;
        }

        _cachedForApiUrl = _serverRegistry.CurrentApiUrl;
        _cachedBlacklistedUids = null;
        _cachedWhitelistedUids = null;
    }

    private void Save()
    {
        _blockConfig.Update(_ => { });
    }
}
