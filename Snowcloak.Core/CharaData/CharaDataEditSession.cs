using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;

namespace Snowcloak.Core.CharaData;

public sealed class CharaDataEditSession
{
    private readonly Baseline _baseline;
    private readonly List<UserData> _users;
    private readonly List<GroupData> _groups;
    private readonly List<PoseEntry> _poses;

    private string? _description;
    private DateTime? _expiryDate;
    private string? _glamourerData;
    private string? _customizeData;
    private string? _manipulationData;
    private List<GamePathEntry>? _fileGamePaths;
    private List<GamePathEntry>? _fileSwaps;
    private AccessTypeDto? _accessType;
    private ShareTypeDto? _shareType;

    public CharaDataEditSession(CharaDataFullDto baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        _baseline = Baseline.From(baseline);
        _users = baseline.AllowedUsers.ToList();
        _groups = baseline.AllowedGroups.ToList();
        _poses = ClonePoses(baseline.PoseData);
        Id = baseline.Id;
    }

    public string Id { get; }
    public IReadOnlyList<UserData> Users => _users;
    public IReadOnlyList<GroupData> Groups => _groups;
    public IReadOnlyList<PoseEntry> Poses => _poses;

    public string Description
    {
        get => _description ?? _baseline.Description;
        set => _description = Same(value, _baseline.Description) ? null : value;
    }

    public DateTime ExpiryDate
    {
        get => _expiryDate ?? _baseline.ExpiryDate;
        set => _expiryDate = value == _baseline.ExpiryDate ? null : value;
    }

    public AccessTypeDto AccessType
    {
        get => _accessType ?? _baseline.AccessType;
        set
        {
            _accessType = value == _baseline.AccessType ? null : value;
            if (value == AccessTypeDto.Public && ShareType == ShareTypeDto.Shared)
            {
                ShareType = ShareTypeDto.Private;
            }
        }
    }

    public ShareTypeDto ShareType
    {
        get => _shareType ?? _baseline.ShareType;
        set
        {
            var effective = value == ShareTypeDto.Shared && AccessType == AccessTypeDto.Public
                ? ShareTypeDto.Private
                : value;
            _shareType = effective == _baseline.ShareType ? null : effective;
        }
    }

    public IReadOnlyList<GamePathEntry> FileGamePaths => _fileGamePaths ?? _baseline.FileGamePaths;

    public IReadOnlyList<GamePathEntry> FileSwaps => _fileSwaps ?? _baseline.FileSwaps;

    public string GlamourerData
    {
        get => _glamourerData ?? _baseline.GlamourerData;
        set => _glamourerData = Same(value, _baseline.GlamourerData) ? null : value;
    }

    public string CustomizeData
    {
        get => _customizeData ?? _baseline.CustomizeData;
        set => _customizeData = Same(value, _baseline.CustomizeData) ? null : value;
    }

    public string ManipulationData
    {
        get => _manipulationData ?? _baseline.ManipulationData;
        set => _manipulationData = Same(value, _baseline.ManipulationData) ? null : value;
    }

    public CharaDataChangeSet ChangeSet => new(
        _description != null,
        _expiryDate != null,
        _accessType != null,
        _shareType != null,
        !SameUserIds(_users.Select(user => user.UID), _baseline.AllowedUsers),
        !SameGroupIds(_groups.Select(group => group.GID), _baseline.AllowedGroups),
        _glamourerData != null,
        _customizeData != null,
        _manipulationData != null,
        _fileGamePaths != null,
        _fileSwaps != null,
        !SamePoses(_poses, _baseline.Poses));

    public bool HasChanges => ChangeSet.HasChanges;

    public bool IsAppearanceEqual =>
        Same(GlamourerData, _baseline.GlamourerData)
        && Same(CustomizeData, _baseline.CustomizeData)
        && Same(ManipulationData, _baseline.ManipulationData)
        && SameEntries(FileGamePaths, _baseline.FileGamePaths)
        && SameEntries(FileSwaps, _baseline.FileSwaps);

    public void SetFileGamePaths(IEnumerable<GamePathEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        _fileGamePaths = SameEntries(list, _baseline.FileGamePaths) ? null : list;
    }

    public void SetFileSwaps(IEnumerable<GamePathEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        _fileSwaps = SameEntries(list, _baseline.FileSwaps) ? null : list;
    }

    public void AddUser(string uid)
    {
        if (!_users.Any(user => Same(user.UID, uid) || Same(user.Alias, uid)))
        {
            _users.Add(new UserData(uid, null));
        }
    }

    public void RemoveUser(string uid)
    {
        _users.RemoveAll(user => Same(user.UID, uid));
    }

    public void AddGroup(string gid)
    {
        if (!_groups.Any(group => Same(group.GID, gid) || Same(group.Alias, gid)))
        {
            _groups.Add(new GroupData(gid, null));
        }
    }

    public void RemoveGroup(string gid)
    {
        _groups.RemoveAll(group => Same(group.GID, gid));
    }

    public PoseEntry AddPose()
    {
        var pose = new PoseEntry(null);
        _poses.Add(pose);
        return pose;
    }

    public void RemovePose(PoseEntry pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (pose.Id == null)
        {
            _poses.Remove(pose);
            return;
        }

        pose.Description = null;
        pose.PoseData = null;
        pose.WorldData = null;
    }

    public void RevertPose(PoseEntry pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (pose.Id == null)
        {
            return;
        }

        var baselinePose = _baseline.Poses.FirstOrDefault(entry => entry.Id == pose.Id);
        if (baselinePose == null)
        {
            return;
        }

        pose.Description = baselinePose.Description;
        pose.PoseData = baselinePose.PoseData;
        pose.WorldData = baselinePose.WorldData;
    }

    public bool PoseHasChanges(PoseEntry pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (pose.Id == null)
        {
            return false;
        }

        var baselinePose = _baseline.Poses.FirstOrDefault(entry => entry.Id == pose.Id);
        return baselinePose != null && !SamePose(pose, baselinePose);
    }

    public void SetExpiry(bool expiring)
    {
        if (!expiring)
        {
            ExpiryDate = DateTime.MaxValue;
            return;
        }

        var date = DateTime.UtcNow.AddDays(7);
        SetExpiry(date.Year, date.Month, date.Day);
    }

    public void SetExpiry(int year, int month, int day)
    {
        var daysInMonth = DateTime.DaysInMonth(year, month);
        if (day > daysInMonth)
        {
            day = 1;
        }

        ExpiryDate = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
    }

    public void UndoChanges()
    {
        _description = null;
        _expiryDate = null;
        _glamourerData = null;
        _customizeData = null;
        _manipulationData = null;
        _fileGamePaths = null;
        _fileSwaps = null;
        _accessType = null;
        _shareType = null;
        _users.Clear();
        _users.AddRange(_baseline.UserData);
        _groups.Clear();
        _groups.AddRange(_baseline.GroupData);
        _poses.Clear();
        _poses.AddRange(ClonePoses(_baseline.Poses));
    }

    public CharaDataUpdateDto ToUpdateDto()
    {
        var changes = ChangeSet;
        return new CharaDataUpdateDto(Id)
        {
            Description = changes.Description ? _description : null,
            ExpiryDate = changes.ExpiryDate ? _expiryDate : null,
            AccessType = changes.AccessType ? _accessType : null,
            ShareType = changes.ShareType ? _shareType : null,
            AllowedUsers = changes.AllowedUsers ? [.. _users.Select(user => user.UID)] : null,
            AllowedGroups = changes.AllowedGroups ? [.. _groups.Select(group => group.GID)] : null,
            GlamourerData = changes.GlamourerData ? _glamourerData : null,
            CustomizeData = changes.CustomizeData ? _customizeData : null,
            ManipulationData = changes.ManipulationData ? _manipulationData : null,
            FileGamePaths = changes.FileGamePaths ? _fileGamePaths : null,
            FileSwaps = changes.FileSwaps ? _fileSwaps : null,
            Poses = changes.Poses ? ClonePoses(_poses) : null,
        };
    }

    private static bool Same(string? left, string? right) => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static bool SameUserIds(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool SameGroupIds(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool SameEntries(IEnumerable<GamePathEntry> left, IEnumerable<GamePathEntry> right)
    {
        return left.OrderBy(entry => entry.HashOrFileSwap, StringComparer.Ordinal)
            .ThenBy(entry => entry.GamePath, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(entry => entry.HashOrFileSwap, StringComparer.Ordinal)
                .ThenBy(entry => entry.GamePath, StringComparer.Ordinal));
    }

    private static bool SamePoses(IEnumerable<PoseEntry> left, IEnumerable<PoseEntry> right)
    {
        return left.OrderBy(entry => entry.Id ?? long.MinValue)
            .ThenBy(entry => entry.Description, StringComparer.Ordinal)
            .ThenBy(entry => entry.PoseData, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(entry => entry.Id ?? long.MinValue)
                .ThenBy(entry => entry.Description, StringComparer.Ordinal)
                .ThenBy(entry => entry.PoseData, StringComparer.Ordinal), PoseEntryComparer.Instance);
    }

    private static bool SamePose(PoseEntry left, PoseEntry right) => PoseEntryComparer.Instance.Equals(left, right);

    private static List<PoseEntry> ClonePoses(IEnumerable<PoseEntry> poses)
    {
        return poses.Select(entry => new PoseEntry(entry.Id)
        {
            Description = entry.Description,
            PoseData = entry.PoseData,
            WorldData = entry.WorldData,
        }).ToList();
    }

    private sealed record Baseline(
        string Description,
        DateTime ExpiryDate,
        string GlamourerData,
        string CustomizeData,
        string ManipulationData,
        List<UserData> UserData,
        List<GroupData> GroupData,
        List<string> AllowedUsers,
        List<string> AllowedGroups,
        List<GamePathEntry> FileGamePaths,
        List<GamePathEntry> FileSwaps,
        AccessTypeDto AccessType,
        ShareTypeDto ShareType,
        List<PoseEntry> Poses)
    {
        public static Baseline From(CharaDataFullDto dto)
        {
            return new Baseline(
                dto.Description,
                dto.ExpiryDate,
                dto.GlamourerData,
                dto.CustomizeData,
                dto.ManipulationData,
                dto.AllowedUsers.ToList(),
                dto.AllowedGroups.ToList(),
                dto.AllowedUsers.Select(user => user.UID).ToList(),
                dto.AllowedGroups.Select(group => group.GID).ToList(),
                dto.FileGamePaths.ToList(),
                dto.FileSwaps.ToList(),
                dto.AccessType,
                dto.ShareType,
                ClonePoses(dto.PoseData));
        }
    }

    private sealed class PoseEntryComparer : IEqualityComparer<PoseEntry>
    {
        public static readonly PoseEntryComparer Instance = new();

        public bool Equals(PoseEntry? x, PoseEntry? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x == null || y == null)
            {
                return false;
            }

            return x.Id == y.Id
                && Same(x.Description, y.Description)
                && Same(x.PoseData, y.PoseData)
                && EqualityComparer<WorldData?>.Default.Equals(x.WorldData, y.WorldData);
        }

        public int GetHashCode(PoseEntry obj)
        {
            return HashCode.Combine(obj.Id, obj.Description ?? string.Empty, obj.PoseData ?? string.Empty, obj.WorldData);
        }
    }
}

public sealed record CharaDataChangeSet(
    bool Description,
    bool ExpiryDate,
    bool AccessType,
    bool ShareType,
    bool AllowedUsers,
    bool AllowedGroups,
    bool GlamourerData,
    bool CustomizeData,
    bool ManipulationData,
    bool FileGamePaths,
    bool FileSwaps,
    bool Poses)
{
    public bool HasChanges => Description
        || ExpiryDate
        || AccessType
        || ShareType
        || AllowedUsers
        || AllowedGroups
        || GlamourerData
        || CustomizeData
        || ManipulationData
        || FileGamePaths
        || FileSwaps
        || Poses;
}
