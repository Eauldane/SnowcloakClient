using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using ElezenTools.Core.Async;
using ElezenTools.UI;
using Snowcloak.Core.Accounts;
using System.Numerics;

namespace Snowcloak.UI.Components.Account;

public readonly record struct AccountFlowResult(bool Success, string Message);

public sealed class PasswordAccountFlowOptions
{
    public string IdPrefix { get; init; } = "account";
    public string HeaderTitle { get; init; } = string.Empty;
    public string HeaderDescription { get; init; } = string.Empty;

    public bool ShowModeToggle { get; init; } = true;

    public bool CanCreate { get; init; } = true;

    public string? CreateDisabledHelp { get; init; }
    public string? SignInDescription { get; init; }
    public string? CreateDescription { get; init; }
    public string SignInModeLabel { get; init; } = "Sign in";
    public string CreateModeLabel { get; init; } = "Create account";
    public string CreateSubmitLabel { get; init; } = "Create account";

    public string SignInRunningMessage { get; init; } = "Signing in...";
    public string CreateRunningMessage { get; init; } = "Creating account...";

    public required Func<string, string, Task<AccountFlowResult>> SignIn { get; init; }
    public required Func<string, string, Task<AccountFlowResult>> Create { get; init; }
}

public sealed class PasswordAccountFlow
{
    private readonly AsyncOp<AccountFlowResult> _operation = new();
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _passwordConfirm = string.Empty;
    private bool _showPassword;
    private AccountAuthMode _mode;
    private string? _message;
    private bool _success;
    private bool _usernameValidationReady;
    private bool _passwordValidationReady;
    private bool _passwordConfirmValidationReady;

    public bool IsRunning => _operation.IsRunning;

    public void Reset()
    {
        _username = string.Empty;
        _password = string.Empty;
        _passwordConfirm = string.Empty;
        _showPassword = false;
        _mode = AccountAuthMode.SignIn;
        _message = null;
        _success = false;
        _usernameValidationReady = false;
        _passwordValidationReady = false;
        _passwordConfirmValidationReady = false;
        _operation.Reset();
    }

    public void Draw(PasswordAccountFlowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ConsumeOperation();

        if (!options.ShowModeToggle)
            _mode = AccountAuthMode.SignIn;
        else if (!options.CanCreate && _mode == AccountAuthMode.CreateAccount)
            SetMode(AccountAuthMode.SignIn);

        AccountCredentialUi.DrawHeader(options.HeaderTitle, options.HeaderDescription);

        if (options.ShowModeToggle)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "ACCOUNT ACTION");
            if (ImGui.RadioButton($"{options.SignInModeLabel}##{options.IdPrefix}SignInMode", _mode == AccountAuthMode.SignIn))
                SetMode(AccountAuthMode.SignIn);
            ImGui.SameLine();
            using (ImRaii.Disabled(!options.CanCreate))
            {
                if (ImGui.RadioButton($"{options.CreateModeLabel}##{options.IdPrefix}CreateMode", _mode == AccountAuthMode.CreateAccount))
                    SetMode(AccountAuthMode.CreateAccount);
            }

            if (!options.CanCreate && !options.CreateDisabledHelp.IsNullOrEmpty())
                ElezenImgui.DrawHelpText(options.CreateDisabledHelp);
        }

        var description = _mode == AccountAuthMode.CreateAccount ? options.CreateDescription : options.SignInDescription;
        if (!description.IsNullOrEmpty())
        {
            ImGuiHelpers.ScaledDummy(new Vector2(0, 5));
            ImGui.TextWrapped(description);
        }

        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
        AccountCredentialUi.DrawTextInput($"{options.IdPrefix}Username", "Username", "Enter your username", ref _username, 64);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _usernameValidationReady = true;
        AccountCredentialUi.DrawPasswordInput($"{options.IdPrefix}Password", "Password", "Enter your password", ref _password, 128, _showPassword);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _passwordValidationReady = true;
        if (_mode == AccountAuthMode.CreateAccount)
        {
            AccountCredentialUi.DrawPasswordInput($"{options.IdPrefix}PasswordConfirm", "Confirm password", "Re-enter your password",
                ref _passwordConfirm, 128, _showPassword);
            if (ImGui.IsItemDeactivatedAfterEdit())
                _passwordConfirmValidationReady = true;
        }
        AccountCredentialUi.DrawPasswordVisibilityToggle($"{options.IdPrefix}PasswordVisibility", ref _showPassword);
        AccountCredentialUi.DrawRequirements(includePassword: _mode == AccountAuthMode.CreateAccount);

        var validationMessage = AccountCredentialValidator.Validate(_username, _password, _passwordConfirm,
            requireConfirmation: _mode == AccountAuthMode.CreateAccount);

        using (ImRaii.Disabled(IsRunning || validationMessage != null))
        {
            var buttonLabel = IsRunning
                ? _mode == AccountAuthMode.CreateAccount ? options.CreateRunningMessage : options.SignInRunningMessage
                : _mode == AccountAuthMode.CreateAccount ? options.CreateSubmitLabel : "Sign in";
            if (AccountCredentialUi.DrawPrimaryButton($"{options.IdPrefix}Submit", buttonLabel))
                Submit(options);
        }

        if (validationMessage != null && ShouldShowValidationMessage())
            ElezenImgui.ColouredWrappedText(validationMessage, ImGuiColors.DalamudYellow);

        DrawStatus();
    }

    private void Submit(PasswordAccountFlowOptions options)
    {
        var username = _username;
        var password = _password;
        var mode = _mode;
        _success = false;
        _message = mode == AccountAuthMode.CreateAccount ? options.CreateRunningMessage : options.SignInRunningMessage;

        _ = _operation.Run(() => mode == AccountAuthMode.CreateAccount
            ? options.Create(username, password)
            : options.SignIn(username, password));
    }

    private void ConsumeOperation()
    {
        if (!_operation.IsCompleted)
            return;

        if (_operation.Faulted)
        {
            _success = false;
            _message = "Account request failed. Please try again later.";
        }
        else
        {
            var result = _operation.Result;
            _success = result.Success;
            _message = result.Message;
            if (result.Success)
            {
                _password = string.Empty;
                _passwordConfirm = string.Empty;
                _usernameValidationReady = false;
                _passwordValidationReady = false;
                _passwordConfirmValidationReady = false;
            }
        }

        _operation.Reset();
    }

    private bool ShouldShowValidationMessage()
    {
        var trimmedUsernameLength = _username.Trim().Length;
        if (trimmedUsernameLength == 0 || trimmedUsernameLength is < 3 or > 64 || _username.Any(char.IsWhiteSpace))
            return _usernameValidationReady;

        if (string.IsNullOrEmpty(_password))
            return _passwordValidationReady;

        if (_mode != AccountAuthMode.CreateAccount)
            return false;

        if (_password.Length < 8)
            return _passwordValidationReady;

        return _passwordConfirmValidationReady;
    }

    private void DrawStatus()
    {
        if (_message.IsNullOrEmpty())
            return;

        if (IsRunning)
            ImGui.TextWrapped(_message);
        else if (_success)
            ElezenImgui.ColouredWrappedText(_message, ImGuiColors.HealerGreen);
        else
            ElezenImgui.ColouredWrappedText(_message, ImGuiColors.DalamudYellow);
    }

    private void SetMode(AccountAuthMode mode)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        _message = null;
        _passwordConfirm = string.Empty;
        _passwordConfirmValidationReady = false;
    }
}
