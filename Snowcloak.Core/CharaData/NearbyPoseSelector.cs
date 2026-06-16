using System.Numerics;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;

namespace Snowcloak.Core.CharaData;

public sealed record NearbyPoseCandidate<TPose>(
    TPose Pose,
    UserData Uploader,
    bool IsOwnData,
    bool HasPoseData,
    bool HasWorldData,
    LocationInfo Location,
    Vector3 Position);

public sealed record NearbyPoseContext(
    LocationInfo CurrentLocation,
    uint CurrentWorldId,
    Vector3 PlayerPosition,
    Vector3 CameraPosition,
    Vector3 CameraLookAt,
    string NoteFilter,
    string ConfiguredNote,
    bool IgnoreHousingLimits,
    bool OnlyCurrentServer,
    bool ShowOwnData,
    float DistanceLimit);

public sealed record NearbyPoseResult<TPose>(TPose Pose, float Direction, float Distance);

public static class NearbyPoseSelector
{
    public static IReadOnlyList<NearbyPoseResult<TPose>> Select<TPose>(
        IEnumerable<NearbyPoseCandidate<TPose>> candidates,
        NearbyPoseContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);

        var cameraYaw = GetCameraYaw(context.CameraPosition, context.CameraLookAt);
        var hasNoteFilter = !string.IsNullOrWhiteSpace(context.NoteFilter);
        var distanceLimitSquared = context.DistanceLimit * context.DistanceLimit;
        var result = new List<NearbyPoseResult<TPose>>();

        foreach (var candidate in candidates)
        {
            if (!candidate.HasPoseData || !candidate.HasWorldData || (!context.ShowOwnData && candidate.IsOwnData))
            {
                continue;
            }

            if (hasNoteFilter && !MatchesNoteFilter(candidate.Uploader, context.NoteFilter, context.ConfiguredNote))
            {
                continue;
            }

            if (!IsSamePlayablePlace(candidate.Location, context))
            {
                continue;
            }

            var distanceSquared = Vector3.DistanceSquared(context.PlayerPosition, candidate.Position);
            if (distanceSquared > distanceLimitSquared)
            {
                continue;
            }

            result.Add(new NearbyPoseResult<TPose>(
                candidate.Pose,
                GetAngleToTarget(context.CameraPosition, cameraYaw, candidate.Position),
                MathF.Sqrt(distanceSquared)));
        }

        return result;
    }

    public static float CalculateYawDegrees(Vector3 directionXZ)
    {
        var yawDegrees = MathF.Atan2(-directionXZ.X, directionXZ.Z) * (180f / MathF.PI);
        return yawDegrees < 0 ? yawDegrees + 360f : yawDegrees;
    }

    public static float GetAngleToTarget(Vector3 cameraPosition, float cameraYawDegrees, Vector3 targetPosition)
    {
        var directionToTarget = targetPosition - cameraPosition;
        var directionToTargetXz = new Vector3(directionToTarget.X, 0, directionToTarget.Z);
        if (directionToTargetXz.LengthSquared() < 1e-10f)
        {
            return 0;
        }

        directionToTargetXz = Vector3.Normalize(directionToTargetXz);
        var targetYawDegrees = CalculateYawDegrees(directionToTargetXz);
        var relativeAngle = targetYawDegrees - cameraYawDegrees;
        return relativeAngle < 0 ? relativeAngle + 360f : relativeAngle;
    }

    public static float GetCameraYaw(Vector3 cameraPosition, Vector3 lookAtVector)
    {
        var directionFacing = lookAtVector - cameraPosition;
        var directionFacingXz = new Vector3(directionFacing.X, 0, directionFacing.Z);
        if (directionFacingXz.LengthSquared() < 1e-10f)
        {
            directionFacingXz = new Vector3(0, 0, 1);
        }
        else
        {
            directionFacingXz = Vector3.Normalize(directionFacingXz);
        }

        return CalculateYawDegrees(directionFacingXz);
    }

    private static bool MatchesNoteFilter(UserData uploader, string noteFilter, string configuredNote)
    {
        var alias = uploader.Alias ?? string.Empty;
        return alias.Contains(noteFilter, StringComparison.OrdinalIgnoreCase)
            || uploader.UID.Contains(noteFilter, StringComparison.OrdinalIgnoreCase)
            || configuredNote.Contains(noteFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePlayablePlace(LocationInfo poseLocation, NearbyPoseContext context)
    {
        if (poseLocation.TerritoryId != context.CurrentLocation.TerritoryId)
        {
            return false;
        }

        var isHousing = poseLocation.WardId != 0;
        if (!isHousing)
        {
            return poseLocation.MapId == context.CurrentLocation.MapId
                && (!context.OnlyCurrentServer || poseLocation.ServerId == context.CurrentWorldId);
        }

        var serverMatches = poseLocation.ServerId == context.CurrentWorldId;
        if (context.OnlyCurrentServer && !serverMatches)
        {
            return false;
        }

        if (!serverMatches && !context.IgnoreHousingLimits)
        {
            return false;
        }

        if (poseLocation.HouseId == 0)
        {
            return poseLocation.DivisionId == context.CurrentLocation.DivisionId
                && (context.IgnoreHousingLimits || poseLocation.WardId == context.CurrentLocation.WardId);
        }

        return context.IgnoreHousingLimits
            || poseLocation.HouseId == context.CurrentLocation.HouseId
            && poseLocation.WardId == context.CurrentLocation.WardId
            && poseLocation.DivisionId == context.CurrentLocation.DivisionId
            && poseLocation.RoomId == context.CurrentLocation.RoomId;
    }
}
