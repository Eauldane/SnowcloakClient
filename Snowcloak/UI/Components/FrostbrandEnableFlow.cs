using System.Numerics;
using System.Security.Cryptography;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using ElezenTools.UI;
using ElezenTools.UI.Mvu;
using Snowcloak.Services.Pairing;
using Snowcloak.UI.PairingAvailability;

namespace Snowcloak.UI.Components;

public sealed class FrostbrandEnableFlow
{
    private bool _modalShown;
    private bool _modalJustShown;
    private bool _useUriangerText;

    public void Draw(AvailabilityViewState state, IDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(dispatcher);

        var pairingEnabled = state.PairingEnabled;
        var requestedPairingEnabled = pairingEnabled;
        if (ImGui.Checkbox("Enable Frostbrand pairing features", ref requestedPairingEnabled))
        {
            if (requestedPairingEnabled && !pairingEnabled)
            {
                _modalJustShown = true;
                _modalShown = true;
                ImGui.OpenPopup("Enable Frostbrand pairing?");
            }
            else
            {
                dispatcher.Dispatch(new SetPairingEnabledIntent(requestedPairingEnabled));
            }
        }

        ElezenImgui.DrawHelpText("Disable to hide pairing highlights, suppress right-click pairing actions, and pause auto-rejection.");
        DrawModal(dispatcher);
    }

    private void DrawModal(IDispatcher dispatcher)
    {
        if (!ImGui.BeginPopupModal("Enable Frostbrand pairing?", ref _modalShown, SnowcloakUi.PopupWindowFlags))
            return;

        if (_modalJustShown)
        {
            _useUriangerText = RandomNumberGenerator.GetInt32(99) == 0;
            _modalJustShown = false;
        }

        if (!_useUriangerText)
            DrawPlainConsent(dispatcher);
        else
            DrawUriangerConsent(dispatcher);

        ElezenImgui.SetScaledWindowSize(500);
        ImGui.EndPopup();
    }

    private void DrawPlainConsent(IDispatcher dispatcher)
    {
        ElezenImgui.WrappedText("Frostbrand is a system that, when opted-in to, shows other nearby users who've opted in that you're open to pairing.");
        ElezenImgui.WrappedText("Whilst Snowcloak provides filters to automatically reject those you're not interested in pairing with, please be aware that while you have it enabled, anyone using Frostbrand will be able to see that you're using Snowcloak.");
        ElezenImgui.WrappedText("Please take the time to understand the privacy risk this introduces, and if you choose to enable the system, you're advised to configure filters immediately, preferably in a quiet area.");
        ElezenImgui.WrappedText("Continue?");
        FrostbrandPanelChrome.DrawSoftSeparator();
        ImGuiHelpers.ScaledDummy(new Vector2(0, 2));

        var buttonSize = GetConsentButtonWidth();
        if (ImGui.Button("Confirm", new Vector2(buttonSize, 0)))
            Confirm(dispatcher);

        ImGui.SameLine();

        if (ImGui.Button("Cancel##cancelFrostbrandEnable", new Vector2(buttonSize, 0)))
            Cancel();
    }

    private void DrawUriangerConsent(IDispatcher dispatcher)
    {
        ElezenImgui.WrappedText("Frostbrand be a covenant of mutual accord, whereby those who do willingly partake therein may perceive, among the souls nearby, others likewise disposed unto pairing.");
        ElezenImgui.WrappedText("Know this also: though Snowcloak doth grant thee wards and strictures, by which thou mayest deny communion with those thou wouldst not suffer, yet whilst Frostbrand remaineth enabled, any who wield its sight shall discern that thou makest use of Snowcloak.");
        ElezenImgui.WrappedText("Ponder well, then, the peril to thine own privacy that this revelation entailest. Shouldst thou resolve to walk this path regardless, thou art strongly counseled to set thy filters with all haste - best done where few eyes linger and fewer ears attend.");
        ElezenImgui.WrappedText("Wilt thou press on?");
        FrostbrandPanelChrome.DrawSoftSeparator();
        ImGuiHelpers.ScaledDummy(new Vector2(0, 2));

        var buttonSize = GetConsentButtonWidth();
        if (ImGui.Button("Thus do I assent", new Vector2(buttonSize, 0)))
            Confirm(dispatcher);

        ImGui.SameLine();

        if (ImGui.Button("I shall refrain##cancelFrostbrandEnable", new Vector2(buttonSize, 0)))
            Cancel();
    }

    private static float GetConsentButtonWidth()
        => (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
            ImGui.GetStyle().ItemSpacing.X) / 2;

    private void Confirm(IDispatcher dispatcher)
    {
        dispatcher.Dispatch(new SetPairingEnabledIntent(true));
        _modalShown = false;
        ImGui.CloseCurrentPopup();
    }

    private void Cancel()
    {
        _modalShown = false;
        ImGui.CloseCurrentPopup();
    }
}
