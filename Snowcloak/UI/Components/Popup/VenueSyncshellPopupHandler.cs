using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Threading.Tasks;
using Snowcloak.Services.Venue;
using Dalamud.Utility;
using Snowcloak.Utils;
using Snowcloak.Services.Housing;
using System.Text;

namespace Snowcloak.UI.Components.Popup;

internal class VenueSyncshellPopupHandler : IPopupHandler
{
    private readonly UiSharedService _uiSharedService;
    private readonly VenueSyncshellService _venueSyncshellService;
    private bool _closeOnSuccess;
    private bool _isJoining;
    private bool _joinFailed;
    private VenueSyncshellPrompt? _prompt;

    public VenueSyncshellPopupHandler(UiSharedService uiSharedService, VenueSyncshellService venueSyncshellService)
    {
        _uiSharedService = uiSharedService;
        _venueSyncshellService = venueSyncshellService;
    }

    public Vector2 PopupSize => new(550, 450);
    public bool ShowClose => false;

    public void DrawContent()
    {
        if (_prompt == null) return;

        if (_closeOnSuccess)
        {
            ImGui.CloseCurrentPopup();
            _closeOnSuccess = false;
        }

        var venue = _prompt.Venue;

        using (_uiSharedService.UidFont.Push())
            UiSharedService.ColorText(venue.VenueName, Colours.Hex2Vector4(venue.JoinInfo.Group.DisplayColour));

        ImGuiHelpers.ScaledDummy(5f);

        if (ImGui.BeginTable("venue_info_table", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
        {
            AddInfoRow(FontAwesomeIcon.MapMarkedAlt, "Venue location", GetHousingPlotName(_prompt.Location));
            AddInfoRow(FontAwesomeIcon.User, "Host", venue.VenueHost);
            AddInfoRow(FontAwesomeIcon.Globe, "Website", venue.VenueWebsite, isLink: true);
            ImGui.EndTable();        
        }

        if (!string.IsNullOrWhiteSpace(venue.VenueDescription))
        {
            ImGuiHelpers.ScaledDummy(8f);
            UiSharedService.TextWrapped("About this venue");
            using var child = ImRaii.Child("##venue_description",
                new Vector2(-1, MathF.Max(100f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 3.75f)),
                true,
                ImGuiWindowFlags.AlwaysVerticalScrollbar);
            if (child)
            {
                UiSharedService.TextWrapped(venue.VenueDescription);
            }
        }
        UiSharedService.TextWrapped("This housing plot has a venue registered to it, and you have venue auto-joins enabled in settings.");
        UiSharedService.TextWrapped("Upon leaving, you will be removed from the syncshell within a few minutes. Snowcloak staff " +
                                    "are not responsible for the content of this venue.");


        if (_joinFailed)
        {
            UiSharedService.ColorTextWrapped("Failed to join syncshell. Please try again.", ImGuiColors.DalamudRed);
        }

        using (ImRaii.Disabled(_isJoining))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.SignInAlt, _isJoining ? "Joining..." : "Join syncshell"))
            {
                _joinFailed = false;
                _isJoining = true;
                var promptId = _prompt.PromptId;
                _ = Task.Run(async () =>
                {
                    var success = await _venueSyncshellService.JoinVenueShellAsync(promptId).ConfigureAwait(false);
                    _joinFailed = !success;
                    _closeOnSuccess = success;
                    _isJoining = false;
                });
            }
        }
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, "Close"))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    public void Open(VenueSyncshellPrompt prompt)
    {
        _prompt = prompt;
        _joinFailed = false;
        _isJoining = false;
        _closeOnSuccess = false;
    }
    
    
    private static void AddInfoRow(FontAwesomeIcon icon, string label, string? value, Vector4? valueColor = null, bool isLink = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextUnformatted(icon.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey2, label + ":");

        ImGui.TableNextColumn();
        if (valueColor.HasValue)
        {
            ImGui.TextColored(valueColor.Value, value);
        }
        else
        {
            ImGui.TextUnformatted(value);
        }

        if (isLink && ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
            {
                Util.OpenLink(value);
            }
        }
    }
    
    private string GetHousingPlotName(HousingPlotLocation location)
    {
        var worldName = _uiSharedService.WorldData.GetValueOrDefault((ushort)location.WorldId, location.WorldId.ToString());
        var territoryName = _uiSharedService.TerritoryData.GetValueOrDefault(location.TerritoryId, $"Territory {location.TerritoryId}");

        StringBuilder builder = new();
        builder.Append(worldName);
        builder.Append(" - ");
        builder.Append(territoryName);
        builder.Append(" - Ward ");
        builder.Append(location.WardId);
        if (location.IsApartment)
        {
            builder.Append(" Apartments");
            if (location.RoomId > 0)
                builder.Append($" Room {location.RoomId}");
        }
        else
        {
            builder.Append($" Plot {location.PlotId}");
        }

        return builder.ToString();
    }
}