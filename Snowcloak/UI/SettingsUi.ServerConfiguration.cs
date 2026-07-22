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
    private int? _accountKeyLinkIndex;
    private Task<AccountOperationResult>? _accountKeyLinkTask;
    private string _accountKeyLinkMessage = string.Empty;
    private bool _accountKeyLinkSucceeded;

    private void DrawServerConfiguration()
    {
        if (ApiController.IsConnected)
        {
            _fontService.BigText("Service Actions");
            ImGuiHelpers.ScaledDummy(new Vector2(5, 5));
            const string deleteUidPopupTitle = "Delete your current UID?";
            if (ImGui.Button("Delete current UID"))
            {
                _deleteUidError = string.Empty;
                ImGui.OpenPopup(deleteUidPopupTitle);
            }

            ElezenImgui.DrawHelpText("Deletes the UID used by your current connection. This does not delete your Snowcloak account or its other UIDs.");

            if (ImGui.BeginPopupModal(deleteUidPopupTitle, SnowcloakUi.PopupWindowFlags))
            {
                ElezenImgui.WrappedText(
                    "Your current UID and the service data owned by it will be deleted. It will be removed from pairing lists, syncshells and rooms.");
                ElezenImgui.WrappedText(
                    "Your Snowcloak account and any other UIDs on it will remain. The deleted UID's local secret key and character assignments will be removed after the service confirms deletion.");
                ImGui.TextUnformatted("Are you sure you want to continue?");
                if (!_deleteUidError.IsNullOrEmpty())
                    ElezenImgui.ColouredWrappedText(_deleteUidError, ImGuiColors.DalamudRed);
                ImGui.Separator();
                ImGui.Spacing();

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                  ImGui.GetStyle().ItemSpacing.X) / 2;

                using (ImRaii.Disabled(_deleteUidTask != null))
                {
                    if (ImGui.Button(_deleteUidTask == null ? "Delete current UID" : "Deleting...", new Vector2(buttonSize, 0)))
                    {
                        var currentServer = _serverConfigurationManager.CurrentServer;
                        var currentPlayerName = _dalamudUtilService.GetPlayerName();
                        var currentPlayerWorldId = _dalamudUtilService.GetHomeWorldId();
                        _deleteUidSecretKeyIndex = currentServer.Authentications
                            .FirstOrDefault(a => string.Equals(a.CharacterName, currentPlayerName, StringComparison.OrdinalIgnoreCase)
                                                 && a.WorldId == currentPlayerWorldId)
                            ?.SecretKeyIdx;
                        _deleteUidError = string.Empty;
                        var deleteTask = DeleteCurrentUidAsync();
                        _deleteUidTask = deleteTask.IsCompleted ? null : deleteTask;
                    }
                }

                ImGui.SameLine();

                using (ImRaii.Disabled(_deleteUidTask != null))
                {
                    if (ImGui.Button("Cancel##cancelDeleteUid", new Vector2(buttonSize, 0)))
                    {
                        _deleteUidError = string.Empty;
                        ImGui.CloseCurrentPopup();
                    }
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

    private async Task DeleteCurrentUidAsync()
    {
        try
        {
            await ApiController.UserDelete().ConfigureAwait(false);

            var currentServer = _serverConfigurationManager.CurrentServer;
            if (_deleteUidSecretKeyIndex is { } deletedKeyIndex)
            {
                currentServer.Authentications.RemoveAll(a => a.SecretKeyIdx == deletedKeyIndex);
                currentServer.SecretKeys.Remove(deletedKeyIndex);
            }

            currentServer.JoinedRooms.Clear();
            currentServer.AccountLinked = false;
            currentServer.UserAccountId = null;

            _serverConfigurationManager.Save();
            _deleteUidSecretKeyIndex = null;
            _deleteUidTask = null;
            Mediator.Publish(new NotificationMessage("UID deleted",
                "This UID has been successfully deleted.", NotificationType.Info, TimeSpan.FromSeconds(5)));
            Mediator.Publish(new SwitchToIntroUiMessage());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Current UID deletion failed");
            _deleteUidSecretKeyIndex = null;
            _deleteUidError = "The server failed to delete the UID. No local keys or character assignments were changed.";
            _deleteUidTask = null;
            Mediator.Publish(new NotificationMessage("UID deletion failed", _deleteUidError,
                NotificationType.Error, TimeSpan.FromSeconds(7.5)));
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
            var keyPrompt = selectedServer.AccountLinked
                ? "Your current character is not linked to a working key. Create and assign either a new account UID or a standalone key."
                : invalidSecretKey
                    ? "Your current character's secret key appears to be invalid. Sign in with a Snowcloak account below to restore account keys, or create a standalone key."
                    : "Your current character is not linked to a secret key. Sign in with a Snowcloak account below to restore account keys, or create a standalone key.";
            ElezenImgui.ColouredWrappedText(keyPrompt, ImGuiColors.DalamudYellow);

            if (selectedServer.AccountLinked && selectedServer == _serverConfigurationManager.CurrentServer)
            {
                DrawAccountUidGenerationButton();
            }

            using (ImRaii.Disabled(_characterKeyAssignmentFlow.IsRunning || _accountUidGenerationFlow.IsRunning))
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Create and assign standalone key"))
                {
                    _characterKeyAssignmentFlow.Begin(selectedServer, playerName, playerWorldId, removeInvalidSecretKey,
                        invalidSecretKeyIdx, _registerService.RegisterAccount,
                        "Standalone key created successfully. Added a new secret key and assigned it to your current character.",
                        "Standalone key registration failed");
                }
            }

            _characterKeyAssignmentFlow.DrawStatus();
            ImGui.Separator();
        }
        if (selectedServer == _serverConfigurationManager.CurrentServer)
        {
            if (selectedServer.AccountLinked)
                DrawAccountManagementSection(selectedServer);
            if (selectedServer.AccountLinked)
                DrawAccountKeyLinkSection(selectedServer);
            DrawAccountMigrationSection(
                hasWorkingCurrentKey: hasSecretKey && !invalidSecretKey,
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
                using (ImRaii.Disabled(_addKeyAccountUidFlow.IsRunning || _standaloneKeyFlow.IsRunning))
                {
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.UserPlus, "Create account UID"))
                    {
                        _addKeyAccountUidFlow.Begin();
                    }
                }
                ElezenImgui.AttachTooltip("Creates a new UID on the server, attaches it to your Snowcloak account, and downloads its key.");
                ImGui.SameLine();
            }

            using (ImRaii.Disabled(_standaloneKeyFlow.IsRunning || _addKeyAccountUidFlow.IsRunning))
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Create standalone key"))
                {
                    _standaloneKeyFlow.Begin(_registerService.RegisterAccount,
                        "New standalone key created.\nPlease keep a copy of your secret key in case you need to reset your plugins, or to use it on another PC.",
                        reply =>
                        {
                            selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Count != 0 ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                            {
                                FriendlyName = string.Format(CultureInfo.InvariantCulture, "{0} {1}", reply.UID, string.Format(CultureInfo.InvariantCulture, "(registered {0:yyyy-MM-dd})", DateTime.Now)),
                                Key = reply.SecretKey ?? ""
                            });
                            _serverConfigurationManager.Save();
                        },
                        "Standalone key registration failed");
                }
            }
            ElezenImgui.AttachTooltip("Registers a standalone secret key with the server and adds it to this service without linking it to your Snowcloak account.");

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
            _standaloneKeyFlow.DrawStatus("Registering standalone key...");
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

    private void DrawAccountManagementSection(ServerStorage selectedServer)
    {
        ImGui.TextUnformatted("Snowcloak account");
        ElezenImgui.DrawHelpText("This device can restore account-held keys and create account UIDs. Your saved secret keys remain usable independently.");

        var accountOperationRunning = _accountMigrationFlow.IsRunning
                                      || _accountUidGenerationFlow.IsRunning
                                      || _addKeyAccountUidFlow.IsRunning
                                      || _accountKeyLinkTask != null;
        using (ImRaii.Disabled(accountOperationRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Unlink, "Disconnect account on this device"))
            {
                selectedServer.AccountLinked = false;
                selectedServer.UserAccountId = null;
                _accountKeyLinkIndex = null;
                _accountKeyLinkMessage = string.Empty;
                _accountKeyLinkSucceeded = false;
                _accountMigrationFlow.Reset();
                _serverConfigurationManager.Save();
                Mediator.Publish(new NotificationMessage("Account disconnected on this device",
                    "Saved secret keys and character assignments were retained. The server account and its UIDs were not changed.",
                    NotificationType.Info, TimeSpan.FromSeconds(7.5)));
            }
        }
        ElezenImgui.AttachTooltip("Stops account key restore and account UID creation on this device. Saved secret keys and character assignments are retained, and nothing is unlinked or revoked on the server.");
    }

    private void DrawAccountUidGenerationButton()
    {
        using (ImRaii.Disabled(_accountUidGenerationFlow.IsRunning || _characterKeyAssignmentFlow.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.UserPlus, "Create and assign account UID"))
            {
                _accountUidGenerationFlow.Begin();
            }
        }

        _accountUidGenerationFlow.DrawStatus();
    }

    private void DrawAccountKeyLinkSection(ServerStorage selectedServer)
    {
        ConsumeAccountKeyLinkResult();

        if (_accountKeyLinkIndex.HasValue && !selectedServer.SecretKeys.ContainsKey(_accountKeyLinkIndex.Value))
            _accountKeyLinkIndex = null;

        ImGui.TextUnformatted("Link a standalone key");
        ElezenImgui.DrawHelpText("Choose one saved key to add to this account. Only the selected key is uploaded; other standalone keys remain local and unchanged.");

        var selectedName = _accountKeyLinkIndex.HasValue
                           && selectedServer.SecretKeys.TryGetValue(_accountKeyLinkIndex.Value, out var selectedKey)
            ? selectedKey.FriendlyName
            : "Select a saved key";

        if (ImGui.BeginCombo("##accountKeyLinkSelection", selectedName))
        {
            foreach (var key in selectedServer.SecretKeys)
            {
                if (ImGui.Selectable($"{key.Value.FriendlyName}##accountKeyLink{key.Key}", key.Key == _accountKeyLinkIndex))
                {
                    _accountKeyLinkIndex = key.Key;
                    _accountKeyLinkMessage = string.Empty;
                }
            }

            ImGui.EndCombo();
        }

        using (ImRaii.Disabled(!_accountKeyLinkIndex.HasValue || _accountKeyLinkTask != null))
        {
            var buttonText = _accountKeyLinkTask == null ? "Link selected key to account" : "Linking selected key...";
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Link, buttonText)
                && _accountKeyLinkIndex.HasValue
                && selectedServer.SecretKeys.TryGetValue(_accountKeyLinkIndex.Value, out var key))
            {
                _accountKeyLinkMessage = string.Empty;
                _accountKeyLinkSucceeded = false;
                _accountKeyLinkTask = _registerService.LinkLocalSecretKey(key.Key, CancellationToken.None);
            }
        }

        if (_accountKeyLinkTask != null)
        {
            ImGui.TextUnformatted("Linking only the selected key...");
        }
        else if (!_accountKeyLinkMessage.IsNullOrEmpty())
        {
            if (_accountKeyLinkSucceeded)
                ImGui.TextWrapped(_accountKeyLinkMessage);
            else
                ElezenImgui.ColouredWrappedText(_accountKeyLinkMessage, ImGuiColors.DalamudYellow);
        }
    }

    private void ConsumeAccountKeyLinkResult()
    {
        if (_accountKeyLinkTask is not { IsCompleted: true } task)
            return;

        try
        {
            var result = task.GetAwaiter().GetResult();
            _accountKeyLinkSucceeded = result.Success;
            _accountKeyLinkMessage = result.Success
                ? string.Format(CultureInfo.InvariantCulture,
                    "The selected key is now linked to this account as UID {0}. No other local keys were uploaded.", result.Uid)
                : result.ErrorMessage.IsNullOrEmpty()
                    ? "The selected key could not be linked. No other local keys were uploaded."
                    : string.Format(CultureInfo.InvariantCulture, "The selected key could not be linked: {0}", result.ErrorMessage);
            if (result.Success)
                _accountKeyLinkIndex = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Standalone secret key account link failed");
            _accountKeyLinkSucceeded = false;
            _accountKeyLinkMessage = "The selected key could not be linked. No other local keys were uploaded.";
        }

        _accountKeyLinkTask = null;
    }

    private void DrawAccountMigrationSection(bool hasWorkingCurrentKey, bool accountLinked)
    {
        _accountMigrationFlow.Draw(new PasswordAccountFlowOptions
        {
            IdPrefix = "accountMigration",
            HeaderTitle = accountLinked ? "Sync Snowcloak account keys" : "Snowcloak account sign-in",
            HeaderDescription = accountLinked
                ? "Sign in again to restore account-held keys, or use your current working key to add another sign-in credential. Standalone keys remain local unless you explicitly link one above."
                : "Sign in to restore keys already held by this account. Your standalone keys remain local unless you explicitly link one after signing in. You can also create an account from an existing working character key.",
            ShowModeToggle = true,
            CanCreate = hasWorkingCurrentKey,
            CreateDisabledHelp = accountLinked
                ? "Adding a sign-in credential requires a working secret key for the current character. You can still sign in and restore account keys."
                : "Creating an account here requires a working secret key for the current character. Existing accounts can still sign in and restore keys.",
            CreateModeLabel = accountLinked ? "Add sign-in credential" : "Create account",
            CreateSubmitLabel = accountLinked ? "Add sign-in credential" : "Create account",
            CreateDescription = accountLinked
                ? "Add another username and password to the account authenticated by your current key. Existing sign-in credentials remain valid."
                : "Create an optional username and password for the account authenticated by your current key.",
            SignInRunningMessage = "Signing in and restoring account keys...",
            CreateRunningMessage = accountLinked
                ? "Adding sign-in credential..."
                : "Creating a password account for the current key...",
            SignIn = SignInAndRestoreAccountKeys,
            Create = accountLinked ? AddSignInCredentialFromCurrentKey : CreateAccountFromCurrentKey
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
                "Account sign-in succeeded. Stored {0} account key(s), including {1} new key(s). Other local keys were left unchanged. Attempting to connect.",
                result.SecretKeyCount, result.NewSecretKeyCount);
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
                "Password account ready for the current key. Stored {0} account key(s), including {1} new key(s). Other local keys were left unchanged.",
                result.SecretKeyCount, result.NewSecretKeyCount));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Password account setup failed");
            return new AccountFlowResult(false, "Password account setup failed. Please try again later.");
        }
    }

    private async Task<AccountFlowResult> AddSignInCredentialFromCurrentKey(string username, string password)
    {
        try
        {
            var result = await _registerService.AttachPasswordToCurrentAccount(username, password, CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                return new AccountFlowResult(false, result.ErrorMessage.IsNullOrEmpty()
                    ? "Sign-in credential setup failed. Please try again later."
                    : result.ErrorMessage);
            }

            return new AccountFlowResult(true, string.Format(CultureInfo.InvariantCulture,
                "Sign-in credential added. Stored {0} account key(s), including {1} new key(s). Existing sign-in credentials remain valid and other local keys were left unchanged.",
                result.SecretKeyCount, result.NewSecretKeyCount));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sign-in credential setup failed");
            return new AccountFlowResult(false, "Sign-in credential setup failed. Please try again later.");
        }
    }
}
