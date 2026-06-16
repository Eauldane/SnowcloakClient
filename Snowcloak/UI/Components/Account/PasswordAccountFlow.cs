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

/// <summary>
/// The result of a sign-in / create-account submission, surfaced to the user as a status line.
/// </summary>
public readonly record struct AccountFlowResult(bool Success, string Message);

/// <summary>
/// Per-draw configuration describing the surface a <see cref="PasswordAccountFlow"/> renders on.
/// The flow owns the credential form, validation, in-flight tracking and status display; the
/// surface supplies the copy and the async actions (which perform the service call and any
/// side effects, returning a user-facing result).
/// </summary>
public sealed class PasswordAccountFlowOptions
{
    public string IdPrefix { get; init; } = "account";
    public string HeaderTitle { get; init; } = string.Empty;
    public string HeaderDescription { get; init; } = string.Empty;

    /// <summary>When false only the sign-in mode is offered (no mode toggle).</summary>
    public bool ShowModeToggle { get; init; } = true;

    /// <summary>When false the "Create account" toggle is disabled (sign-in is still allowed).</summary>
    public bool CanCreate { get; init; } = true;

    public string? CreateDisabledHelp { get; init; }
    public string? SignInDescription { get; init; }
    public string? CreateDescription { get; init; }

    public string SignInRunningMessage { get; init; } = "Signing in...";
    public string CreateRunningMessage { get; init; } = "Creating account...";

    public required Func<string, string, Task<AccountFlowResult>> SignIn { get; init; }
    public required Func<string, string, Task<AccountFlowResult>> Create { get; init; }
}

/// <summary>
/// Reusable username/password account form rendered by both onboarding (IntroUI) and Settings.
/// Replaces the duplicated credential state, validation and submission code that previously lived
/// as window fields in each surface (P30).
/// </summary>
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
            var modeButtonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
            if (AccountCredentialUi.DrawModeButton("Sign in", _mode == AccountAuthMode.SignIn, modeButtonWidth))
                SetMode(AccountAuthMode.SignIn);
            ImGui.SameLine();
            using (ImRaii.Disabled(!options.CanCreate))
            {
                if (AccountCredentialUi.DrawModeButton("Create account", _mode == AccountAuthMode.CreateAccount, modeButtonWidth))
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
        AccountCredentialUi.DrawPasswordInput($"{options.IdPrefix}Password", "Password", "Enter your password", ref _password, 128, _showPassword);
        if (_mode == AccountAuthMode.CreateAccount)
        {
            AccountCredentialUi.DrawPasswordInput($"{options.IdPrefix}PasswordConfirm", "Confirm password", "Re-enter your password",
                ref _passwordConfirm, 128, _showPassword);
        }
        AccountCredentialUi.DrawPasswordVisibilityToggle($"{options.IdPrefix}PasswordVisibility", ref _showPassword);
        AccountCredentialUi.DrawRequirements(includePassword: _mode == AccountAuthMode.CreateAccount);

        var validationMessage = AccountCredentialValidator.Validate(_username, _password, _passwordConfirm,
            requireConfirmation: _mode == AccountAuthMode.CreateAccount);

        using (ImRaii.Disabled(IsRunning))
        {
            var buttonLabel = IsRunning
                ? _mode == AccountAuthMode.CreateAccount ? "Creating account..." : "Signing in..."
                : _mode == AccountAuthMode.CreateAccount ? "Create account" : "Sign in";
            if (AccountCredentialUi.DrawPrimaryButton($"{options.IdPrefix}Submit", buttonLabel))
            {
                if (validationMessage != null)
                {
                    _message = validationMessage;
                    _success = false;
                }
                else
                {
                    Submit(options);
                }
            }
        }

        if (validationMessage != null)
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
            }
        }

        _operation.Reset();
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
    }
}
