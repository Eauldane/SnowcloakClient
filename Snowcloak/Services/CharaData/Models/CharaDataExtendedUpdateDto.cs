using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.Core.CharaData;

namespace Snowcloak.Services.CharaData.Models;

public sealed class CharaDataExtendedUpdateDto
{
    private readonly CharaDataEditSession _session;

    public CharaDataExtendedUpdateDto(CharaDataUpdateDto dto, CharaDataFullDto charaDataFullDto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        _session = new CharaDataEditSession(charaDataFullDto);
    }

    public string Id => _session.Id;
    public CharaDataChangeSet ChangeSet => _session.ChangeSet;
    public CharaDataUpdateDto BaseDto => _session.ToUpdateDto();
    public IEnumerable<UserData> UserList => _session.Users;
    public IEnumerable<GroupData> GroupList => _session.Groups;
    public IEnumerable<PoseEntry> PoseList => _session.Poses;
    public bool HasChanges => _session.HasChanges;
    public bool IsAppearanceEqual => _session.IsAppearanceEqual;

    public string ManipulationData
    {
        get => _session.ManipulationData;
        set => _session.ManipulationData = value ?? string.Empty;
    }

    public string Description
    {
        get => _session.Description;
        set => _session.Description = value ?? string.Empty;
    }

    public DateTime ExpiryDate => _session.ExpiryDate;

    public AccessTypeDto AccessType
    {
        get => _session.AccessType;
        set => _session.AccessType = value;
    }

    public ShareTypeDto ShareType
    {
        get => _session.ShareType;
        set => _session.ShareType = value;
    }

    public IReadOnlyList<GamePathEntry> FileGamePaths => _session.FileGamePaths;

    public IReadOnlyList<GamePathEntry> FileSwaps => _session.FileSwaps;

    public string? GlamourerData
    {
        get => _session.GlamourerData;
        set => _session.GlamourerData = value ?? string.Empty;
    }

    public string? CustomizeData
    {
        get => _session.CustomizeData;
        set => _session.CustomizeData = value ?? string.Empty;
    }

    public void AddUserToList(string user) => _session.AddUser(user);

    public void AddGroupToList(string group) => _session.AddGroup(group);

    public void RemoveUserFromList(string user) => _session.RemoveUser(user);

    public void RemoveGroupFromList(string group) => _session.RemoveGroup(group);

    public void SetFileGamePaths(IEnumerable<GamePathEntry> entries) => _session.SetFileGamePaths(entries);

    public void SetFileSwaps(IEnumerable<GamePathEntry> entries) => _session.SetFileSwaps(entries);

    public void AddPose() => _session.AddPose();

    public void RemovePose(PoseEntry entry) => _session.RemovePose(entry);

    public bool UpdatePoseList() => ChangeSet.Poses;

    public void SetExpiry(bool expiring) => _session.SetExpiry(expiring);

    public void SetExpiry(int year, int month, int day) => _session.SetExpiry(year, month, day);

    internal void UndoChanges() => _session.UndoChanges();

    internal void RevertDeletion(PoseEntry pose) => _session.RevertPose(pose);

    internal bool PoseHasChanges(PoseEntry pose) => _session.PoseHasChanges(pose);
}
