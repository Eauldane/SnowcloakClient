using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Utility;
using ElezenTools.Core.Async;
using Microsoft.Extensions.Logging;
using Snowcloak.WebAPI;

namespace Snowcloak.UI.Components.Account;

/// <summary>
/// Creates a server-side account UID and assigns it to the current character (account-linked
/// services). Used by the Settings secret-key tab (P30).
/// </summary>
public sealed class AccountUidGenerationFlow
{
    private readonly ILogger _logger;
    private readonly AccountRegistrationService _registerService;
    private readonly ApiController _apiController;
    private readonly AsyncOp<AccountOperationResult> _operation = new();
    private string? _message;
    private bool _success;

    public AccountUidGenerationFlow(ILogger logger, AccountRegistrationService registerService, ApiController apiController)
    {
        _logger = logger;
        _registerService = registerService;
        _apiController = apiController;
    }

    public bool IsRunning => _operation.IsRunning;

    public void Begin()
    {
        _message = null;
        _success = false;
        _ = _operation.Run(() => _registerService.CreateAccountUid(CancellationToken.None));
    }

    public void DrawStatus()
    {
        ConsumeOperation();

        if (IsRunning)
        {
            ImGui.TextUnformatted("Creating account UID...");
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
            _logger.LogWarning("Account UID creation failed: {err}", _operation.Error);
            _success = false;
            _message = "Account UID creation failed. Please try again later.";
        }
        else
        {
            var result = _operation.Result;
            if (result is { Success: true })
            {
                _success = true;
                _message = string.Format(CultureInfo.InvariantCulture,
                    "Created account UID {0}. Stored {1} account key(s), including {2} new key(s), and assigned this character. Attempting to connect.",
                    result.Uid, result.SecretKeyCount, result.NewSecretKeyCount);
                _ = _apiController.CreateConnections();
            }
            else
            {
                _success = false;
                _message = result?.ErrorMessage.IsNullOrEmpty() == false
                    ? result.ErrorMessage
                    : "Account UID creation failed. Please try again later.";
            }
        }

        _operation.Reset();
    }
}
