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

namespace Snowcloak.UI.Components.Account;

/// <summary>
/// Registers a fresh secret key (XIVAuth or standalone) and assigns it to the current character,
/// optionally replacing an invalid key. Used by the Settings secret-key tab (P30). Encapsulates the
/// in-flight state and the key-assignment bookkeeping that previously lived as SettingsUi fields.
/// </summary>
public sealed class CharacterKeyAssignmentFlow
{
    private readonly ILogger _logger;
    private readonly ServerRegistry _serverRegistry;
    private readonly ApiController _apiController;
    private readonly AsyncOp<RegisterReplyDto> _operation = new();

    private ServerStorage? _server;
    private string _playerName = string.Empty;
    private uint _worldId;
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
        _playerName = playerName;
        _worldId = worldId;
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
                AssignRegisteredKeyToCurrentCharacter(_server, _playerName, _worldId, reply, _removeInvalidSecretKey, _invalidSecretKeyIdx);
                _serverRegistry.Save();
                _ = _apiController.CreateConnections();
                _success = true;
                _message = _successMessage;
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

    private static void AssignRegisteredKeyToCurrentCharacter(ServerStorage server, string currentPlayerName, uint currentPlayerWorldId,
        RegisterReplyDto reply, bool removeInvalidSecretKey, int? invalidSecretKeyIdx)
    {
        var assignedCharacter = server.Authentications.Find(a =>
            string.Equals(a.CharacterName, currentPlayerName, StringComparison.OrdinalIgnoreCase)
            && a.WorldId == currentPlayerWorldId);

        var newSecretKeyIdx = server.SecretKeys.Any() ? server.SecretKeys.Max(p => p.Key) + 1 : 0;
        server.SecretKeys.Add(newSecretKeyIdx, new SecretKey()
        {
            FriendlyName = string.Format(CultureInfo.InvariantCulture, "{0} {1}", reply.UID,
                string.Format(CultureInfo.InvariantCulture, "(registered {0:yyyy-MM-dd})", DateTime.Now)),
            Key = reply.SecretKey ?? string.Empty
        });

        if (removeInvalidSecretKey && invalidSecretKeyIdx.HasValue)
        {
            foreach (var auth in server.Authentications.Where(a => a.SecretKeyIdx == invalidSecretKeyIdx.Value).ToList())
            {
                auth.SecretKeyIdx = newSecretKeyIdx;
            }
            server.SecretKeys.Remove(invalidSecretKeyIdx.Value);
        }
        else if (assignedCharacter == null)
        {
            server.Authentications.Add(new Authentication()
            {
                CharacterName = currentPlayerName,
                WorldId = currentPlayerWorldId,
                SecretKeyIdx = newSecretKeyIdx
            });
        }
        else
        {
            assignedCharacter.SecretKeyIdx = newSecretKeyIdx;
        }
    }
}
