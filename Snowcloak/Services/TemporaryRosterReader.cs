using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Snowcloak.API.Dto.TemporaryAppearance;

namespace Snowcloak.Services;

public interface ITemporaryRosterReader
{
    bool TryCapture(out TemporaryRosterCapture capture);
}

public readonly record struct TemporaryRosterCapture(TemporaryAppearanceSourceKind Source,
    TemporaryAppearanceLayoutKind Layout, ulong[] ContentIds);

public sealed unsafe class TemporaryRosterReader(IPartyList partyList, IPlayerState playerState)
    : ITemporaryRosterReader
{
    private const int PartyCapacity = 8;
    private const int StandardAllianceGroupCount = 3;
    private const int StandardAllianceAdditionalCapacity = 16;
    private const int AllianceStorageCapacity = 20;

    public bool TryCapture(out TemporaryRosterCapture capture)
    {
        capture = default;
        if (!playerState.IsLoaded || playerState.ContentId == 0) return false;

        return InfoProxyCrossRealm.IsCrossRealmParty()
            ? TryCaptureCrossWorld(playerState.ContentId, out capture)
            : TryCaptureLocal(playerState.ContentId, out capture);
    }

    private static bool TryCaptureCrossWorld(ulong localContentId, out TemporaryRosterCapture capture)
    {
        capture = default;
        var proxy = InfoProxyCrossRealm.Instance();
        if (proxy == null || !proxy->IsInCrossRealmParty) return false;

        var alliance = InfoProxyCrossRealm.IsAllianceRaid();
        var groupCount = (int)proxy->GroupCount;
        var expectedGroupCount = alliance ? StandardAllianceGroupCount : 1;
        if (groupCount != expectedGroupCount || proxy->LocalPlayerGroupIndex >= groupCount) return false;

        var members = new List<ulong>(alliance ? PartyCapacity * StandardAllianceGroupCount : PartyCapacity);
        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            var memberCount = (int)InfoProxyCrossRealm.GetGroupMemberCount(groupIndex);
            if (memberCount > PartyCapacity
                || groupIndex == proxy->LocalPlayerGroupIndex && memberCount == 0
                || !alliance && memberCount < 2) return false;
            for (uint memberIndex = 0; memberIndex < memberCount; memberIndex++)
            {
                var member = InfoProxyCrossRealm.GetGroupMember(memberIndex, groupIndex);
                if (member == null || !TryAddUnique(members, member->ContentId)) return false;
            }
        }

        if (!ValidateCompleteRoster(members, localContentId)) return false;
        capture = new(alliance ? TemporaryAppearanceSourceKind.Alliance : TemporaryAppearanceSourceKind.Party,
            alliance ? TemporaryAppearanceLayoutKind.Alliance3x8 : TemporaryAppearanceLayoutKind.Party,
            members.Order().ToArray());
        return true;
    }

    private bool TryCaptureLocal(ulong localContentId, out TemporaryRosterCapture capture)
    {
        capture = default;
        if (!partyList.IsAlliance)
        {
            if (partyList.Length is < 2 or > PartyCapacity) return false;
            var members = new List<ulong>(partyList.Length);
            for (var index = 0; index < partyList.Length; index++)
            {
                var member = partyList.CreatePartyMemberReference(partyList.GetPartyMemberAddress(index));
                if (member == null || !TryAddUnique(members, member.ContentId)) return false;
            }
            if (members.Count != partyList.Length || !ValidateCompleteRoster(members, localContentId)) return false;
            capture = new(TemporaryAppearanceSourceKind.Party, TemporaryAppearanceLayoutKind.Party,
                members.Order().ToArray());
            return true;
        }

        var groupManager = (GroupManager*)partyList.GroupManagerAddress;
        if (groupManager == null || !groupManager->MainGroup.IsAlliance
            || groupManager->MainGroup.IsSmallGroupAlliance) return false;

        // This is the current occupied roster count. It may be anywhere from 2 through 24 for a
        // standard alliance; the loops below scan capacity and deliberately skip empty slots.
        var declaredMemberCount = (int)groupManager->MainGroup.MemberCount;
        if (declaredMemberCount is < 2 or > PartyCapacity * StandardAllianceGroupCount) return false;

        var allianceMembers = new List<ulong>(PartyCapacity * StandardAllianceGroupCount);
        for (var index = 0; index < PartyCapacity; index++)
        {
            var member = partyList.CreatePartyMemberReference(partyList.GetPartyMemberAddress(index));
            if (member?.ContentId is > 0 && !TryAddUnique(allianceMembers, member.ContentId)) return false;
        }
        for (var index = 0; index < AllianceStorageCapacity; index++)
        {
            var member = partyList.CreateAllianceMemberReference(partyList.GetAllianceMemberAddress(index));
            var contentId = member?.ContentId ?? 0;
            if (index >= StandardAllianceAdditionalCapacity)
            {
                if (contentId != 0) return false;
                continue;
            }
            if (contentId > 0 && !TryAddUnique(allianceMembers, contentId)) return false;
        }

        if (allianceMembers.Count != declaredMemberCount
            || !ValidateCompleteRoster(allianceMembers, localContentId)) return false;
        capture = new(TemporaryAppearanceSourceKind.Alliance, TemporaryAppearanceLayoutKind.Alliance3x8,
            allianceMembers.Order().ToArray());
        return true;
    }

    private static bool TryAddUnique(List<ulong> members, ulong contentId)
    {
        if (contentId == 0 || members.Contains(contentId)) return false;
        members.Add(contentId);
        return true;
    }

    private static bool ValidateCompleteRoster(IReadOnlyCollection<ulong> members, ulong localContentId)
        => members.Count >= 2 && members.Count(member => member == localContentId) == 1;
}
