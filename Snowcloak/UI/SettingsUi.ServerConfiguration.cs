using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using Snowcloak.UI.Components.Account;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public partial class SettingsUi
{
    private const string ServerTabCharacters = "Character Assignments";
    private const string ServerTabSecretKey = "Secret Key Management";
    private const string ServerTabService = "Service Settings";
    private string _serverActiveTab = ServerTabCharacters;

    private void DrawServerConfiguration()
    {
        if (ApiController.ServerAlive)
        {
            _fontService.BigText("Service Actions");
            ImGuiHelpers.ScaledDummy(new Vector2(5, 5));
            var deleteAccountPopupTitle ="Delete your account?";
            if (ImGui.Button("Delete account"))
            {
                _deleteAccountPopupModalShown = true;
                ImGui.OpenPopup(deleteAccountPopupTitle);
            }

            ElezenImgui.DrawHelpText("Completely deletes your currently connected account.");

            if (ImGui.BeginPopupModal(deleteAccountPopupTitle, ref _deleteAccountPopupModalShown, SnowcloakUi.PopupWindowFlags))
            {
                ElezenImgui.WrappedText(
                   "Your account and all associated files and data on the service will be deleted.");
                ElezenImgui.WrappedText("Your UID will be removed from all pairing lists.");
                ImGui.TextUnformatted("Are you sure you want to continue?");
                ImGui.Separator();
                ImGui.Spacing();

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                  ImGui.GetStyle().ItemSpacing.X) / 2;

                if (ImGui.Button("Delete account", new Vector2(buttonSize, 0)))
                {
                    _ = Task.Run(ApiController.UserDelete);
                    _deleteAccountPopupModalShown = false;
                    Mediator.Publish(new SwitchToIntroUiMessage());
                }

                ImGui.SameLine();

                if (ImGui.Button("Cancel##cancelDelete", new Vector2(buttonSize, 0)))
                {
                    _deleteAccountPopupModalShown = false;
                }

                ElezenImgui.SetScaledWindowSize(325);
                ImGui.EndPopup();
            }
            ImGui.Separator();
        }

        _fontService.BigText("Service & Character Settings");

        var idx = _serviceSelectionPanel.Draw();
        var playerName = _dalamudUtilService.GetPlayerName();
        var playerWorldId = _dalamudUtilService.GetHomeWorldId();
        var worldData = _dalamudUtilService.WorldData.OrderBy(u => u.Value, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value);
        string playerWorldName = worldData.GetValueOrDefault((ushort)playerWorldId, $"{playerWorldId}");

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));

        var selectedServer = _serverConfigurationManager.GetServerByIndex(idx);
        if (selectedServer == _serverConfigurationManager.CurrentServer && _apiController.IsConnected)
        {
            ElezenImgui.ColouredWrappedText("For any changes to be applied to the current service you need to reconnect to the service.", ImGuiColors.DalamudYellow);
        }

        _serverActiveTab = ModernTabBar.Draw("serverTabs",
            new[] { ServerTabCharacters, ServerTabSecretKey, ServerTabService }, _serverActiveTab);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 5));

        if (string.Equals(_serverActiveTab, ServerTabCharacters, StringComparison.Ordinal))
        {
            DrawCharacterAssignmentsTab(selectedServer, idx, playerName, playerWorldId, worldData);
        }

        if (string.Equals(_serverActiveTab, ServerTabSecretKey, StringComparison.Ordinal))
        {
            DrawSecretKeyTab(selectedServer, playerName, playerWorldId, playerWorldName);
        }

        if (string.Equals(_serverActiveTab, ServerTabService, StringComparison.Ordinal))
        {
            DrawServiceSettingsTab(selectedServer);
        }
    }

    private void DrawCharacterAssignmentsTab(ServerStorage selectedServer, int idx, string playerName, uint playerWorldId,
        Dictionary<ushort, string> worldData)
    {
        if (selectedServer.SecretKeys.Count == 0)
        {
            ElezenImgui.ColouredWrappedText("You need to add a Secret Key first before adding Characters.", ImGuiColors.DalamudYellow);
            return;
        }

        float windowPadding = ImGui.GetStyle().WindowPadding.X;
        float itemSpacing = ImGui.GetStyle().ItemSpacing.X;
        float longestName = 0.0f;
        if (selectedServer.Authentications.Count > 0)
            longestName = selectedServer.Authentications.Max(p => ImGui.CalcTextSize($"{p.CharacterName} @ Pandaemonium  ").X);
        float iconWidth;

        using (_ = _fontService.IconFont.Push())
            iconWidth = ImGui.CalcTextSize(FontAwesomeIcon.Trash.ToIconString()).X;

        ElezenImgui.ColouredWrappedText("Characters listed here will connect with the specified secret key.", ImGuiColors.DalamudYellow);
        int i = 0;
        foreach (var item in selectedServer.Authentications.ToList())
        {
            using var charaId = ImRaii.PushId("selectedChara" + i);

            bool thisIsYou = string.Equals(playerName, item.CharacterName, StringComparison.OrdinalIgnoreCase)
                && playerWorldId == item.WorldId;

            if (!worldData.TryGetValue((ushort)item.WorldId, out string? worldPreview))
                worldPreview = worldData.First().Value;

            ElezenImgui.ShowIcon(thisIsYou ? FontAwesomeIcon.Star : FontAwesomeIcon.None);

            if (thisIsYou)
                ElezenImgui.AttachTooltip("Current character");

            ImGui.SameLine(windowPadding + iconWidth + itemSpacing);
            float beforeName = ImGui.GetCursorPosX();
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture,"{0} @ {1}", item.CharacterName, worldPreview));
            float afterName = ImGui.GetCursorPosX();

            ImGui.SameLine(afterName + (afterName - beforeName) + longestName + itemSpacing);

            ImGui.SetNextItemWidth(afterName - iconWidth - itemSpacing * 2 - windowPadding);

            string selectedKeyName = string.Empty;
            if (selectedServer.SecretKeys.TryGetValue(item.SecretKeyIdx, out var selectedKey))
                selectedKeyName = selectedKey.FriendlyName;

            // DrawCombo() remembers the selected option -- we don't want that, because the value can change
            if (ImGui.BeginCombo($"##{item.CharacterName}{i}", selectedKeyName))
            {
                foreach (var key in selectedServer.SecretKeys)
                {
                    if (ImGui.Selectable($"{key.Value.FriendlyName}##{i}", key.Key == item.SecretKeyIdx)
                        && key.Key != item.SecretKeyIdx)
                    {
                        item.SecretKeyIdx = key.Key;
                        _serverConfigurationManager.Save();
                    }
                }
                ImGui.EndCombo();
            }

            ImGui.SameLine();

            if (ElezenImgui.IconButton(FontAwesomeIcon.Trash))
                _serverConfigurationManager.RemoveCharacterFromServer(idx, item);
            ElezenImgui.AttachTooltip("Delete character assignment");
            i++;
        }

        ImGui.Separator();
        using (_ = ImRaii.Disabled(selectedServer.Authentications.Exists(c =>
                string.Equals(c.CharacterName, playerName, StringComparison.Ordinal)
                    && c.WorldId == playerWorldId
        )))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.User, "Add current character"))
            {
                _serverConfigurationManager.AddCurrentCharacterToServer(idx);
            }
            ImGui.SameLine();
        }
    }

    private void DrawSecretKeyTab(ServerStorage selectedServer, string playerName, uint playerWorldId, string playerWorldName)
    {
        var currentCharacterAssignment = selectedServer.Authentications.Find(a =>
            string.Equals(a.CharacterName, playerName, StringComparison.OrdinalIgnoreCase)
                && a.WorldId == playerWorldId
        );
        var hasSecretKey =
            currentCharacterAssignment != null
            && selectedServer.SecretKeys.TryGetValue(currentCharacterAssignment.SecretKeyIdx, out var currentSecretKey)
            && !currentSecretKey.Key.IsNullOrEmpty();

        var invalidSecretKey = _apiController.ServerState == ServerState.Unauthorized
                               && !_apiController.AuthFailureMessage.IsNullOrEmpty()
                               && _apiController.AuthFailureMessage.Contains("secret", StringComparison.OrdinalIgnoreCase);

        var invalidSecretKeyIdx = currentCharacterAssignment?.SecretKeyIdx;
        var removeInvalidSecretKey = invalidSecretKey
                                     && invalidSecretKeyIdx.HasValue
                                     && selectedServer.SecretKeys.ContainsKey(invalidSecretKeyIdx.Value);

        if (!hasSecretKey || invalidSecretKey)
        {
            var xivAuthPrompt = selectedServer.AccountLinked
                ? "Your current character is not linked to a working account key. Create and assign a new account UID to receive a server-generated key."
                : invalidSecretKey
                    ? "Your current character's secret key appears to be invalid. Sign in with a Snowcloak account below to restore account keys, log in with XIVAuth to replace and assign a working key automatically, or create a standalone key."
                    : "Your current character is not linked to a secret key. Sign in with a Snowcloak account below to restore account keys, log in with XIVAuth to add and assign one automatically, or create a standalone key.";
            ElezenImgui.ColouredWrappedText(xivAuthPrompt, ImGuiColors.DalamudYellow);

            if (selectedServer.AccountLinked && selectedServer == _serverConfigurationManager.CurrentServer)
            {
                DrawAccountUidGenerationButton();
            }
            else
            {
                using (ImRaii.Disabled(_characterKeyAssignmentFlow.IsRunning))
                {
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Log in with XIVAuth"))
                    {
                        _characterKeyAssignmentFlow.Begin(selectedServer, playerName, playerWorldId, removeInvalidSecretKey,
                            invalidSecretKeyIdx, _registerService.XIVAuth,
                            "XIVAuth login successful. Added a new secret key and assigned it to your current character.",
                            "XIVAuth registration failed");
                    }

                    ImGui.SameLine();
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Create and assign key"))
                    {
                        _characterKeyAssignmentFlow.Begin(selectedServer, playerName, playerWorldId, removeInvalidSecretKey,
                            invalidSecretKeyIdx, _registerService.RegisterAccount,
                            "Standalone key created successfully. Added a new secret key and assigned it to your current character.",
                            "Standalone key registration failed");
                    }
                }

                _characterKeyAssignmentFlow.DrawStatus();
            }

            ImGui.Separator();
        }
        if (selectedServer == _serverConfigurationManager.CurrentServer)
        {
            if (selectedServer.AccountLinked && hasSecretKey && !invalidSecretKey)
                DrawAccountManagementSection();
            DrawAccountMigrationSection(
                canCreateAccount: !selectedServer.AccountLinked && hasSecretKey && !invalidSecretKey,
                accountLinked: selectedServer.AccountLinked);
            ImGui.Separator();
        }

        foreach (var item in selectedServer.SecretKeys.ToList())
        {
            using var id = ImRaii.PushId("key" + item.Key);
            var friendlyName = item.Value.FriendlyName;
            if (ImGui.InputText("Secret Key Display Name", ref friendlyName, 255))
            {
                item.Value.FriendlyName = friendlyName;
                _serverConfigurationManager.Save();
            }
            var key = item.Value.Key;
            var keyInUse = selectedServer.Authentications.Exists(p => p.SecretKeyIdx == item.Key);
            if (keyInUse) ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
            if (ImGui.InputText("Secret Key", ref key, 64, keyInUse ? ImGuiInputTextFlags.ReadOnly : default))
            {
                item.Value.Key = key;
                _serverConfigurationManager.Save();
            }
            if (keyInUse) ImGui.PopStyleColor();

            bool thisIsYou = selectedServer.Authentications.Any(a =>
                a.SecretKeyIdx == item.Key
                    && string.Equals(a.CharacterName, playerName, StringComparison.OrdinalIgnoreCase)
                    && a.WorldId == playerWorldId
            );

            bool disableAssignment = thisIsYou || item.Value.Key.IsNullOrEmpty();

            using (_ = ImRaii.Disabled(disableAssignment))
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.User, "Assign current character"))
                {
                    var existingAssignment = selectedServer.Authentications.Find(a =>
                        string.Equals(a.CharacterName, playerName, StringComparison.OrdinalIgnoreCase)
                            && a.WorldId == playerWorldId
                    );

                    if (existingAssignment == null)
                    {
                        selectedServer.Authentications.Add(new Authentication()
                        {
                            CharacterName = playerName,
                            WorldId = playerWorldId,
                            SecretKeyIdx = item.Key
                        });
                    }
                    else
                    {
                        existingAssignment.SecretKeyIdx = item.Key;
                    }
                }
                if (!disableAssignment)
                    ElezenImgui.AttachTooltip(string.Format(CultureInfo.InvariantCulture, "Use this secret key for {0} @ {1}", playerName, playerWorldName));
            }

            ImGui.SameLine();
            using var disableDelete = ImRaii.Disabled(keyInUse);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete Secret Key") && ElezenImgui.CtrlPressed())
            {
                selectedServer.SecretKeys.Remove(item.Key);
                _serverConfigurationManager.Save();
            }
            if (!keyInUse)
                ElezenImgui.AttachTooltip("Hold CTRL to delete this secret key entry");

            if (keyInUse)
            {
                ElezenImgui.ColouredWrappedText("This key is currently assigned to a character and cannot be edited or deleted.", ImGuiColors.DalamudYellow);
            }

            if (item.Key != selectedServer.SecretKeys.Keys.LastOrDefault())
                ImGui.Separator();
        }

        ImGui.Separator();
        var isCurrentServer = selectedServer == _serverConfigurationManager.CurrentServer;
        if (isCurrentServer)
        {
            if (selectedServer.AccountLinked)
            {
                using (ImRaii.Disabled(_addKeyAccountUidFlow.IsRunning))
                {
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Create new Secret Key"))
                    {
                        _addKeyAccountUidFlow.Begin();
                    }
                }
                ElezenImgui.AttachTooltip("Creates a new UID on the server, attaches it to your Snowcloak account, and downloads its key.");
            }
            else
            {
                using (ImRaii.Disabled(_standaloneKeyFlow.IsRunning))
                {
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Create new Secret Key"))
                    {
                        _standaloneKeyFlow.Begin(_registerService.RegisterAccount,
                            "New secret key created.\nPlease keep a copy of your secret key in case you need to reset your plugins, or to use it on another PC.",
                            reply =>
                            {
                                selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Count != 0 ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                                {
                                    FriendlyName = string.Format(CultureInfo.InvariantCulture, "{0} {1}", reply.UID, string.Format(CultureInfo.InvariantCulture, "(registered {0:yyyy-MM-dd})", DateTime.Now)),
                                    Key = reply.SecretKey ?? ""
                                });
                                _serverConfigurationManager.Save();
                            },
                            "Registration failed");
                    }
                }
                ElezenImgui.AttachTooltip("Registers a new secret key with the server and adds it to this service.");
            }

            ImGui.SameLine();
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add empty key"))
        {
            selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Count != 0 ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
            {
                FriendlyName = "New Secret Key",
            });
            _serverConfigurationManager.Save();
        }
        ElezenImgui.AttachTooltip("Adds an empty entry so you can paste a secret key you already have.");

        if (isCurrentServer)
        {
            if (selectedServer.AccountLinked)
                _addKeyAccountUidFlow.DrawStatus();
            else
                _standaloneKeyFlow.DrawStatus("Sending request...");
        }
    }

    private void DrawServiceSettingsTab(ServerStorage selectedServer)
    {
        var serverName = selectedServer.ServerName;
        var serverUri = selectedServer.ServerUri;
        var isMain = string.Equals(serverName, ApiController.SnowcloakServer, StringComparison.OrdinalIgnoreCase);
        var flags = isMain ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None;

        if (ImGui.InputText("Service URI", ref serverUri, 255, flags))
        {
            selectedServer.ServerUri = serverUri;
        }
        if (isMain)
        {
            ElezenImgui.DrawHelpText("You cannot edit the URI of the main service.");
        }

        if (ImGui.InputText("Service Name", ref serverName, 255, flags))
        {
            selectedServer.ServerName = serverName;
            _serverConfigurationManager.Save();
        }
        if (isMain)
        {
            ElezenImgui.DrawHelpText("You cannot edit the name of the main service.");
        }

        if (!isMain && selectedServer != _serverConfigurationManager.CurrentServer)
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete Service") && ElezenImgui.CtrlPressed())
            {
                _serverConfigurationManager.DeleteServer(selectedServer);
            }
            ElezenImgui.DrawHelpText("Hold CTRL to delete this service");
        }

        ImGui.Separator();
        _fontService.BigText("Snowcloak Backup");
        ElezenImgui.DrawHelpText("Export and restore secret keys, character assignments, and notes for this service as a backup file for if you plan to reinstall the game.");

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Export secret key backup"))
        {
            _secretKeyBackupFlow.BeginExport(selectedServer);
        }
        ElezenImgui.AttachTooltip("Choose a location to save the backup file.");

        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileImport, "Restore secret key backup"))
        {
            _secretKeyBackupFlow.BeginImportIntoServer(selectedServer);
        }
        ElezenImgui.AttachTooltip("Restore secret keys, character assignments, and notes from a JSON backup file.");

        _secretKeyBackupFlow.DrawStatus();
    }

    private void DrawAccountManagementSection()
    {
        ImGui.TextUnformatted("Snowcloak account");
        ElezenImgui.DrawHelpText("This service is linked to a Snowcloak account. New UIDs are created on the server and downloaded with an account-managed secret key.");
        DrawAccountUidGenerationButton();
    }

    private void DrawAccountUidGenerationButton()
    {
        using (ImRaii.Disabled(_accountUidGenerationFlow.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.UserPlus, "Create and assign account UID"))
            {
                _accountUidGenerationFlow.Begin();
            }
        }

        _accountUidGenerationFlow.DrawStatus();
    }

    private void DrawAccountMigrationSection(bool canCreateAccount, bool accountLinked)
    {
        _accountMigrationFlow.Draw(new PasswordAccountFlowOptions
        {
            IdPrefix = "accountMigration",
            HeaderTitle = accountLinked ? "Sync Snowcloak account keys" : "Snowcloak account sign-in",
            HeaderDescription = accountLinked
                ? "Sign in again to upload local keys and restore the account keys held by this service."
                : "Sign in to upload local keys and restore the account keys held by this service. You can also create an account from an existing working character key.",
            ShowModeToggle = !accountLinked,
            CanCreate = canCreateAccount,
            CreateDisabledHelp = "Creating an account here requires a working secret key for the current character. Existing accounts can still sign in and restore keys.",
            SignInRunningMessage = "Signing in, uploading local keys, and restoring account keys...",
            CreateRunningMessage = "Creating a password account and uploading local keys...",
            SignIn = SignInAndRestoreAccountKeys,
            Create = CreateAccountFromCurrentKey
        });
    }

    private async Task<AccountFlowResult> SignInAndRestoreAccountKeys(string username, string password)
    {
        try
        {
            var result = await _registerService.LoginWithPassword(username, password, CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                return new AccountFlowResult(false, result.ErrorMessage.IsNullOrEmpty()
                    ? "Account sign-in failed. Please try again later."
                    : result.ErrorMessage);
            }

            var message = string.Format(CultureInfo.InvariantCulture,
                "Account sign-in succeeded. Linked {0} local key(s) and stored {1} account key(s), including {2} new key(s). Attempting to connect.",
                result.LinkedLocalSecretKeyCount, result.SecretKeyCount, result.NewSecretKeyCount);
            _ = _apiController.CreateConnections();
            return new AccountFlowResult(true, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Account sign-in failed");
            return new AccountFlowResult(false, "Account sign-in failed. Please try again later.");
        }
    }

    private async Task<AccountFlowResult> CreateAccountFromCurrentKey(string username, string password)
    {
        try
        {
            var result = await _registerService.AttachPasswordToCurrentAccount(username, password, CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                return new AccountFlowResult(false, result.ErrorMessage.IsNullOrEmpty()
                    ? "Password account setup failed. Please try again later."
                    : result.ErrorMessage);
            }

            return new AccountFlowResult(true, string.Format(CultureInfo.InvariantCulture,
                "Password account ready. Linked {0} local key(s) and stored {1} account key(s), including {2} new key(s).",
                result.LinkedLocalSecretKeyCount, result.SecretKeyCount, result.NewSecretKeyCount));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Password account setup failed");
            return new AccountFlowResult(false, "Password account setup failed. Please try again later.");
        }
    }
}
