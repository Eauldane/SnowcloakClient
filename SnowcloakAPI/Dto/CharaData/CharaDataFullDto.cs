using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.CharaData;

[MessagePackObject(keyAsPropertyName: true)]
public record CharaDataFullDto(string Id, UserData Uploader) : CharaDataDto(Id, Uploader)
{
    public DateTime CreatedDate { get; init; }
    public DateTime ExpiryDate { get; set; }
    public string GlamourerData { get; set; } = string.Empty;
    public string CustomizeData { get; set; } = string.Empty;
    public string ManipulationData { get; set; } = string.Empty;
    public int DownloadCount { get; set; } = 0;
    public List<UserData> AllowedUsers { get; set; } = [];
    public List<GroupData> AllowedGroups { get; set; } = [];
    public List<GamePathEntry> FileGamePaths { get; set; } = [];
    public List<GamePathEntry> FileSwaps { get; set; } = [];
    public List<GamePathEntry> OriginalFiles { get; set; } = [];
    public AccessTypeDto AccessType { get; set; }
    public ShareTypeDto ShareType { get; set; }
    public List<PoseEntry> PoseData { get; set; } = [];
}

[MessagePackObject(keyAsPropertyName: true)]
public record GamePathEntry(string HashOrFileSwap, string GamePath);

[MessagePackObject(keyAsPropertyName: true)]
public record PoseEntry(long? Id)
{
    public string? Description { get; set; } = string.Empty;
    public string? PoseData { get; set; } = string.Empty;
    public WorldData? WorldData { get; set; }
}

[MessagePackObject]
public record struct WorldData
{
    [Key(0)] public LocationInfo LocationInfo { get; set; }
    [Key(1)] public float PositionX { get; set; }
    [Key(2)] public float PositionY { get; set; }
    [Key(3)] public float PositionZ { get; set; }
    [Key(4)] public float RotationX { get; set; }
    [Key(5)] public float RotationY { get; set; }
    [Key(6)] public float RotationZ { get; set; }
    [Key(7)] public float RotationW { get; set; }
    [Key(8)] public float ScaleX { get; set; }
    [Key(9)] public float ScaleY { get; set; }
    [Key(10)] public float ScaleZ { get; set; }
}

[MessagePackObject]
public record struct LocationInfo
{
    [Key(0)] public uint ServerId { get; set; }
    [Key(1)] public uint MapId { get; set; }
    [Key(2)] public uint TerritoryId { get; set; }
    [Key(3)] public uint DivisionId { get; set; }
    [Key(4)] public uint WardId { get; set; }
    [Key(5)] public uint HouseId { get; set; }
    [Key(6)] public uint RoomId { get; set; }
}

[MessagePackObject]
public record struct PoseData
{
    [Key(0)] public bool IsDelta { get; set; }
    [Key(1)] public Dictionary<string, BoneData> Bones { get; set; }
    [Key(2)] public Dictionary<string, BoneData> MainHand { get; set; }
    [Key(3)] public Dictionary<string, BoneData> OffHand { get; set; }
    [Key(4)] public BoneData ModelDifference { get; set; }
}

[MessagePackObject]
public record struct BoneData
{
    [Key(0)] public bool Exists { get; set; }
    [Key(1)] public float PositionX { get; set; }
    [Key(2)] public float PositionY { get; set; }
    [Key(3)] public float PositionZ { get; set; }
    [Key(4)] public float RotationX { get; set; }
    [Key(5)] public float RotationY { get; set; }
    [Key(6)] public float RotationZ { get; set; }
    [Key(7)] public float RotationW { get; set; }
    [Key(8)] public float ScaleX { get; set; }
    [Key(9)] public float ScaleY { get; set; }
    [Key(10)] public float ScaleZ { get; set; }
}