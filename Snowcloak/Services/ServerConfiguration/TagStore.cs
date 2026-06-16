using Snowcloak.Configuration;

namespace Snowcloak.Services.ServerConfiguration;

public sealed class TagStore
{
    private readonly ServerRegistry _serverRegistry;
    private readonly ServerTagConfigService _serverTagConfig;

    public TagStore(ServerRegistry serverRegistry, ServerTagConfigService serverTagConfig)
    {
        _serverRegistry = serverRegistry;
        _serverTagConfig = serverTagConfig;
    }

    public void AddOpenPairTag(string tag)
    {
        CurrentServerTagStorage().OpenPairTags.Add(tag);
        Save();
    }

    public void AddTag(string tag)
    {
        CurrentServerTagStorage().ServerAvailablePairTags.Add(tag);
        Save();
    }

    public void AddTagForUid(string uid, string tagName)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            tags.Add(tagName);
        }
        else
        {
            CurrentServerTagStorage().UidServerPairedUserTags[uid] = [tagName];
        }

        Save();
    }

    public bool ContainsOpenPairTag(string tag)
    {
        return CurrentServerTagStorage().OpenPairTags.Contains(tag);
    }

    public bool ContainsTag(string uid, string tag)
    {
        return CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags) && tags.Contains(tag, StringComparer.Ordinal);
    }

    public HashSet<string> GetServerAvailablePairTags()
    {
        return CurrentServerTagStorage().ServerAvailablePairTags;
    }

    public Dictionary<string, List<string>> GetUidServerPairedUserTags()
    {
        return CurrentServerTagStorage().UidServerPairedUserTags;
    }

    public HashSet<string> GetUidsForTag(string tag)
    {
        return CurrentServerTagStorage()
            .UidServerPairedUserTags
            .Where(p => p.Value.Contains(tag, StringComparer.Ordinal))
            .Select(p => p.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    public bool HasTags(string uid)
    {
        return CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags) && tags.Any();
    }

    public void RemoveOpenPairTag(string tag)
    {
        CurrentServerTagStorage().OpenPairTags.Remove(tag);
        Save();
    }

    public void RemoveTag(string tag)
    {
        CurrentServerTagStorage().ServerAvailablePairTags.Remove(tag);
        foreach (var uid in GetUidsForTag(tag))
        {
            RemoveTagForUid(uid, tag, save: false);
        }

        Save();
    }

    public void RemoveTagForUid(string uid, string tagName, bool save = true)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            tags.Remove(tagName);

            if (save)
            {
                Save();
            }
        }
    }

    public void RenameTag(string oldName, string newName)
    {
        CurrentServerTagStorage().ServerAvailablePairTags.Remove(oldName);
        CurrentServerTagStorage().ServerAvailablePairTags.Add(newName);
        foreach (var existingTags in CurrentServerTagStorage().UidServerPairedUserTags.Select(k => k.Value))
        {
            if (existingTags.Remove(oldName))
            {
                existingTags.Add(newName);
            }
        }

        Save();
    }

    private void Save()
    {
        _serverTagConfig.Update(_ => { });
    }

    private Configuration.Models.ServerTagStorage CurrentServerTagStorage()
    {
        if (!_serverTagConfig.Current.ServerTagStorage.ContainsKey(_serverRegistry.CurrentApiUrl))
        {
            _serverTagConfig.Current.ServerTagStorage[_serverRegistry.CurrentApiUrl] = new Configuration.Models.ServerTagStorage();
        }

        return _serverTagConfig.Current.ServerTagStorage[_serverRegistry.CurrentApiUrl];
    }
}
