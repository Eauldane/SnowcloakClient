using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.Venue;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Venue;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class VenueRegistrationUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private ILogger<VenueRegistrationUi> _logger;
    private readonly List<GroupFullInfoDto> _adminGroups = new();

    private VenueRegistrationContext? _context;
    private string _venueName = string.Empty;
    private string _venueDescription = string.Empty;
    private string _venueWebsite = string.Empty;
    private string _venueHost = string.Empty;
    private string _syncshellAlias = string.Empty;
    private Vector3 _venueNameColour = Vector3.One;
    private string? _selectedGroupGid;
    private string? _statusMessage;
    private bool _isSubmitting;
    private bool _isEditingExistingVenue;
    private CancellationTokenSource? _prefillCancellation;

    public VenueRegistrationUi(ILogger<VenueRegistrationUi> logger, SnowMediator mediator, UiSharedService uiSharedService,
        ApiController apiController, PairManager pairManager, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Venue Registration###SnowcloakVenueRegistration", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _apiController = apiController;
        _pairManager = pairManager;
        _logger = logger;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(500, 400),
            MaximumSize = new(1000, 1200)
        };

        Mediator.Subscribe<OpenVenueRegistrationWindowMessage>(this, OnOpenRegistrationWindow);
    }
    
    private void OnOpenRegistrationWindow(OpenVenueRegistrationWindowMessage message)
    {
        _prefillCancellation?.Cancel();
        _prefillCancellation?.Dispose();
        _prefillCancellation = null;

        _context = message.Context;
        _statusMessage = null;
        _isSubmitting = false;
        _isEditingExistingVenue = false;
        
        _venueName = string.Empty;
        _venueHost = string.Empty;
        _venueDescription = string.Empty;
        _venueWebsite = string.Empty;
        _syncshellAlias = string.Empty;
        _venueNameColour = Vector3.One;
        _selectedGroupGid = null;


        RefreshAdminGroups();
        if (_selectedGroupGid == null && _adminGroups.Count > 0)
            _selectedGroupGid = _adminGroups[0].Group.GID;

        _syncshellAlias = GetSyncshellAliasOrDefault(_selectedGroupGid);
        
        ApplyOwnerDefaults();

        BeginPrefillExistingVenue();

        IsOpen = true;
    }

    protected override void DrawInternal()
    {
        if (_context == null)
        {
            UiSharedService.ColorTextWrapped("No pending venue registration. Use /venue to verify a placard first.",
                ImGuiColors.DalamudGrey);
            return;
        }

        RefreshAdminGroups();

        _uiSharedService.BigText("Plot details");
        ImGui.TextUnformatted(_context.Location.FriendlyName);
        ImGui.TextUnformatted(string.Format("Placard owner: {0}", _context.OwnerName ?? "Unknown"));
        ImGui.TextUnformatted(_context.AuthorisedByFreeCompany
            ? "Plot Type: Free Company"
            : "Plot Type: Personal ownership");
        if (_context.AuthorisedByFreeCompany)
        {
            ImGui.TextUnformatted(_context.FreeCompanyTag != null
                ? string.Format("Free Company tag: {0}", _context.FreeCompanyTag)
                : "Free Company tag: None detected");
        }

        ImGui.Separator();
        _uiSharedService.BigText("Select syncshell");

        if (_adminGroups.Count == 0)
        {
            UiSharedService.ColorTextWrapped("You must own or moderate a syncshell to register a venue.", ImGuiColors.DalamudRed);
        }
        else
        {
            if (_isEditingExistingVenue)
                UiSharedService.ColorTextWrapped("Syncshell association cannot be changed for an existing venue.", ImGuiColors.DalamudGrey);

            ImGui.BeginDisabled(_isEditingExistingVenue);
            var previousSelectedGroup = _selectedGroupGid;
            
            if (ImGui.BeginCombo("##VenueRegistrationShell", GetGroupLabel(_selectedGroupGid)))
            {
                foreach (var group in _adminGroups)
                {
                    var isSelected = string.Equals(_selectedGroupGid, group.Group.GID, StringComparison.Ordinal);
                    if (ImGui.Selectable(GetGroupLabel(group.Group.GID), isSelected))
                    {
                        _selectedGroupGid = group.Group.GID;
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }
            UiSharedService.AttachToolTip( "Choose which syncshell will own this venue entry.");
            ImGui.EndDisabled();
            
            if (!string.Equals(previousSelectedGroup, _selectedGroupGid, StringComparison.Ordinal))
                _syncshellAlias = GetSyncshellAliasOrDefault(_selectedGroupGid);
        }
        ImGui.Separator();
        _uiSharedService.BigText("Venue details");
        
        ImGui.InputText("Venue name", ref _venueName, 100);
        UiSharedService.AttachToolTip("Required: this is the display name shown to visitors.");
        ImGui.InputText("Syncshell name", ref _syncshellAlias, 100);

        UiSharedService.AttachToolTip("Optional: set how this syncshell should appear; the server applies it as the syncshell alias.");

        ImGui.ColorEdit3("Venue name colour", ref _venueNameColour,
            ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.Uint8);
        UiSharedService.AttachToolTip("Leave as white to use the default syncshell colour, or pick a custom override for the venue name.");
        
        ImGui.InputText("Host / contact", ref _venueHost, 200);
        UiSharedService.AttachToolTip("Optional: who should be listed as the venue host.");

        ImGui.InputText("Website", ref _venueWebsite, 200);
        UiSharedService.AttachToolTip("Optional: external link for the venue.");

        ImGui.TextUnformatted("Description");
        ImGui.InputTextMultiline("##VenueDescription", ref _venueDescription, 2000,
            ImGuiHelpers.ScaledVector2(-1, ImGuiHelpers.GlobalScale * 120));
        UiSharedService.AttachToolTip("Optional: a short description shown on the venue listing.");
        
        ImGui.TextUnformatted("Preview (BBCode renderer)");
        if (ImGui.BeginChild("##VenueDescriptionPreview", ImGuiHelpers.ScaledVector2(-1, ImGuiHelpers.GlobalScale * 120), true))
        {
            _uiSharedService.RenderBbCode(_venueDescription, ImGui.GetContentRegionAvail().X);
        }
        ImGui.EndChild();

        ImGui.Separator();
        var canSubmit = !_isSubmitting && _context != null && _adminGroups.Count > 0 && !string.IsNullOrWhiteSpace(_venueName)
                        && !string.IsNullOrWhiteSpace(_selectedGroupGid) && _apiController.IsConnected;

        if (!_apiController.IsConnected)
        {
            UiSharedService.ColorTextWrapped("You must be connected to Snowcloak to submit a venue registration.",
                ImGuiColors.DalamudYellow);
        }

        if (_statusMessage != null)
        {
            UiSharedService.ColorTextWrapped(_statusMessage, ImGuiColors.DalamudGrey);
        }

        ImGui.BeginDisabled(!canSubmit);
        if (ImGui.Button(_isSubmitting ? "Submitting..." : "Submit", ImGuiHelpers.ScaledVector2(200, 0)))
        {
            SubmitRegistration();
        }
        ImGui.EndDisabled();
    }

    private void RefreshAdminGroups()
    {
        _adminGroups.Clear();
        var uid = _apiController.UID;
        var previousSelectedGroup = _selectedGroupGid;
        
        foreach (var group in _pairManager.Groups.Values)
        {
            var isOwner = string.Equals(group.OwnerUID, uid, StringComparison.Ordinal);
            var isModerator = group.GroupUserInfo.IsModerator();

            if (isOwner || isModerator)
                _adminGroups.Add(group);
        }

        _adminGroups.Sort((a, b) => string.Compare(a.Group.AliasOrGID, b.Group.AliasOrGID, StringComparison.OrdinalIgnoreCase));

        if (_selectedGroupGid != null && !_adminGroups.Any(g => string.Equals(g.Group.GID, _selectedGroupGid, StringComparison.Ordinal)) && !_isEditingExistingVenue)
            _selectedGroupGid = _adminGroups.FirstOrDefault()?.Group.GID;
        
        
        if (!string.Equals(previousSelectedGroup, _selectedGroupGid, StringComparison.Ordinal))
            _syncshellAlias = GetSyncshellAliasOrDefault(_selectedGroupGid);
    }

    private string GetGroupLabel(string? gid)
    {
        if (gid == null)
            return "Select a syncshell";
        
        var group = _adminGroups.FirstOrDefault(g => string.Equals(g.Group.GID, gid, StringComparison.Ordinal));
        return group?.Group.AliasOrGID ?? gid;
    }

    private void SubmitRegistration()
    {
        if (_context == null || string.IsNullOrWhiteSpace(_selectedGroupGid))
            return;

        _isSubmitting = true;
        _statusMessage = null;

        var context = _context;
        var selectedGid = _selectedGroupGid;
        var venueName = _venueName.Trim();
        var venueHost = TrimToNull(_venueHost);
        var venueDescription = TrimToNull(_venueDescription);
        var venueWebsite = TrimToNull(_venueWebsite);
        var syncshellAlias = TrimToNull(_syncshellAlias);

        _ = Task.Run(async () =>
        {
            try
            {
                var location = new VenueLocationDto(context.Location.WorldId, context.Location.TerritoryId,
                    context.Location.DivisionId, context.Location.WardId, context.Location.PlotId, context.Location.RoomId,
                    context.Location.IsApartment);

                var request = new VenueRegistrationRequestDto(location, selectedGid, venueName)
                {
                    VenueDescription = venueDescription,
                    VenueWebsite = venueWebsite,
                    VenueHost = venueHost,
                    Alias = syncshellAlias,
                    HexString = GetVenueNameColourHex(),
                    IsFreeCompanyPlot = context.IsFreeCompanyPlot,
                    FreeCompanyTag = context.FreeCompanyTag,
                    OwnerName = context.OwnerName
                };

                var response = await _apiController.VenueRegister(request).ConfigureAwait(false);
                _statusMessage = response.Success
                    ? (response.WasUpdate ? "Venue registration updated." : "Venue registration submitted.")
                    : (response.ErrorMessage ?? "Registration failed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit venue registration");
                _statusMessage = "An error occurred while submitting the registration.";
            }
            finally
            {
                _isSubmitting = false;
            }
        });
    }
    
    private static string? TrimToNull(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
    

    private string GetSyncshellAliasOrDefault(string? gid)
    {
        return _adminGroups.FirstOrDefault(g => string.Equals(g.Group.GID, gid, StringComparison.Ordinal))?.Group.AliasOrGID
               ?? string.Empty;
    }

    private static Vector3 ParseHexColourOrDefault(string? hex, Vector3 fallback)
    {
        if (hex != null && hex.Length == 6)
        {
            var colour = Colours.Hex2Vector4(hex);
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

    private void BeginPrefillExistingVenue()
    {
        if (_context == null || !_apiController.IsConnected)
        {
            ApplyOwnerDefaults();
            return;
        }

        var location = new VenueLocationDto(_context.Location.WorldId, _context.Location.TerritoryId,
            _context.Location.DivisionId, _context.Location.WardId, _context.Location.PlotId, _context.Location.RoomId,
            _context.Location.IsApartment);

        var cancellation = new CancellationTokenSource();
        _prefillCancellation = cancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                var response = await _apiController.VenueGetInfoForPlot(new VenueInfoRequestDto(location))
                    .ConfigureAwait(false);

                if (cancellation.IsCancellationRequested)
                    return;

                if (response.HasVenue && response.Venue != null)
                {
                    _venueName = response.Venue.VenueName ?? string.Empty;
                    _venueDescription = response.Venue.VenueDescription ?? string.Empty;
                    _venueWebsite = response.Venue.VenueWebsite ?? string.Empty;
                    _venueHost = response.Venue.VenueHost ?? string.Empty;
                    _syncshellAlias = response.Venue.JoinInfo.Group.AliasOrGID;

                    _venueNameColour = ParseHexColourOrDefault(response.Venue.HexString, Vector3.One);
                    _isEditingExistingVenue = true;
                    
                    var existingGid = response.Venue.JoinInfo.Group.GID;
                    if (_adminGroups.Any(g => string.Equals(g.Group.GID, existingGid, StringComparison.Ordinal)))
                        _selectedGroupGid = existingGid;
                }
                else
                {
                    ApplyOwnerDefaults();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prefill existing venue info");
                ApplyOwnerDefaults();
            }
            finally
            {
                cancellation.Dispose();

                if (ReferenceEquals(_prefillCancellation, cancellation))
                    _prefillCancellation = null;
            }
        }, cancellation.Token);
    }

    private void ApplyOwnerDefaults()
    {
        if (_context == null)
            return;

        if (string.IsNullOrWhiteSpace(_venueName))
            _venueName = _context.OwnerName ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_venueHost) && !string.IsNullOrWhiteSpace(_context.OwnerName))
            _venueHost = _context.OwnerName!;
    }
}