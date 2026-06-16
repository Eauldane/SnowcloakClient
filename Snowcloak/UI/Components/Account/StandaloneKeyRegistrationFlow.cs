using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Utility;
using ElezenTools.Core.Async;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Account;

namespace Snowcloak.UI.Components.Account;

/// <summary>
/// Wraps a single "register and obtain a secret key" call (XIVAuth or a standalone key) over
/// <see cref="AsyncOp{T}"/>. The surface decides what to do with the resulting reply via the
/// <c>onSuccess</c> callback (populate the onboarding key field, or add the key to a server).
/// Replaces the duplicated <c>_registration*</c> window fields (P30).
/// </summary>
public sealed class StandaloneKeyRegistrationFlow
{
    private readonly ILogger _logger;
    private readonly AsyncOp<RegisterReplyDto> _operation = new();
    private string _successMessage = string.Empty;
    private string _failureLogMessage = "Registration failed";
    private Action<RegisterReplyDto>? _onSuccess;
    private string? _message;
    private bool _success;
    private RegisterReplyDto? _reply;

    public StandaloneKeyRegistrationFlow(ILogger logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _operation.IsRunning;
    public bool Succeeded => _success;
    public string? Message => _message;
    public RegisterReplyDto? Reply => _reply;

    public void Reset()
    {
        _message = null;
        _success = false;
        _reply = null;
        _onSuccess = null;
        _operation.Reset();
    }

    public void Begin(Func<CancellationToken, Task<RegisterReplyDto>> registrationFunc, string successMessage,
        Action<RegisterReplyDto> onSuccess, string failureLogMessage = "Registration failed")
    {
        _successMessage = successMessage;
        _failureLogMessage = failureLogMessage;
        _onSuccess = onSuccess;
        _message = null;
        _success = false;
        _ = _operation.Run(() => registrationFunc(CancellationToken.None));
    }

    public void DrawStatus(string runningText)
    {
        ConsumeOperation();

        if (IsRunning)
        {
            ImGui.TextUnformatted(runningText);
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
            if (reply is { Success: true })
            {
                _reply = reply;
                _success = true;
                _message = _successMessage;
                _onSuccess?.Invoke(reply);
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
}
