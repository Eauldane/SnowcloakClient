using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.WebAPI;
using System.Diagnostics;

namespace Snowcloak.Services.ServerConfiguration;

public sealed class ServerRegistry
{
    private readonly ServerConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ILogger<ServerRegistry> _logger;
    private string? _realApiUrl;

    public ServerRegistry(ILogger<ServerRegistry> logger, ServerConfigService configService, DalamudUtilService dalamudUtil)
    {
        _logger = logger;
        _configService = configService;
        _dalamudUtil = dalamudUtil;

        EnsureMainExists();
    }

    public string CurrentApiUrl => CurrentServer.ServerUri;

    public string CurrentRealApiUrl => _realApiUrl ?? CurrentApiUrl;

    public ServerStorage CurrentServer => _configService.Current.ServerStorage[CurrentServerIndex];

    public int CurrentServerIndex
    {
        get
        {
            if (_configService.Current.CurrentServer < 0)
            {
                _configService.Update(c => c.CurrentServer = 0);
            }

            return _configService.Current.CurrentServer;
        }
        set
        {
            _configService.Current.CurrentServer = value;
            _realApiUrl = null;
            Save();
        }
    }

    public void AddCurrentCharacterToServer(int serverSelectionIndex = -1, int? secretKeyIdx = null, bool save = true)
    {
        if (serverSelectionIndex == -1)
        {
            serverSelectionIndex = CurrentServerIndex;
        }

        var server = GetServerByIndex(serverSelectionIndex);
        if (!server.SecretKeys.Any())
        {
            return;
        }

        var characterName = _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult();
        var worldId = _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult();
        if (server.Authentications.Any(c => string.Equals(c.CharacterName, characterName, StringComparison.Ordinal) && c.WorldId == worldId))
        {
            return;
        }

        server.Authentications.Add(new Authentication
        {
            CharacterName = characterName,
            WorldId = worldId,
            SecretKeyIdx = secretKeyIdx ?? server.SecretKeys.Last().Key
        });

        if (save)
        {
            Save();
        }
    }

    public void AddEmptyCharacterToServer(int serverSelectionIndex)
    {
        var server = GetServerByIndex(serverSelectionIndex);
        server.Authentications.Add(new Authentication
        {
            SecretKeyIdx = server.SecretKeys.Any() ? server.SecretKeys.First().Key : -1
        });
        Save();
    }

    public void AddServer(ServerStorage serverStorage)
    {
        _configService.Current.ServerStorage.Add(serverStorage);
        Save();
    }

    public void DeleteServer(ServerStorage selectedServer)
    {
        if (Array.IndexOf(_configService.Current.ServerStorage.ToArray(), selectedServer) < _configService.Current.CurrentServer)
        {
            _configService.Current.CurrentServer--;
        }

        _configService.Current.ServerStorage.Remove(selectedServer);
        Save();
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
            return CurrentServer;
        }
    }

    public string[] GetServerApiUrls()
    {
        return _configService.Current.ServerStorage.Select(v => v.ServerUri).ToArray();
    }

    public string[] GetServerNames()
    {
        return _configService.Current.ServerStorage.Select(v => v.ServerName).ToArray();
    }

    public string? GetSecretKey(out bool hasMulti, int serverIdx = -1)
    {
        var currentServer = serverIdx == -1 ? CurrentServer : GetServerByIndex(serverIdx);
        hasMulti = false;

        if (currentServer == null)
        {
            currentServer = new ServerStorage();
            Save();
        }

        var charaName = _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult();
        var worldId = _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult();
        if (!currentServer.Authentications.Any() && currentServer.SecretKeys.Any())
        {
            currentServer.Authentications.Add(new Authentication
            {
                CharacterName = charaName,
                WorldId = worldId,
                SecretKeyIdx = currentServer.SecretKeys.Last().Key
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

    public bool HasCurrentCharacterAssignment(int serverSelectionIndex = -1)
    {
        if (serverSelectionIndex == -1)
        {
            serverSelectionIndex = CurrentServerIndex;
        }

        var server = GetServerByIndex(serverSelectionIndex);
        var playerName = _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult();
        var worldId = _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult();
        return server.Authentications.Any(c => string.Equals(c.CharacterName, playerName, StringComparison.Ordinal) && c.WorldId == worldId);
    }

    public bool HasValidConfig()
    {
        return CurrentServer != null && CurrentServer.SecretKeys.Any();
    }

    public void RemoveCharacterFromServer(int serverSelectionIndex, Authentication item)
    {
        var server = GetServerByIndex(serverSelectionIndex);
        server.Authentications.Remove(item);
        Save();
    }

    public void Save()
    {
        var caller = new StackTrace().GetFrame(1)?.GetMethod()?.ReflectedType?.Name ?? "Unknown";
        _logger.LogDebug("{caller} Calling config save", caller);
        _configService.Update(_ => { });
    }

    public void SelectServer(int idx)
    {
        _configService.Current.CurrentServer = idx;
        CurrentServer.FullPause = false;
        Save();
    }

    private void EnsureMainExists()
    {
        var serverExists = _configService.Current.ServerStorage.Any(x => x.ServerUri.Equals(ApiController.SnowcloakServiceUri, StringComparison.OrdinalIgnoreCase));
        if (!serverExists)
        {
            _logger.LogDebug("Re-adding missing server {uri}", ApiController.SnowcloakServiceUri);
            _configService.Current.ServerStorage.Insert(0, new ServerStorage { ServerUri = ApiController.SnowcloakServiceUri, ServerName = ApiController.SnowcloakServer });
            if (_configService.Current.CurrentServer >= 0)
            {
                _configService.Current.CurrentServer++;
            }
        }

        Save();
    }
}
