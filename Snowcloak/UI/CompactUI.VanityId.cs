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
    private static readonly Action<ILogger, Exception?> LogPatreonStatusRefreshFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(20, nameof(LogPatreonStatusRefreshFailed)),
            "Failed to refresh Patreon status");
    private static readonly Action<ILogger, Exception?> LogPatreonConnectionRefreshFailed =
        LoggerMessage.Define(LogLevel.Debug, new EventId(21, nameof(LogPatreonConnectionRefreshFailed)),
            "Failed to refresh connection dto after Patreon login");
    private static readonly Action<ILogger, Exception?> LogPatreonLoginFlowFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(22, nameof(LogPatreonLoginFlowFailed)),
            "Patreon login flow failed");

    private void DrawVanityIdPopup()
    {
        var popupTitle = "Edit Vanity ID";
        if (_showVanityIdModal && !ImGui.IsPopupOpen(popupTitle))
        {
            ImGui.OpenPopup(popupTitle);
        }
        if (ImGui.BeginPopupModal(popupTitle, ref _showVanityIdModal, SnowcloakUi.PopupWindowFlags))
        {
            ElezenImgui.WrappedText("Set your vanity ID (3-25 characters, letters/numbers/underscores/hyphens). Leave blank to clear.");
            ImGui.InputTextWithHint("##vanity-id", "Enter vanity ID (optional)", ref _vanityIdInput, 25);

            var canUseVanityColours = _apiController.HexAllowed || _patreonStatus.Entitlements.HasBenefits;

            ImGui.Spacing();
            if (_patreonStatusLoading)
            {
                ImGui.TextUnformatted("Checking Patreon status...");
            }
            else if (!_patreonStatus.Entitlements.HasBenefits)
            {
                ElezenImgui.WrappedText("Patreon subscribers get vanity colours! If you have a pledge, log in and get them.");
                using (ImRaii.Disabled(_patreonLoginInProgress))
                {
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Heart, "Log in with Patreon"))
                    {
                        StartPatreonLogin();
                    }
                }

                if (_patreonLoginInProgress)
                {
                    ImGui.TextUnformatted("Waiting for Patreon login...");
                }
                else if (!_patreonStatus.Success && !string.IsNullOrWhiteSpace(_patreonStatus.ErrorMessage))
                {
                    ImGui.TextColored(ImGuiColors.DalamudYellow, _patreonStatus.ErrorMessage);
                }
            }
            else
            {
                ElezenImgui.ColouredWrappedText("Donator benefits are active! You can set a custom display colour and glow.", SnowcloakColours.BooleanTrue);
            }

            if (!string.IsNullOrWhiteSpace(_patreonLoginFeedback))
            {
                ImGui.Spacing();
                var feedbackColor = _patreonLoginFeedbackLevel switch
                {
                    PatreonLoginFeedbackLevel.Failure => ImGuiColors.DalamudRed,
                    PatreonLoginFeedbackLevel.LoggedInNoPledge => ImGuiColors.DalamudYellow,
                    PatreonLoginFeedbackLevel.Success => ImGuiColors.HealerGreen,
                    _ => ImGui.GetStyle().Colors[(int)ImGuiCol.Text]
                };
                ElezenImgui.ColouredWrappedText(_patreonLoginFeedback, feedbackColor);
            }

            if (canUseVanityColours)
            {
                ImGui.Spacing();
                ElezenImgui.WrappedText("Optional: set a custom display color and glow.");
                ImGui.Checkbox("Use custom color", ref _useVanityColour);
                using (ImRaii.Disabled(!_useVanityColour))
                {
                    ImGui.ColorEdit3("Name color##vanity-color", ref _vanityColour, ImGuiColorEditFlags.NoInputs);
                    ImGui.Checkbox("Use custom glow", ref _useVanityGlowColour);
                    using (ImRaii.Disabled(!_useVanityGlowColour))
                    {
                        ImGui.ColorEdit3("Glow color##vanity-glow-color", ref _vanityGlowColour, ImGuiColorEditFlags.NoInputs);
                    }
                }
            }
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save"))
            {
                var trimmed = _vanityIdInput.Trim();
                var vanityId = string.IsNullOrEmpty(trimmed) ? null : trimmed;
                string? hexString = null;
                string? glowHexString = null;
                if (canUseVanityColours)
                {
                    if (_useVanityColour)
                    {
                        hexString = Colour.Vector3ToHex(_vanityColour);
                        glowHexString = _useVanityGlowColour ? Colour.Vector3ToHex(_vanityGlowColour) : string.Empty;
                    }
                    else
                    {
                        hexString = string.Empty;
                        glowHexString = string.Empty;
                    }
                }
                _ = _apiController.UserSetVanityId(new UserVanityIdDto(vanityId) { HexString = hexString, GlowHexString = glowHexString });
                _showVanityIdModal = false;
            }

            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Times, "Cancel"))
            {
                _showVanityIdModal = false;
            }
            ElezenImgui.SetScaledWindowSize(360);
            ImGui.EndPopup();
        }
    }

    private void RefreshPatreonStatus()
    {
        _patreonStatusLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                _patreonStatus = await _registerService.GetPatreonStatus(CancellationToken.None).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                SetPatreonStatusRefreshFailed(ex);
            }
            catch (InvalidOperationException ex)
            {
                SetPatreonStatusRefreshFailed(ex);
            }
            catch (TaskCanceledException ex)
            {
                SetPatreonStatusRefreshFailed(ex);
            }
            finally
            {
                _patreonStatusLoading = false;
            }
        });
    }

    private void SetPatreonStatusRefreshFailed(Exception ex)
    {
        LogPatreonStatusRefreshFailed(_logger, ex);
        _patreonStatus = new PatreonStatusResult
        {
            Success = false,
            ErrorMessage = "Unable to check Patreon status right now."
        };
    }

    private void StartPatreonLogin()
    {
        _patreonLoginInProgress = true;
        _patreonLoginFeedback = null;
        _patreonLoginFeedbackLevel = PatreonLoginFeedbackLevel.None;
        _ = Task.Run(async () =>
        {
            try
            {
                var loginResult = await _registerService.LoginWithPatreon(CancellationToken.None).ConfigureAwait(false);
                _patreonLoginFeedback = BuildPatreonLoginFeedback(loginResult);
                _patreonLoginFeedbackLevel = GetPatreonLoginFeedbackLevel(loginResult);

                if (loginResult.Success)
                {
                    await RefreshConnectionAfterPatreonLogin().ConfigureAwait(false);
                    _patreonStatus = await _registerService.GetPatreonStatus(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException ex)
            {
                SetPatreonLoginFailed(ex);
            }
            catch (InvalidOperationException ex)
            {
                SetPatreonLoginFailed(ex);
            }
            catch (TaskCanceledException ex)
            {
                SetPatreonLoginFailed(ex);
            }
            finally
            {
                _patreonLoginInProgress = false;
            }
        });
    }

    private async Task RefreshConnectionAfterPatreonLogin()
    {
        try
        {
            await _apiController.GetConnectionDto(publishConnected: false).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            LogPatreonConnectionRefreshFailed(_logger, ex);
        }
        catch (InvalidOperationException ex)
        {
            LogPatreonConnectionRefreshFailed(_logger, ex);
        }
        catch (TaskCanceledException ex)
        {
            LogPatreonConnectionRefreshFailed(_logger, ex);
        }
    }

    private void SetPatreonLoginFailed(Exception ex)
    {
        LogPatreonLoginFlowFailed(_logger, ex);
        _patreonLoginFeedback = "Patreon login failed. Please try again.";
        _patreonLoginFeedbackLevel = PatreonLoginFeedbackLevel.Failure;
    }

    private static PatreonLoginFeedbackLevel GetPatreonLoginFeedbackLevel(PatreonLoginResult result)
    {
        if (!result.Success)
        {
            return PatreonLoginFeedbackLevel.Failure;
        }

        return (result.Entitlements.IsPayingPatron || result.Entitlements.IsCreatorForCampaign)
            ? PatreonLoginFeedbackLevel.Success
            : PatreonLoginFeedbackLevel.LoggedInNoPledge;
    }

    private static string BuildPatreonLoginFeedback(PatreonLoginResult result)
    {
        if (!result.Success)
        {
            return string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Patreon login failed. Please try again."
                : result.ErrorMessage;
        }

        if (result.Entitlements.IsCreatorForCampaign)
        {
            return "Login succeeded! Creator account detected for the configured campaign. This account is treated as subscribed and perks are active.";
        }

        if (result.Entitlements.IsPayingPatron)
        {
            return "Login succeeded! Your Patreon perks are now active.";
        }

        if (result.Entitlements.IsCompetitionWinner)
        {
            return "Login succeeded! No active paid membership detected, but you won one of our competitions! Winner status keeps your benefits active permanently.";
        }

        if (result.Entitlements.IsTestOverride)
        {
            return "Login succeeded! No active paid membership detected. Test override is active for this account.";
        }

        return "Login succeeded! No active subscription was detected for your account. Log in again once you have one to unlock vanity colours!";
    }
}
