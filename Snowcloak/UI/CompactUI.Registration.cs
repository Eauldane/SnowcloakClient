using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ElezenTools.UI;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Account;
using Snowcloak.API.Dto.User;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.UI.Handlers;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Snowcloak.UI;

public partial class CompactUi
{
    private void DrawAddCharacter()
    {
        ImGui.Dummy(new(10));
        var server = _serverManager.CurrentServer!;
        var keys = server.SecretKeys;
        _characterKeyFlow.DrawStatus("Creating standalone key...");
        _accountUidFlow.DrawStatus();

        var operationInProgress = _characterKeyFlow.IsRunning
                                  || _accountUidFlow.IsRunning
                                  || _apiController.ServerState == ServerState.Connecting
                                  || _apiController.ServerState == ServerState.Reconnecting;

        ImGui.BeginDisabled(operationInProgress);

        if (server.AccountLinked && ElezenImgui.ShowIconButton(FontAwesomeIcon.UserPlus, "Create and assign account UID"))
        {
            _accountUidFlow.Begin();
        }

        using (ImRaii.Disabled(_secretKey.Length > 0))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Create standalone key"))
            {
                BeginCharacterRegistration(
                    _registerService.RegisterAccount,
                    "New standalone key created. Copy it below, then save and connect.");
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted(_characterKeyFlow.Succeeded ? "New standalone key" : "Enter or paste a secret key");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##addCharacterSecretKey", "64-character secret key", ref _secretKey, 80))
        {
            _secretKey = NormalizeSecretKey(_secretKey);
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Paste, "Paste key from clipboard"))
        {
            _secretKey = NormalizeSecretKey(ImGui.GetClipboardText());
        }

        var validSecretKey = IsValidSecretKey(_secretKey);
        if (validSecretKey)
        {
            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Copy, "Copy key"))
            {
                ImGui.SetClipboardText(_secretKey);
            }
        }

        if (_secretKey.Length > 0 && _secretKey.Length != 64)
        {
            ElezenImgui.ColouredWrappedText("Your secret key must be exactly 64 characters long.", ImGuiColors.DalamudRed);
        }
        else if (_secretKey.Length == 64 && !validSecretKey)
        {
            ElezenImgui.ColouredWrappedText("Your secret key can only contain ABCDEF and the numbers 0-9.", ImGuiColors.DalamudRed);
        }
        else if (validSecretKey && ImGui.Button("Save and Connect"))
        {
            var existingKey = keys.FirstOrDefault(item => string.Equals(NormalizeSecretKey(item.Value.Key), _secretKey, StringComparison.Ordinal));
            var secretKeyIdx = existingKey.Value == null
                ? keys.Any() ? keys.Keys.Max() + 1 : 0
                : existingKey.Key;

            if (existingKey.Value == null)
            {
                var registrationReply = _characterKeyFlow.Reply;
                var keyName = registrationReply != null && _secretKey.Equals(NormalizeSecretKey(registrationReply.SecretKey ?? string.Empty), StringComparison.Ordinal)
                    ? string.Format(CultureInfo.InvariantCulture, "{0} (registered {1:yyyy-MM-dd})", registrationReply.UID, DateTime.Now)
                    : string.Format(CultureInfo.InvariantCulture, "Secret Key added on Setup ({0:yyyy-MM-dd})", DateTime.Now);
                keys.Add(secretKeyIdx, new SecretKey()
                {
                    FriendlyName = keyName,
                    Key = _secretKey
                });
            }

            AssignCurrentCharacterToKey(server, secretKeyIdx);
            _ = Task.Run(() => _apiController.CreateConnections());
        }

        if (keys.Any())
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Assign a saved key");
            if (!keys.ContainsKey(_secretKeyIdx))
                _secretKeyIdx = keys.First().Key;
            DrawCombo("Secret Key##savedCharacterSecretKey", keys, item => item.Value.FriendlyName, item => _secretKeyIdx = item.Key);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add character with selected key"))
            {
                AssignCurrentCharacterToKey(server, _secretKeyIdx);
                _ = _apiController.CreateConnections();
            }
        }

        ImGui.EndDisabled();
    }

    private void BeginCharacterRegistration(Func<CancellationToken, Task<RegisterReplyDto>> registrationFunc, string successMessage,
        string failureMessage = "Registration failed")
    {
        _secretKey = string.Empty;
        _characterKeyFlow.Begin(registrationFunc, successMessage, reply => _secretKey = reply.SecretKey ?? "", failureMessage);
    }

    private static string NormalizeSecretKey(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private static bool IsValidSecretKey(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private void AssignCurrentCharacterToKey(ServerStorage server, int secretKeyIdx)
    {
        var characterName = _dalamudUtilService.GetPlayerName();
        var worldId = _dalamudUtilService.GetHomeWorldId();
        server.Authentications.RemoveAll(item => string.Equals(item.CharacterName, characterName, StringComparison.Ordinal) && item.WorldId == worldId);
        server.Authentications.Add(new Configuration.Models.Authentication()
        {
            CharacterName = characterName,
            WorldId = worldId,
            SecretKeyIdx = secretKeyIdx
        });
        _serverManager.Save();
    }
}
