using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Utility;
using ElezenTools.Core.Async;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Account;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI;
using Snowcloak.Core.PlayerData;

namespace Snowcloak.UI.Components.Account;

public sealed class CharacterKeyAssignmentFlow
{
    private readonly ILogger _logger;
    private readonly ServerRegistry _serverRegistry;
    private readonly ApiController _apiController;
    private readonly AsyncOp<RegisterReplyDto> _operation = new();

    private ServerStorage? _server;
    private CharacterIdentity _character;
    private bool _removeInvalidSecretKey;
    private int? _invalidSecretKeyIdx;
    private string _successMessage = string.Empty;
    private string _failureLogMessage = "Registration failed";
    private string? _message;
    private bool _success;

    public CharacterKeyAssignmentFlow(ILogger logger, ServerRegistry serverRegistry, ApiController apiController)
    {
        _logger = logger;
        _serverRegistry = serverRegistry;
        _apiController = apiController;
    }

    public bool IsRunning => _operation.IsRunning;

    public void Begin(ServerStorage server, string playerName, uint worldId, bool removeInvalidSecretKey,
        int? invalidSecretKeyIdx, Func<CancellationToken, Task<RegisterReplyDto>> registrationFunc,
        string successMessage, string failureLogMessage)
    {
        _server = server;
        _character = _serverRegistry.GetCurrentCharacterIdentity();
        if (!_character.IsValid
            || !string.Equals(_character.Name, playerName, StringComparison.Ordinal)
            || _character.HomeWorldId != worldId)
            throw new InvalidOperationException("The character changed before key registration started.");
        _removeInvalidSecretKey = removeInvalidSecretKey;
        _invalidSecretKeyIdx = invalidSecretKeyIdx;
        _successMessage = successMessage;
        _failureLogMessage = failureLogMessage;
        _message = null;
        _success = false;
        _ = _operation.Run(() => registrationFunc(CancellationToken.None));
    }

    public void DrawStatus()
    {
        ConsumeOperation();

        if (IsRunning)
        {
            ImGui.TextUnformatted("Waiting for the server...");
        }
        else if (!_message.IsNullOrEmpty())
        {
            if (!_success)
                ImGui.TextColored(ImGuiColors.DalamudYellow, _message);
            else
                ImGui.TextWrapped(_message);
        }
    }

    private void ConsumeOperation()
    {
        if (!_operation.IsCompleted)
            return;

        if (_operation.Faulted)
        {
            _logger.LogWarning("{msg}: {err}", _failureLogMessage, _operation.Error);
            _success = false;
            _message = "An unknown error occured. Please try again later.";
        }
        else
        {
            var reply = _operation.Result;
            if (reply is { Success: true } && _server != null)
            {
                try
                {
                    AssignRegisteredKeyToCurrentCharacter(_server, _character, reply, _removeInvalidSecretKey, _invalidSecretKeyIdx);
                    _serverRegistry.Save();
                    _ = _apiController.CreateConnections();
                    _success = true;
                    _message = _successMessage;
                }
                catch (InvalidOperationException)
                {
                    _success = false;
                    _message = "The new key was saved, but the character assignment is ambiguous. Remove duplicate assignments and select the saved key.";
                }
            }
            else
            {
                _logger.LogWarning("{msg}: {err}", _failureLogMessage, reply?.ErrorMessage);
                _success = false;
                _message = reply?.ErrorMessage.IsNullOrEmpty() == false
                    ? reply.ErrorMessage
                    : "An unknown error occured. Please try again later.";
            }
        }

        _operation.Reset();
    }

    private void AssignRegisteredKeyToCurrentCharacter(ServerStorage server, CharacterIdentity character,
        RegisterReplyDto reply, bool removeInvalidSecretKey, int? invalidSecretKeyIdx)
    {
        var newSecretKeyIdx = server.SecretKeys.Any() ? server.SecretKeys.Max(p => p.Key) + 1 : 0;
        server.SecretKeys.Add(newSecretKeyIdx, new SecretKey()
        {
            FriendlyName = string.Format(CultureInfo.InvariantCulture, "{0} {1}", reply.UID,
                string.Format(CultureInfo.InvariantCulture, "(registered {0:yyyy-MM-dd})", DateTime.Now)),
            Key = reply.SecretKey ?? string.Empty
        });
        // Preserve the generated credential even if assigning it is rejected.
        _serverRegistry.Save();

        if (removeInvalidSecretKey && invalidSecretKeyIdx.HasValue)
        {
            foreach (var auth in server.Authentications.Where(a => a.SecretKeyIdx == invalidSecretKeyIdx.Value).ToList())
            {
                auth.SecretKeyIdx = newSecretKeyIdx;
            }
            server.SecretKeys.Remove(invalidSecretKeyIdx.Value);
        }
        _serverRegistry.AssignCharacterToSecretKey(server, character, newSecretKeyIdx, save: false);
    }
}
