using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Venue;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Venue;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.SignalR.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Snowcloak.UI;

public sealed class VenueRegistryWindow : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly VenueRegistrationService _venueRegistrationService;
    private readonly List<VenueRegistryEntryDto> _ownedVenues = [];
    private int _selectedVenueIndex = -1;
    private string _venueName = string.Empty;
    private string _venueDescription = string.Empty;
    private string _venueWebsite = string.Empty;
    private string _venueHost = string.Empty;
    private bool _isListed = true;
    private bool _isLoading;
    private bool _isSaving;
    private string? _statusMessage;
    private bool _statusIsError;
    private bool _refreshOnNextDraw;

    public VenueRegistryWindow(ILogger<VenueRegistryWindow> logger, SnowMediator mediator, UiSharedService uiSharedService,
        ApiController apiController, VenueRegistrationService venueRegistrationService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Venue Manager###SnowcloakVenueRegistry", performanceCollectorService)
    {
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _venueRegistrationService = venueRegistrationService;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(550, 450)
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
            ElezenImgui.ColouredWrappedText("Loading owned venues...", ImGuiColors.DalamudGrey);
            return;
        }

        if (_ownedVenues.Count == 0)
        {
            ElezenImgui.ColouredWrappedText("No owned venues were found. You can register a plot using the button below.", ImGuiColors.DalamudGrey);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.MapMarkedAlt, "Register current plot"))
            {
                _venueRegistrationService.BeginRegistrationFromCommand();
            }
            UiSharedService.AttachToolTip("Start the placard verification flow for the plot you are standing on.");
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
        UiSharedService.AttachToolTip("Reload your owned venues from the server.");

        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.MapMarkedAlt, "Register current plot"))
        {
            _venueRegistrationService.BeginRegistrationFromCommand();
        }
        UiSharedService.AttachToolTip("Start the placard verification flow for the plot you are standing on.");

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

        if (ImGui.BeginCombo("Owned Venues", currentLabel))
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
                ElezenImgui.ColouredWrappedText($"Housing: {selected.AssociatedHousing}", ImGuiColors.DalamudGrey);
            }
        }
    }

    private void DrawVenueDetails()
    {
        ImGui.BeginDisabled(_selectedVenueIndex < 0 || _isSaving);

        _uiSharedService.BigText("Listing Details");
        ImGui.InputText("Venue name", ref _venueName, 100);
        ImGui.InputText("Host / contact", ref _venueHost, 200);
        ImGui.InputText("Website", ref _venueWebsite, 200);

        ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "Description {0}/2000", _venueDescription.Length));
        using (_uiSharedService.GameFont.Push())
        {
            ImGui.InputTextMultiline("##VenueRegistryDescription", ref _venueDescription, 2000,
                ImGuiHelpers.ScaledVector2(-1, 160));
        }

        if (ImGui.Checkbox("List this venue publicly", ref _isListed))
        {
            // immediate update to toggle value
        }
        _uiSharedService.DrawHelpText("Unlisted venues remain editable but won't show up in public searches or ads.");

        ImGui.EndDisabled();

        if (_selectedVenueIndex < 0)
            return;

        var canSubmit = !_isSaving && !string.IsNullOrWhiteSpace(_venueName);
        ImGui.BeginDisabled(!canSubmit);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save Venue"))
        {
            _ = SaveVenueAsync();
        }
        ImGui.EndDisabled();
        UiSharedService.AttachToolTip("Save updates to the selected venue listing.");
    }

    private static string FormatVenueLabel(VenueRegistryEntryDto entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.VenueName) ? "Unnamed Venue" : entry.VenueName;
        var listed = entry.IsListed ? "Listed" : "Unlisted";
        return string.Format(CultureInfo.InvariantCulture, "{0} ({1})", name, listed);
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
        _venueHost = selected.VenueHost ?? string.Empty;
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh owned venues");
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
                VenueHost = TrimToNull(_venueHost),
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

    private static string? TrimToNull(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
