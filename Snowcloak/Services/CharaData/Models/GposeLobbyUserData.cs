using Dalamud.Utility;
using ElezenTools.Data;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using System.Globalization;
using System.Numerics;
using System.Text;

using ElezenTools.Services;

namespace Snowcloak.Services.CharaData.Models;

public sealed record GposeLobbyUserData(UserData UserData)
{
    public void Reset()
    {
        HasWorldDataUpdate = WorldData != null;
        HasPoseDataUpdate = ApplicablePoseData != null;
        SpawnedVfxId = null;
        LastAppliedCharaDataDate = DateTime.MinValue;
    }

    private WorldData? _worldData;
    public WorldData? WorldData
    {
        get => _worldData; set
        {
            _worldData = value;
            HasWorldDataUpdate = true;
        }
    }

    public bool HasWorldDataUpdate { get; set; }

    private PoseData? _fullPoseData;

    public PoseData? FullPoseData
    {
        get => _fullPoseData;
        set
        {
            _fullPoseData = value;
            ApplicablePoseData = value;
            HasPoseDataUpdate = true;
        }
    }

    public PoseData? ApplicablePoseData { get; private set; }
    public bool HasPoseDataUpdate { get; set; }
    public Guid? SpawnedVfxId { get; set; }
    public Vector3? LastWorldPosition { get; set; }
    public Vector3? TargetWorldPosition { get; set; }
    public DateTime? UpdateStart { get; set; }
    private CharaDataDownloadDto? _charaData;
    public CharaDataDownloadDto? CharaData
    {
        get => _charaData; set
        {
            _charaData = value;
            LastUpdatedCharaData = _charaData?.UpdatedDate ?? DateTime.MaxValue;
        }
    }

    public DateTime LastUpdatedCharaData { get; private set; } = DateTime.MaxValue;
    public DateTime LastAppliedCharaDataDate { get; set; } = DateTime.MinValue;
    public nint Address { get; set; }
    public string AssociatedCharaName { get; set; } = string.Empty;

    public string WorldDataDescriptor { get; private set; } = string.Empty;
    public Vector2 MapCoordinates { get; private set; }
    public Lumina.Excel.Sheets.Map Map { get; private set; }
    public HandledCharaDataEntry? HandledChara { get; set; }

    public async Task SetWorldDataDescriptor(DalamudUtilService dalamudUtilService)
    {
        if (WorldData == null)
        {
            WorldDataDescriptor = "No World Data found";
            return;
        }

        var worldData = WorldData!.Value;
        MapCoordinates = await Service.RunOnFrameworkAsync(() =>
                MapUtil.WorldToMap(new Vector2(worldData.PositionX, worldData.PositionY), dalamudUtilService.Maps[worldData.LocationInfo.MapId].Map))
            .ConfigureAwait(false);
        Map = dalamudUtilService.Maps[worldData.LocationInfo.MapId].Map;

        StringBuilder sb = new();
        sb.AppendLine("Server: " + ElezenData.Worlds.GetById(worldData.LocationInfo.ServerId)?.Name);
        sb.AppendLine("Territory: " + ElezenData.Locations.GetByTerritoryId(worldData.LocationInfo.TerritoryId)?.Name ?? "Unknown");
        sb.AppendLine("Map: " + dalamudUtilService.Maps[worldData.LocationInfo.MapId].MapName);

        if (worldData.LocationInfo.WardId != 0)
            sb.AppendLine("Ward #: " + worldData.LocationInfo.WardId);
        if (worldData.LocationInfo.DivisionId != 0)
        {
            sb.AppendLine("Subdivision: " + worldData.LocationInfo.DivisionId switch
            {
                1 => "No",
                2 => "Yes",
                _ => "-"
            });
        }
        if (worldData.LocationInfo.HouseId != 0)
        {
            sb.AppendLine("House #: " + (worldData.LocationInfo.HouseId == 100 ? "Apartments" : worldData.LocationInfo.HouseId.ToString()));
        }
        if (worldData.LocationInfo.RoomId != 0)
        {
            sb.AppendLine("Apartment #: " + worldData.LocationInfo.RoomId);
        }
        sb.AppendLine("Coordinates: X: " + MapCoordinates.X.ToString("0.0", CultureInfo.InvariantCulture) + ", Y: " + MapCoordinates.Y.ToString("0.0", CultureInfo.InvariantCulture));
        WorldDataDescriptor = sb.ToString();
    }
}
