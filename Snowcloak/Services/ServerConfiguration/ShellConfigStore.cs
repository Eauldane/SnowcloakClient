using Snowcloak.API.Data;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;

namespace Snowcloak.Services.ServerConfiguration;

public sealed class ShellConfigStore
{
    private readonly ServerRegistry _serverRegistry;
    private readonly SyncshellConfigService _syncshellConfig;

    public ShellConfigStore(ServerRegistry serverRegistry, SyncshellConfigService syncshellConfig)
    {
        _serverRegistry = serverRegistry;
        _syncshellConfig = syncshellConfig;
    }

    public IReadOnlyList<ChatChannelData> GetJoinedStandardChannels()
    {
        return _serverRegistry.CurrentServer.JoinedChannels;
    }

    public ShellConfig GetShellConfigForGid(string gid)
    {
        if (CurrentSyncshellStorage().GidShellConfig.TryGetValue(gid, out var config))
        {
            return config;
        }

        int newShellNumber = CurrentSyncshellStorage().GidShellConfig.Count > 0
            ? CurrentSyncshellStorage().GidShellConfig.Select(x => x.Value.ShellNumber).Max() + 1
            : 1;

        var shellConfig = new ShellConfig
        {
            ShellNumber = newShellNumber
        };

        SaveShellConfigForGid(gid, shellConfig);
        return CurrentSyncshellStorage().GidShellConfig[gid];
    }

    public bool HasShellConfigForGid(string gid)
    {
        if (string.IsNullOrEmpty(gid))
        {
            return false;
        }

        return CurrentSyncshellStorage().GidShellConfig.ContainsKey(gid);
    }

    public void RemoveJoinedStandardChannel(string channelId)
    {
        _serverRegistry.CurrentServer.JoinedChannels.RemoveAll(channel => string.Equals(channel.ChannelId, channelId, StringComparison.Ordinal));
        _serverRegistry.Save();
    }

    public void SaveShellConfigForGid(string gid, ShellConfig config)
    {
        if (string.IsNullOrEmpty(gid))
        {
            return;
        }

        CurrentSyncshellStorage().GidShellConfig[gid] = config;
        _syncshellConfig.Update(_ => { });
    }

    public void UpsertJoinedStandardChannel(ChatChannelData channel)
    {
        var channels = _serverRegistry.CurrentServer.JoinedChannels;
        var existing = channels.FindIndex(entry => string.Equals(entry.ChannelId, channel.ChannelId, StringComparison.Ordinal));
        if (existing >= 0)
        {
            channels[existing] = channel;
        }
        else
        {
            channels.Add(channel);
        }

        _serverRegistry.Save();
    }

    private ServerShellStorage CurrentSyncshellStorage()
    {
        if (!_syncshellConfig.Current.ServerShellStorage.ContainsKey(_serverRegistry.CurrentApiUrl))
        {
            _syncshellConfig.Current.ServerShellStorage[_serverRegistry.CurrentApiUrl] = new ServerShellStorage();
        }

        return _syncshellConfig.Current.ServerShellStorage[_serverRegistry.CurrentApiUrl];
    }
}
