using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Snowcloak.API.Dto.CharaData;

namespace Snowcloak.Core.CharaData;

public static class PoseDataCodec
{
    public static PoseData FromBrioJson(string json)
    {
        var pose = new PoseData
        {
            Bones = new(StringComparer.Ordinal),
            MainHand = new(StringComparer.Ordinal),
            OffHand = new(StringComparer.Ordinal),
        };

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return pose;
        }

        if (node is not JsonObject root)
            return pose;

        ParseSection(root, "Bones", pose.Bones);
        ParseSection(root, "MainHand", pose.MainHand);
        ParseSection(root, "OffHand", pose.OffHand);

        return pose;
    }

    public static string ToBrioJson(PoseData pose)
    {
        var node = new JsonObject
        {
            ["Bones"] = WriteSection(pose.Bones),
            ["MainHand"] = WriteSection(pose.MainHand),
            ["OffHand"] = WriteSection(pose.OffHand),
        };

        return node.ToJsonString();
    }

    private static void ParseSection(JsonObject root, string key, Dictionary<string, BoneData> target)
    {
        if (root[key] is not JsonObject section)
            return;

        foreach (var entry in section)
        {
            if (entry.Value is JsonObject boneJson && TryCreateBoneData(boneJson, out var bone))
                target[entry.Key] = bone;
        }
    }

    private static JsonObject WriteSection(Dictionary<string, BoneData>? bones)
    {
        var section = new JsonObject();
        if (bones is null)
            return section;

        foreach (var (name, bone) in bones)
            section[name] = WriteBone(bone);

        return section;
    }

    private static JsonObject WriteBone(BoneData bone)
    {
        return new JsonObject
        {
            ["Position"] = FormatTriple(bone.PositionX, bone.PositionY, bone.PositionZ),
            ["Scale"] = FormatTriple(bone.ScaleX, bone.ScaleY, bone.ScaleZ),
            ["Rotation"] = FormatQuad(bone.RotationX, bone.RotationY, bone.RotationZ, bone.RotationW),
        };
    }

    private static string FormatTriple(float x, float y, float z)
        => $"{x.ToString(CultureInfo.InvariantCulture)}, {y.ToString(CultureInfo.InvariantCulture)}, {z.ToString(CultureInfo.InvariantCulture)}";

    private static string FormatQuad(float x, float y, float z, float w)
        => $"{x.ToString(CultureInfo.InvariantCulture)}, {y.ToString(CultureInfo.InvariantCulture)}, {z.ToString(CultureInfo.InvariantCulture)}, {w.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryCreateBoneData(JsonObject boneJson, out BoneData bone)
    {
        bone = default;

        if (!TryParseComponents(boneJson["Position"], 3, out var pos))
            return false;
        if (!TryParseComponents(boneJson["Scale"], 3, out var sca))
            return false;
        if (!TryParseComponents(boneJson["Rotation"], 4, out var rot))
            return false;

        bone = new BoneData
        {
            Exists = true,
            PositionX = pos[0],
            PositionY = pos[1],
            PositionZ = pos[2],
            ScaleX = sca[0],
            ScaleY = sca[1],
            ScaleZ = sca[2],
            RotationX = rot[0],
            RotationY = rot[1],
            RotationZ = rot[2],
            RotationW = rot[3],
        };
        return true;
    }

    private static bool TryParseComponents(JsonNode? node, int count, out float[] components)
    {
        components = [];

        if (node is null)
            return false;

        var parts = node.ToString().Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < count)
            return false;

        var parsed = new float[count];
        for (var i = 0; i < count; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return false;

            parsed[i] = float.Round(value, 5);
        }

        components = parsed;
        return true;
    }
}
