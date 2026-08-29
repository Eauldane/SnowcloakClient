using Snowcloak.API.Protocol;
﻿using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.WebAPI;
using System.Diagnostics;
using Snowcloak.Core.PlayerData;

namespace Snowcloak.Services.ServerConfiguration;

public sealed class ServerRegistry
{
    private readonly ServerConfigService _configService;
    private readonly CharacterIdentityConfigService _identityConfig;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ILogger<ServerRegistry> _logger;
    private string? _realApiUrl;

    public ServerRegistry(ILogger<ServerRegistry> logger, ServerConfigService configService, DalamudUtilService dalamudUtil,
        CharacterIdentityConfigService identityConfig)
    {
        _logger = logger;
        _configService = configService;
        _identityConfig = identityConfig;
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

        var character = GetCurrentCharacterIdentity();
        if (!character.IsValid)
            return;
        var assignment = GetCharacterAssignment(server, character, out var ambiguous);
        if (ambiguous)
            return;
        if (assignment == null)
            AssignCharacterToSecretKey(server, character, secretKeyIdx ?? server.SecretKeys.Last().Key, save: false);
        else if (secretKeyIdx.HasValue)
            assignment.SecretKeyIdx = secretKeyIdx.Value;
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

        var character = GetCurrentCharacterIdentity();
        if (!character.IsValid)
            return null;
        if (!currentServer.Authentications.Any() && currentServer.SecretKeys.Any())
        {
            AssignCharacterToSecretKey(currentServer, character, currentServer.SecretKeys.Last().Key);
        }

        var auth = GetCharacterAssignment(currentServer, character, out hasMulti);
        if (hasMulti)
        {
            _logger.LogTrace("Secret key selection rejected ambiguous character assignments.");
            return null;
        }

        if (auth == null)
        {
            _logger.LogTrace("No assignment exists for the current character.");
            return null;
        }

        if (currentServer.SecretKeys.TryGetValue(auth.SecretKeyIdx, out var secretKey))
        {
            _logger.LogTrace("Selected the current character's assigned secret key.");
            return secretKey.Key;
        }

        _logger.LogTrace("The current character's assignment references a missing secret key.");
        return null;
    }

    public bool HasCurrentCharacterAssignment(int serverSelectionIndex = -1)
    {
        if (serverSelectionIndex == -1)
        {
            serverSelectionIndex = CurrentServerIndex;
        }

        var server = GetServerByIndex(serverSelectionIndex);
        return GetCharacterAssignment(server, GetCurrentCharacterIdentity(), out _) != null;
    }

    public CharacterIdentity GetCurrentCharacterIdentity()
        => _dalamudUtil.GetCurrentCharacterIdentityAsync().GetAwaiter().GetResult();

    public Authentication? GetCurrentCharacterAssignment(ServerStorage server)
        => GetCharacterAssignment(server, GetCurrentCharacterIdentity(), out _);

    public Authentication? GetCharacterAssignment(ServerStorage server, CharacterIdentity character, out bool ambiguous)
    {
        ambiguous = false;
        if (!character.IsValid)
            return null;

        var restored = false;
        if (_identityConfig.Current.Servers.TryGetValue(server.ServerUri, out var remembered))
        {
            foreach (var legacy in server.Authentications.Where(a => a.ContentId == 0))
            {
                var identities = remembered.Where(i => i.IsValid && CharacterAssignmentResolver.MatchesLegacy(legacy, i))
                    .Select(i => i.ContentId).Distinct().Take(2).ToArray();
                if (identities.Length == 1)
                {
                    legacy.ContentId = identities[0];
                    restored = true;
                }
                else if (identities.Length > 1 && CharacterAssignmentResolver.MatchesLegacy(legacy, character))
                {
                    ambiguous = true;
                    return null;
                }
            }
        }
        var assignment = CharacterAssignmentResolver.Resolve(server.Authentications, character, out ambiguous);
        if (assignment != null)
        {
            if (_identityConfig.Current.CompletedBindings.TryGetValue(server.ServerUri, out var bindings)
                && bindings.TryGetValue(character.ContentId, out var bindingId))
                assignment.IdentityBindingId = bindingId;
            if (!assignment.IdentityBindingId.HasValue && assignment.PendingLegacyIdents.Count == 0)
            {
                assignment.PendingLegacyIdents.Add(CharacterIdentityProtocol.LegacyIdent(assignment.CharacterName, assignment.WorldId));
                restored = true;
            }
        }

        if (assignment != null && _identityConfig.Current.PendingLegacyIdents.TryGetValue(server.ServerUri, out var pending)
            && pending.TryGetValue(character.ContentId, out var aliases))
            assignment.PendingLegacyIdents = assignment.PendingLegacyIdents.Concat(aliases).Distinct(StringComparer.Ordinal).ToList();

        if ((assignment != null && CharacterAssignmentResolver.UpdateIdentity(assignment, character)) || restored)
            Save();
        return assignment;
    }

    public void AssignCharacterToSecretKey(ServerStorage server, CharacterIdentity character, int secretKeyIdx, bool save = true)
    {
        if (!character.IsValid || !server.SecretKeys.ContainsKey(secretKeyIdx))
            throw new InvalidOperationException("A loaded character and an existing secret key are required for assignment.");

        var assignment = GetCharacterAssignment(server, character, out var ambiguous);
        if (ambiguous)
            throw new InvalidOperationException("Remove duplicate character assignments before selecting a secret key.");
        if (assignment == null)
        {
            assignment = new Authentication();
            CharacterAssignmentResolver.UpdateIdentity(assignment, character);
            server.Authentications.Add(assignment);
        }
        assignment.SecretKeyIdx = secretKeyIdx;
        if (save)
            Save();
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
        RememberCharacterIdentities();
    }

    private void RememberCharacterIdentities()
    {
        foreach (var server in _configService.Current.ServerStorage)
        {
            var known = _identityConfig.Current.Servers.GetValueOrDefault(server.ServerUri) ?? [];
            var pending = _identityConfig.Current.PendingLegacyIdents.GetValueOrDefault(server.ServerUri) ?? [];
            var updatedPending = pending.ToDictionary(p => p.Key, p => p.Value.ToList());
            var updated = known.ToList();
            foreach (var group in server.Authentications.Where(a => a.ContentId != 0).GroupBy(a => a.ContentId))
            {
                if (group.Count() != 1)
                    continue;
                var assignment = group.Single();
                var identity = new CharacterIdentity(assignment.ContentId, assignment.CharacterName, assignment.WorldId);
                if (!identity.IsValid)
                    continue;
                updated.RemoveAll(i => i.ContentId == identity.ContentId);
                updated.Add(identity);
                updatedPending[identity.ContentId] = assignment.PendingLegacyIdents.ToList();
            }
            updated.Sort((a, b) => a.ContentId.CompareTo(b.ContentId));
            if (!updated.SequenceEqual(known) || updatedPending.Count != pending.Count
                || updatedPending.Any(p => !pending.TryGetValue(p.Key, out var old) || !p.Value.SequenceEqual(old)))
                _identityConfig.Update(c =>
                {
                    c.Servers[server.ServerUri] = updated;
                    c.PendingLegacyIdents[server.ServerUri] = updatedPending;
                });
        }
    }

    public string[] GetPendingIdentityAliases(string apiUrl, ulong contentId, string secretKey)
    {
        var server = _configService.Current.ServerStorage.SingleOrDefault(s => s.ServerUri == apiUrl);
        var assignment = server?.Authentications.SingleOrDefault(a => a.ContentId == contentId);
        if (assignment == null || !server!.SecretKeys.TryGetValue(assignment.SecretKeyIdx, out var key) || key.Key != secretKey)
            throw new InvalidOperationException("The character key assignment changed during authentication.");
        return assignment.PendingLegacyIdents.ToArray();
    }

    public void AcknowledgeIdentityAliases(string apiUrl, ulong contentId, string secretKey, Guid bindingId, string[] aliases)
    {
        var server = _configService.Current.ServerStorage.SingleOrDefault(s => s.ServerUri == apiUrl);
        var assignment = server?.Authentications.SingleOrDefault(a => a.ContentId == contentId);
        if (assignment == null || !server!.SecretKeys.TryGetValue(assignment.SecretKeyIdx, out var key) || key.Key != secretKey)
            return;
        assignment.PendingLegacyIdents.RemoveAll(aliases.Contains);
        assignment.IdentityBindingId = bindingId;
        _identityConfig.Update(c =>
        {
            if (!c.CompletedBindings.TryGetValue(apiUrl, out var bindings))
                c.CompletedBindings[apiUrl] = bindings = [];
            bindings[contentId] = bindingId;
        });
        if (_identityConfig.Current.PendingLegacyIdents.TryGetValue(apiUrl, out var pending))
            _identityConfig.Update(_ => pending[contentId] = assignment.PendingLegacyIdents.ToList());
        Save();
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
