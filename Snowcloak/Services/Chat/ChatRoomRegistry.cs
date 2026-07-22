using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Chat;

namespace Snowcloak.Services.Chat;

public sealed class ChatRoomRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RoomData> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, RoomMemberDto>> _members = new(StringComparer.Ordinal);

    public IReadOnlyList<RoomData> ListRooms()
    {
        lock (_lock)
        {
            return _rooms.Values.OrderBy(room => room.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public IReadOnlyDictionary<string, int> SnapshotCounts()
    {
        lock (_lock)
        {
            return new Dictionary<string, int>(_counts, StringComparer.Ordinal);
        }
    }

    public void ReplaceRooms(IEnumerable<RoomDto> rooms, IReadOnlyDictionary<string, int> counts)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(counts);
        lock (_lock)
        {
            _rooms.Clear();
            foreach (var room in rooms)
            {
                _rooms[room.Room.RoomId] = room.Room;
            }

            _counts.Clear();
            foreach (var count in counts)
            {
                _counts[count.Key] = count.Value;
            }
        }
    }

    public void Upsert(RoomData room)
    {
        ArgumentNullException.ThrowIfNull(room);
        lock (_lock)
        {
            _rooms[room.RoomId] = room;
        }
    }

    public bool TryGet(string roomId, out RoomData room)
    {
        lock (_lock)
        {
            return _rooms.TryGetValue(roomId, out room!);
        }
    }

    public IReadOnlyList<RoomMemberDto> GetMembers(string roomId)
    {
        lock (_lock)
        {
            return _members.TryGetValue(roomId, out var members)
                ? members.Values.OrderByDescending(member => member.Role)
                    .ThenBy(member => member.SceneNickname ?? member.User.AliasOrUID, StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
        }
    }

    public void ReplaceMembers(string roomId, IEnumerable<RoomMemberDto> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        lock (_lock)
        {
            _members[roomId] = members.ToDictionary(member => member.User.UID, StringComparer.Ordinal);
        }
    }

    public void SetMember(RoomMemberJoinedDto member)
    {
        ArgumentNullException.ThrowIfNull(member);
        SetMember(new RoomMemberDto(member.Room, member.User, member.Role)
        {
            SceneNickname = member.SceneNickname,
            SceneRoleIconId = member.SceneRoleIconId,
            SceneRoleLabel = member.SceneRoleLabel,
        });
    }

    public void SetMember(RoomMemberDto member)
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (_lock)
        {
            _rooms[member.Room.RoomId] = member.Room;
            if (!_members.TryGetValue(member.Room.RoomId, out var members))
            {
                members = new Dictionary<string, RoomMemberDto>(StringComparer.Ordinal);
                _members[member.Room.RoomId] = members;
            }

            members[member.User.UID] = member;
            _counts[member.Room.RoomId] = members.Count;
        }
    }

    public void RemoveMember(RoomData room, string uid)
    {
        ArgumentNullException.ThrowIfNull(room);
        lock (_lock)
        {
            if (_members.TryGetValue(room.RoomId, out var members))
            {
                members.Remove(uid);
                _counts[room.RoomId] = members.Count;
            }
        }
    }
}
