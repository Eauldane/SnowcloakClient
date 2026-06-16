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
        var keys = _serverManager.CurrentServer!.SecretKeys;
        ImGui.BeginDisabled(_characterKeyFlow.IsRunning || _accountUidFlow.IsRunning || _apiController.ServerState == ServerState.Connecting || _apiController.ServerState == ServerState.Reconnecting);
        if (keys.Any())
        {
            if (_secretKeyIdx == -1) _secretKeyIdx = keys.First().Key;
            if (_serverManager.CurrentServer.AccountLinked)
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.UserPlus, "Create and assign account UID"))
                {
                    _accountUidFlow.Begin();
                }
            }
            else
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Log in with XIVAuth"))
                {
                    BeginCharacterRegistration(
                        _registerService.XIVAuth,
                        "Account registered. Welcome to Snowcloak!");
                }

                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Create standalone key"))
                {
                    BeginCharacterRegistration(
                        _registerService.RegisterAccount,
                        "New standalone key created.\nPlease keep a copy of your secret key in case you need to reset your plugins, or to use it on another PC.");
                }
            }

            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add character with existing key"))
            {
                _serverManager.CurrentServer!.Authentications.Add(new Configuration.Models.Authentication()
                {
                    CharacterName = _dalamudUtilService.GetPlayerName(),
                    WorldId = _dalamudUtilService.GetHomeWorldId(),
                    SecretKeyIdx = _secretKeyIdx
                });

                _serverManager.Save();

                _ = _apiController.CreateConnections();
            }

            _characterKeyFlow.DrawStatus("Waiting for the server...");
            _accountUidFlow.DrawStatus();
            if (_secretKey.Length > 0 && _secretKey.Length != 64)
            {
                ElezenImgui.ColouredWrappedText("Your secret key must be exactly 64 characters long.", ImGuiColors.DalamudRed);
            }
            else if (_secretKey.Length == 64)
            {
                using var saveDisabled = ImRaii.Disabled(_apiController.ServerState == ServerState.Connecting || _apiController.ServerState == ServerState.Reconnecting);
                if (ImGui.Button( "Save and Connect"))
                {
                    string keyName;
                    if (_serverManager.CurrentServer == null) _serverManager.SelectServer(0);
                    var registrationReply = _characterKeyFlow.Reply;
                    if (registrationReply != null && _secretKey.Equals(registrationReply.SecretKey, StringComparison.Ordinal))
                        keyName = string.Format("{0} (registered {1:yyyy-MM-dd})", registrationReply.UID, DateTime.Now);
                    else
                        keyName = string.Format("Secret Key added on Setup ({0:yyyy-MM-dd})", DateTime.Now);
                    _serverManager.CurrentServer!.SecretKeys.Add(_serverManager.CurrentServer.SecretKeys.Select(k => k.Key).LastOrDefault() + 1, new SecretKey()
                    {
                        FriendlyName = keyName,
                        Key = _secretKey,
                    });
                    _serverManager.AddCurrentCharacterToServer(save: false);
                    _ = Task.Run(() => _apiController.CreateConnections());
                }
            }
            DrawCombo("Secret Key##addCharacterSecretKey", keys, (f) => f.Value.FriendlyName, (f) => _secretKeyIdx = f.Key);
        }
        else
        {
            ElezenImgui.ColouredWrappedText("No secret keys are configured for the current server.", ImGuiColors.DalamudYellow);
        }
        ImGui.EndDisabled(); // registration in progress

    }

    private void BeginCharacterRegistration(Func<CancellationToken, Task<RegisterReplyDto>> registrationFunc, string successMessage)
    {
        _secretKey = string.Empty;
        _characterKeyFlow.Begin(registrationFunc, successMessage, reply => _secretKey = reply.SecretKey ?? "", "Registration failed");
    }
}
