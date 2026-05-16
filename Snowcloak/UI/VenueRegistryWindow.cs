using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Venue;
using Snowcloak.Services;
using Snowcloak.Services.Housing;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Services.Venue;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.SignalR.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Numerics;
using System.Threading.Tasks;

namespace Snowcloak.UI;

public sealed class VenueRegistryWindow : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly UiSharedService _uiSharedService;
    private readonly VenueRegistrationService _venueRegistrationService;
    private readonly List<VenueRegistryEntryDto> _ownedVenues = [];
    private int _selectedVenueIndex = -1;
    private string _venueName = string.Empty;
    private string _venueDescription = string.Empty;
    private string _venueWebsite = string.Empty;
    private string _venueWebhookUrl = string.Empty;
    private string _venueHost = string.Empty;
    private Vector3 _venueNameColour = Vector3.One;
    private bool _isListed = true;
    private bool _isLoading;
    private bool _isSaving;
    private bool _isDeleting;
    private bool _showDeregisterModal;
    private Guid _deregisterTargetId;
    private string _deregisterTargetName = string.Empty;
    private string _deregisterConfirmationText = string.Empty;
    private string? _statusMessage;
    private bool _statusIsError;
    private bool _refreshOnNextDraw;

    public VenueRegistryWindow(ILogger<VenueRegistryWindow> logger, SnowMediator mediator, UiSharedService uiSharedService,
        ApiController apiController, ServerConfigurationManager serverConfigurationManager,
        VenueRegistrationService venueRegistrationService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Venue Manager###SnowcloakVenueRegistry", performanceCollectorService)
    {
        _apiController = apiController;
        _serverConfigurationManager = serverConfigurationManager;
        _uiSharedService = uiSharedService;
        _venueRegistrationService = venueRegistrationService;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(550, 750)
        };

        Mediator.Subscribe<OpenVenueRegistryWindowMessage>(this, (_) =>
        {
            _refreshOnNextDraw = true;
            IsOpen = true;
        });
    }

    public override void OnOpen()
    {
        _ = RefreshOwnedVenuesAsync();
    }

    protected override void DrawInternal()
    {
        if (_refreshOnNextDraw)
        {
            _refreshOnNextDraw = false;
            _ = RefreshOwnedVenuesAsync();
        }

        DrawHeader();

        if (_apiController.ServerState is not ServerState.Connected)
        {
            ElezenImgui.ColouredWrappedText("Connect to Snowcloak to view and update your venue listings.", ImGuiColors.DalamudRed);
            return;
        }

        if (_isLoading)
        {
            ElezenImgui.ColouredWrappedText("Loading managed venues...", ImGuiColors.DalamudGrey);
            return;
        }

        if (_ownedVenues.Count == 0)
        {
            ElezenImgui.ColouredWrappedText("No managed venues were found. You can register a plot using the button above.", ImGuiColors.DalamudGrey);
            return;
        }

        DrawVenueSelector();
        UiSharedService.DistanceSeparator();
        DrawVenueDetails();
    }

    private void DrawHeader()
    {
        _uiSharedService.BigText("Venue Manager");
        ElezenImgui.ColouredWrappedText("Update your venue listing without being on-site, or register a new plot when you're at the placard.", ImGuiColors.DalamudGrey);
        ImGui.Separator();

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.SyncAlt, "Refresh"))
        {
            _ = RefreshOwnedVenuesAsync();
        }
        ElezenImgui.AttachTooltip("Reload your managed venues from the server.");

        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.MapMarkedAlt, "Register current plot"))
        {
            _venueRegistrationService.BeginRegistrationFromCommand();
        }
        ElezenImgui.AttachTooltip("Start the placard verification flow for the plot you are standing on.");

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            var color = _statusIsError ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen;
            ElezenImgui.ColouredWrappedText(_statusMessage, color);
        }
        ImGuiHelpers.ScaledDummy(4);
    }

    private void DrawVenueSelector()
    {
        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        var currentLabel = _selectedVenueIndex >= 0 && _selectedVenueIndex < _ownedVenues.Count
            ? FormatVenueLabel(_ownedVenues[_selectedVenueIndex])
            : "Select a venue...";

        if (ImGui.BeginCombo("Managed Venues", currentLabel))
        {
            for (var i = 0; i < _ownedVenues.Count; i++)
            {
                var entry = _ownedVenues[i];
                var label = FormatVenueLabel(entry);
                if (ImGui.Selectable(label, i == _selectedVenueIndex))
                {
                    SelectVenue(i);
                }
            }
            ImGui.EndCombo();
        }

        if (_selectedVenueIndex >= 0)
        {
            var selected = _ownedVenues[_selectedVenueIndex];
            if (!string.IsNullOrWhiteSpace(selected.AssociatedHousing))
            {
                ElezenImgui.ColouredWrappedText($"Housing: {FormatHousingLocation(selected.AssociatedHousing)}", ImGuiColors.DalamudGrey);
            }
        }
    }

    private void DrawVenueDetails()
    {
        ImGui.BeginDisabled(_selectedVenueIndex < 0 || _isSaving || _isDeleting);

        _uiSharedService.BigText("Listing Details");
        ImGui.InputText("Venue name", ref _venueName, 100);
        ImGui.ColorEdit3("Venue name colour", ref _venueNameColour,
            ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.Uint8);
        ElezenImgui.AttachTooltip("Leave as white to use the default syncshell colour, or pick a custom override for the venue name.");
        ImGui.InputText("Host / contact", ref _venueHost, 200);
        ImGui.InputText("Website", ref _venueWebsite, 200);
        ImGui.InputText("Discord webhook URL", ref _venueWebhookUrl, 2048);
        ElezenImgui.DrawHelpText("Optional: If you have a Discord server you want to publish event ads to, paste the webhook URL here.");

        ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "Description {0}/2000", _venueDescription.Length));
        using (_uiSharedService.GameFont.Push())
        {
            ImGui.InputTextMultiline("##VenueRegistryDescription", ref _venueDescription, 2000,
                ImGuiHelpers.ScaledVector2(-1, 160));
        }
        ImGui.TextUnformatted("Preview (BBCode renderer)");
        if (ImGui.BeginChild("##VenueRegistryDescriptionPreview", ImGuiHelpers.ScaledVector2(-1, ImGuiHelpers.GlobalScale * 120), true))
        {
            _uiSharedService.RenderBbCode(_venueDescription, ImGui.GetContentRegionAvail().X);
        }
        ImGui.EndChild();

        if (ImGui.Checkbox("List this venue publicly", ref _isListed))
        {
            // immediate update to toggle value
        }
        ElezenImgui.DrawHelpText("Unlisted venues remain editable but won't show up in public searches or ads.");

        ImGui.EndDisabled();

        if (_selectedVenueIndex < 0)
            return;

        var selected = _ownedVenues[_selectedVenueIndex];
        var embedUrl = BuildVenueEmbedUrl(selected.Id);
        var embedCode = string.IsNullOrWhiteSpace(embedUrl) ? string.Empty : BuildVenueEmbedCode(embedUrl);

        var canSubmit = !_isSaving && !_isDeleting && !string.IsNullOrWhiteSpace(_venueName);
        ImGui.BeginDisabled(!canSubmit);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save Venue"))
        {
            _ = SaveVenueAsync();
        }
        ImGui.EndDisabled();
        ElezenImgui.AttachTooltip("Save updates to the selected venue listing.");

        ImGui.SameLine();
        ImGui.BeginDisabled(_isDeleting || _isSaving);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "De-register Venue"))
        {
            _deregisterTargetId = selected.Id;
            _deregisterTargetName = selected.VenueName?.Trim() ?? string.Empty;
            _deregisterConfirmationText = string.Empty;
            _showDeregisterModal = true;
            ImGui.OpenPopup("De-register Venue?");
        }
        ImGui.EndDisabled();
        ElezenImgui.AttachTooltip("Remove this venue listing, housing registration, and venue ads. The syncshell remains.");

        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(embedCode));
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Copy, "Copy Embed Code"))
        {
            ImGui.SetClipboardText(embedCode);
            _statusMessage = "Embed code copied to clipboard.";
            _statusIsError = false;
        }
        ImGui.EndDisabled();
        ElezenImgui.AttachTooltip("Copy an iframe snippet to include on a website, carrd etc that'll show your currently running ad.");

        DrawDeregisterModal();
    }

    private string BuildVenueEmbedUrl(Guid registryId)
    {
        if (!Uri.TryCreate(_serverConfigurationManager.CurrentRealApiUrl, UriKind.Absolute, out var apiUri))
        {
            return string.Empty;
        }

        var scheme = apiUri.Scheme switch
        {
            "ws" => Uri.UriSchemeHttp,
            "wss" => Uri.UriSchemeHttps,
            _ => apiUri.Scheme
        };

        var builder = new UriBuilder(apiUri)
        {
            Scheme = scheme,
            Path = $"/venue/embed/{registryId:D}",
            Query = string.Empty,
            Fragment = string.Empty
        };

        if ((string.Equals(builder.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && builder.Port == 443)
            || (string.Equals(builder.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && builder.Port == 80))
        {
            builder.Port = -1;
        }

        return builder.Uri.ToString();
    }

    private static string BuildVenueEmbedCode(string embedUrl)
    {
        return $"""<iframe src="{embedUrl}" title="Snowcloak Venue Ad" width="420" height="660" style="width:100%;max-width:420px;height:660px;border:0;" loading="lazy"></iframe>""";
    }

    private static string FormatVenueLabel(VenueRegistryEntryDto entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.VenueName) ? "Unnamed Venue" : entry.VenueName;
        var listed = entry.IsListed ? "Listed" : "Unlisted";
        return string.Format(CultureInfo.InvariantCulture, "{0} ({1})", name, listed);
    }
    
    private string FormatHousingLocation(string associatedHousing)
    {
        if (!TryParseHousingLocation(associatedHousing, out var location))
            return associatedHousing;

        var worldName = _uiSharedService.WorldData.GetValueOrDefault((ushort)location.WorldId, location.WorldId.ToString(CultureInfo.InvariantCulture));
        var territoryName = _uiSharedService.TerritoryData.GetValueOrDefault(location.TerritoryId, $"Territory {location.TerritoryId.ToString(CultureInfo.InvariantCulture)}");

        var builder = new StringBuilder();
        builder.Append(worldName);
        builder.Append(" - ");
        builder.Append(territoryName);
        builder.Append(" - Ward ");
        builder.Append(location.WardId.ToString(CultureInfo.InvariantCulture));
        if (location.DivisionId > 0)
        {
            builder.Append(" Subdivision");
        }

        if (location.IsApartment)
        {
            builder.Append(" Apartments");
            if (location.RoomId > 0)
            {
                builder.Append(" Room ");
                builder.Append(location.RoomId.ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            builder.Append(" Plot ");
            builder.Append(location.PlotId.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool TryParseHousingLocation(string associatedHousing, out HousingPlotLocation location)
    {
        location = default;
        var parts = associatedHousing.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 6)
            return false;

        if (!uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var worldId)
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var territoryId)
            || !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var divisionId)
            || !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var wardId)
            || !uint.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var plotId)
            || !uint.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var roomId))
        {
            return false;
        }

        // `AssociatedHousing` commonly stores FullId, but may include a boolean apartment flag.
        var isApartment = roomId > 0;
        if (parts.Length >= 7)
        {
            if (bool.TryParse(parts[6], out var parsedBool))
            {
                isApartment = parsedBool;
            }
            else if (uint.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedNumeric))
            {
                isApartment = parsedNumeric != 0;
            }
        }

        location = new HousingPlotLocation(worldId, territoryId, divisionId, wardId, plotId, roomId, isApartment);
        return true;
    }

    private void SelectVenue(int index)
    {
        if (index < 0 || index >= _ownedVenues.Count)
            return;

        _selectedVenueIndex = index;
        var selected = _ownedVenues[index];
        _venueName = selected.VenueName ?? string.Empty;
        _venueDescription = selected.VenueDescription ?? string.Empty;
        _venueWebsite = selected.VenueWebsite ?? string.Empty;
        _venueWebhookUrl = selected.VenueWebhookUrl ?? string.Empty;
        _venueHost = selected.VenueHost ?? string.Empty;
        _venueNameColour = ParseHexColourOrDefault(selected.HexString, Vector3.One);
        _isListed = selected.IsListed;
        _statusMessage = null;
    }

    private async Task RefreshOwnedVenuesAsync()
    {
        if (_isLoading)
            return;

        _isLoading = true;
        _statusMessage = null;
        _statusIsError = false;
        try
        {
            var response = await _apiController.VenueRegistryListOwned(new VenueRegistryListOwnedRequestDto(0, 50))
                .ConfigureAwait(false);
            var venues = response?.Registries ?? [];

            _ownedVenues.Clear();
            _ownedVenues.AddRange(venues);
            _selectedVenueIndex = _ownedVenues.Count > 0 ? 0 : -1;
            if (_selectedVenueIndex >= 0)
            {
                SelectVenue(_selectedVenueIndex);
            }
            else
            {
                ClearVenueFields();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh managed venues");
            _statusMessage = "Failed to load venues. Please try again.";
            _statusIsError = true;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task SaveVenueAsync()
    {
        if (_selectedVenueIndex < 0 || _selectedVenueIndex >= _ownedVenues.Count)
            return;

        var selected = _ownedVenues[_selectedVenueIndex];
        var venueName = _venueName.Trim();
        if (string.IsNullOrWhiteSpace(venueName))
            return;

        _isSaving = true;
        _statusMessage = null;
        _statusIsError = false;
        try
        {
            var request = new VenueRegistryUpsertRequestDto(selected.SyncshellGid, venueName)
            {
                VenueDescription = TrimToNull(_venueDescription),
                VenueWebsite = TrimToNull(_venueWebsite),
                VenueWebhookUrl = TrimToNull(_venueWebhookUrl),
                VenueHost = TrimToNull(_venueHost),
                HexString = GetVenueNameColourHex(),
                IsListed = _isListed
            };
            var response = await _apiController.VenueRegistryUpsert(request).ConfigureAwait(false);
            if (response.Success)
            {
                _statusMessage = response.WasUpdate ? "Venue updated successfully." : "Venue submitted successfully.";
                _statusIsError = false;
                if (response.Registry != null)
                {
                    _ownedVenues[_selectedVenueIndex] = response.Registry;
                    SelectVenue(_selectedVenueIndex);
                }
            }
            else
            {
                _statusMessage = string.IsNullOrWhiteSpace(response.ErrorMessage)
                    ? "Failed to save venue."
                    : response.ErrorMessage;
                _statusIsError = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save venue");
            _statusMessage = "Failed to save venue. Please try again.";
            _statusIsError = true;
        }
        finally
        {
            _isSaving = false;
        }
    }

    private void DrawDeregisterModal()
    {
        const string PopupTitle = "De-register Venue?";

        if (_showDeregisterModal && !ImGui.IsPopupOpen(PopupTitle))
            ImGui.OpenPopup(PopupTitle);

        if (!ImGui.BeginPopupModal(PopupTitle, ref _showDeregisterModal, UiSharedService.PopupWindowFlags))
            return;

        ElezenImgui.ColouredWrappedText(
            "This removes the venue listing, housing registration, and all venue advertisements. The syncshell itself will not be deleted.",
            ImGuiColors.DalamudYellow);
        ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "Type {0} to confirm.", _deregisterTargetName));
        ImGui.InputText("Venue name##DeregisterVenueName", ref _deregisterConfirmationText, 100);

        var confirmationMatches = string.Equals(
            _deregisterConfirmationText.Trim(),
            _deregisterTargetName,
            StringComparison.Ordinal);

        ImGui.BeginDisabled(_isDeleting || !confirmationMatches);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Confirm De-registration"))
        {
            _ = DeregisterVenueAsync(_deregisterTargetId, _deregisterConfirmationText.Trim());
            _showDeregisterModal = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Times, "Cancel"))
        {
            _showDeregisterModal = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private async Task DeregisterVenueAsync(Guid registryId, string confirmationVenueName)
    {
        if (registryId == Guid.Empty || string.IsNullOrWhiteSpace(confirmationVenueName))
            return;

        _isDeleting = true;
        _statusMessage = null;
        _statusIsError = false;
        try
        {
            var response = await _apiController.VenueRegistryDelete(
                    new VenueRegistryDeleteRequestDto(registryId, confirmationVenueName))
                .ConfigureAwait(false);

            if (!response.Success)
            {
                _statusMessage = string.IsNullOrWhiteSpace(response.ErrorMessage)
                    ? "Failed to de-register venue."
                    : response.ErrorMessage;
                _statusIsError = true;
                return;
            }

            var removedIndex = _ownedVenues.FindIndex(v => v.Id == registryId);
            if (removedIndex >= 0)
                _ownedVenues.RemoveAt(removedIndex);

            if (_ownedVenues.Count == 0)
            {
                _selectedVenueIndex = -1;
                ClearVenueFields();
            }
            else
            {
                SelectVenue(Math.Min(removedIndex < 0 ? 0 : removedIndex, _ownedVenues.Count - 1));
            }

            _statusMessage = "Venue de-registered successfully.";
            _statusIsError = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to de-register venue");
            _statusMessage = "Failed to de-register venue. Please try again.";
            _statusIsError = true;
        }
        finally
        {
            _isDeleting = false;
        }
    }

    private void ClearVenueFields()
    {
        _venueName = string.Empty;
        _venueDescription = string.Empty;
        _venueWebsite = string.Empty;
        _venueWebhookUrl = string.Empty;
        _venueHost = string.Empty;
        _venueNameColour = Vector3.One;
        _isListed = true;
    }

    private static string? TrimToNull(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static Vector3 ParseHexColourOrDefault(string? hex, Vector3 fallback)
    {
        if (hex != null && hex.Length == 6)
        {
            var colour = ElezenTools.UI.Colour.HexToVector4(hex);
            return new Vector3(colour.X, colour.Y, colour.Z);
        }

        return fallback;
    }

    private string? GetVenueNameColourHex()
    {
        var hex = ColourVectorToHex(_venueNameColour);
        return hex == "FFFFFF" ? null : hex;
    }

    private static string ColourVectorToHex(Vector3 colour)
    {
        var r = (int)Math.Clamp(colour.X * 255f, 0f, 255f);
        var g = (int)Math.Clamp(colour.Y * 255f, 0f, 255f);
        var b = (int)Math.Clamp(colour.Z * 255f, 0f, 255f);

        return $"{r:X2}{g:X2}{b:X2}";
    }
}
