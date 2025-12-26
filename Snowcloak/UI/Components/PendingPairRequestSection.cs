using System.Numerics;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data;
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
            .Where(r => !IsMalformed(r))
            .Select(dto => BuildPendingRequestDisplay(dto))
            .ToList();

        if (pending.Count == 0)
            return;

        var title = $"Pair Requests ({pending.Count})";
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

        ImGui.TextUnformatted("Notes will be auto-filled with the sender's name when you accept.");

        if (ImGui.BeginTable("pair-request-table", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg))
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
                var requestedAtTemplate = "Requested at {0:HH:mm:ss}";
                UiSharedService.AttachToolTip(string.Format(requestedAtTemplate, request.Request.RequestedAt));
                ImGui.TableSetColumnIndex(1);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserPlus, "Add"))
                {
                    _ = _pairRequestService.RespondAsync(request.Request, true);
                }

                ImGui.SameLine();

                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, "Reject"))
                {
                    _ = _pairRequestService.RespondAsync(request.Request, false, "Rejected by user");
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
        var requesterData = dto.Requester ?? new UserData(string.Empty);
        var requester = _pairRequestService.GetRequesterDisplay(dto);
        string? worldName = null;
        var hasWorld = requester.WorldId.HasValue
                       && _uiSharedService.WorldData != null
                       && _uiSharedService.WorldData.TryGetValue(requester.WorldId.Value, out worldName);
        var hasIdentName = !string.IsNullOrWhiteSpace(requester.Name)
                           && !string.Equals(requester.Name, requesterData.UID, StringComparison.Ordinal);
        var requesterName = hasIdentName ? requester.Name : null;

        if (hasIdentName && hasWorld && !string.IsNullOrWhiteSpace(worldName))
        {
            requesterName += $" @ {worldName}";
        }

        var requesterUid = !string.IsNullOrWhiteSpace(requesterData.UID)
            ? requesterData.UID
            : dto.RequesterIdent;

        var note = !string.IsNullOrWhiteSpace(requesterUid)
            ? _serverManager.GetNoteForUid(requesterUid!)
            : null;

        var aliasOrUid = !string.IsNullOrWhiteSpace(requesterData.AliasOrUID)
            ? requesterData.AliasOrUID
            : !string.IsNullOrWhiteSpace(requesterUid)
                ? requesterUid
                : dto.RequestId.ToString();

        var displayName = !string.IsNullOrWhiteSpace(note)
            ? note!
            : requesterName ?? aliasOrUid;
        
        var showAlias = string.IsNullOrWhiteSpace(note)
                        && requesterName != null
                        && !string.Equals(aliasOrUid, displayName, StringComparison.Ordinal);
        
        return new PendingPairRequestDisplay(dto, displayName, aliasOrUid, showAlias);
    }

    private static bool IsMalformed(PairingRequestDto dto)
    {
        var uid = dto.Requester?.UID;
        return string.IsNullOrWhiteSpace(dto.RequesterIdent) && string.IsNullOrWhiteSpace(uid);
    }
    
}

public readonly record struct PendingPairRequestDisplay(
    PairingRequestDto Request,
    string DisplayName,
    string AliasOrUid,
    bool ShowAlias);
