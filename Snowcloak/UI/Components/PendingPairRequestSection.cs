using System.Numerics;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Dto.User;
using Snowcloak.Services;
using Snowcloak.Services.Localisation;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Handlers;

namespace Snowcloak.UI.Components;

public sealed class PendingPairRequestSection
{
    private readonly PairRequestService _pairRequestService;
    private readonly ServerConfigurationManager _serverManager;
    private readonly UiSharedService _uiSharedService;
    private readonly LocalisationService _localisationService;

    public PendingPairRequestSection(
        PairRequestService pairRequestService,
        ServerConfigurationManager serverManager,
        UiSharedService uiSharedService,
        LocalisationService localisationService)
    {
        _pairRequestService = pairRequestService;
        _serverManager = serverManager;
        _uiSharedService = uiSharedService;
        _localisationService = localisationService;
    }

    public int PendingCount => _pairRequestService.PendingRequests.Count;

    public void Draw(TagHandler? tagHandler, string localisationPrefix, bool indent = false, bool collapsibleWhenNoTag = true)
    {
        var pending = _pairRequestService.PendingRequests
            .OrderBy(r => r.RequestedAt)
            .Select(dto => BuildPendingRequestDisplay(dto))
            .ToList();

        if (pending.Count == 0)
            return;

        var title = string.Format(L(localisationPrefix, "PairRequests.Title", "Pair Requests ({0})"), pending.Count);

        var isOpen = tagHandler?.IsTagOpen(TagHandler.CustomPairRequestsTag) ?? true;
        var usedCollapsingHeader = false;

        if (tagHandler == null && collapsibleWhenNoTag)
        {
            usedCollapsingHeader = true;
            isOpen = ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen);
        }
        else if (tagHandler != null)
        {
            var icon = isOpen ? FontAwesomeIcon.CaretSquareDown : FontAwesomeIcon.CaretSquareRight;
            _uiSharedService.IconText(icon);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                isOpen = !isOpen;
                tagHandler.SetTagOpen(TagHandler.CustomPairRequestsTag, isOpen);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(title);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                isOpen = !isOpen;
                tagHandler.SetTagOpen(TagHandler.CustomPairRequestsTag, isOpen);
            }
        }

        if (!isOpen)
        {
            ImGui.Separator();
            return;
        }

        if (indent)
            ImGui.Indent(20 * ImGuiHelpers.GlobalScale);

        ImGui.TextUnformatted(L(localisationPrefix, "PairRequests.NoteInfo", "Notes will be auto-filled with the sender's name when you accept."));

        if (ImGui.BeginTable($"pair-request-table-{localisationPrefix}", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg))
        {
            foreach (var request in pending)
            {
                using var requestId = ImRaii.PushId(request.Request.RequestId.ToString());
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(request.DisplayName);
                if (request.ShowAlias)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"({request.AliasOrUid})");
                }
                UiSharedService.AttachToolTip(string.Format(L(localisationPrefix, "PairRequests.RequestedAt", "Requested at {0:HH:mm:ss}"), request.Request.RequestedAt));

                ImGui.TableSetColumnIndex(1);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserPlus, L(localisationPrefix, "PairRequests.Accept", "Add")))
                {
                    _ = _pairRequestService.RespondAsync(request.Request, true);
                }

                ImGui.SameLine();

                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, L(localisationPrefix, "PairRequests.Reject", "Reject")))
                {
                    _ = _pairRequestService.RespondAsync(request.Request, false, L(localisationPrefix, "PairRequests.RejectReason", "Rejected by user"));
                }
            }

            ImGui.EndTable();
        }

        if (indent)
            ImGui.Unindent(20 * ImGuiHelpers.GlobalScale);

        ImGui.Separator();

        if (!usedCollapsingHeader && tagHandler == null)
            ImGuiHelpers.ScaledDummy(new Vector2(0, 2));
    }

    private PendingPairRequestDisplay BuildPendingRequestDisplay(PairingRequestDto dto)
    {
        var requester = _pairRequestService.GetRequesterDisplay(dto);
        string? worldName = null;
        var hasWorld = requester.WorldId.HasValue && _uiSharedService.WorldData.TryGetValue(requester.WorldId.Value, out worldName);

        var hasIdentName = !string.IsNullOrWhiteSpace(requester.Name)
                           && !string.Equals(requester.Name, dto.Requester.UID, StringComparison.Ordinal);
        var requesterName = hasIdentName ? requester.Name : null;

        if (hasIdentName && hasWorld && !string.IsNullOrWhiteSpace(worldName))
        {
            requesterName += $" @ {worldName}";
        }

        var note = _serverManager.GetNoteForUid(dto.Requester.UID);
        var displayName = !string.IsNullOrWhiteSpace(note)
            ? note!
            : requesterName ?? dto.Requester.AliasOrUID;

        var showAlias = string.IsNullOrWhiteSpace(note)
                        && requesterName != null
                        && !string.Equals(dto.Requester.AliasOrUID, displayName, StringComparison.Ordinal);

        return new PendingPairRequestDisplay(dto, displayName, dto.Requester.AliasOrUID, showAlias);
    }

    private string L(string prefix, string key, string fallback)
    {
        return _localisationService.GetString($"{prefix}.{key}", fallback);
    }
}

public readonly record struct PendingPairRequestDisplay(
    PairingRequestDto Request,
    string DisplayName,
    string AliasOrUid,
    bool ShowAlias);
