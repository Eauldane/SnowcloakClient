using System.IO.Compression;
using System.Text;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Manifest;
using Snowcloak.Core.Appearance;
using Snowcloak.Core.ModNullification;
using Xunit;

namespace Snowcloak.Tests.Appearance;

public sealed class AppearanceManifestCodecTests
{
    private static string GzipBase64(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    private static string GlamourerBase64(byte gender, byte race, byte clan, byte height)
    {
        var json = $"{{\"Customize\":{{\"Gender\":{{\"Value\":{gender}}},\"Race\":{{\"Value\":{race}}},\"Clan\":{{\"Value\":{clan}}},\"Height\":{{\"Value\":{height}}}}}}}";
        return GzipBase64(json);
    }

    private static CharacterData Sample()
    {
        var data = new CharacterData();
        data.FileReplacements[ObjectKind.Player] =
        [
            new FileReplacementData { GamePaths = ["chara/b.mdl", "chara/shared.mtrl"], Hash = "HASHB", FileSwapPath = string.Empty },
            new FileReplacementData { GamePaths = ["chara/shared.mtrl"], Hash = string.Empty, FileSwapPath = "chara/swap.tex" },
        ];
        data.FileReplacements[ObjectKind.Pet] =
        [
            new FileReplacementData { GamePaths = ["chara/pet.mdl"], Hash = "PETHASH", FileSwapPath = string.Empty },
        ];
        data.GlamourerData[ObjectKind.Player] = GlamourerBase64(1, 4, 5, 50);
        data.GlamourerData[ObjectKind.Pet] = GlamourerBase64(0, 2, 3, 25);
        data.CustomizePlusData[ObjectKind.Player] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"scale\":1.0}"));
        data.ManipulationData = GzipBase64("penumbra-meta-manipulation-payload");
        data.MoodlesData = "{\"statuses\":[]}";
        data.HonorificData = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"Title\":\"Hero\"}"));
        data.HeelsData = "0.15";
        data.PetNamesData = "{\"names\":{}}";
        return data;
    }

    private static HashSet<string> NormalizeFiles(List<FileReplacementData> list)
    {
        return list
            .Select(f => $"{string.Join('|', f.GamePaths.OrderBy(p => p, StringComparer.Ordinal))}#{f.Hash}#{f.FileSwapPath}")
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Round_trip_preserves_every_field()
    {
        var original = Sample();
        var restored = AppearanceManifestCodec.ToCharacterData(AppearanceManifestCodec.ToManifest(original));

        Assert.Equal(original.ManipulationData, restored.ManipulationData);
        Assert.Equal(original.MoodlesData, restored.MoodlesData);
        Assert.Equal(original.HonorificData, restored.HonorificData);
        Assert.Equal(original.HeelsData, restored.HeelsData);
        Assert.Equal(original.PetNamesData, restored.PetNamesData);
        Assert.Equal(original.GlamourerData[ObjectKind.Player], restored.GlamourerData[ObjectKind.Player]);
        Assert.Equal(original.GlamourerData[ObjectKind.Pet], restored.GlamourerData[ObjectKind.Pet]);
        Assert.Equal(original.CustomizePlusData[ObjectKind.Player], restored.CustomizePlusData[ObjectKind.Player]);

        foreach (var (kind, list) in original.FileReplacements)
        {
            Assert.True(restored.FileReplacements.ContainsKey(kind));
            Assert.True(NormalizeFiles(list).SetEquals(NormalizeFiles(restored.FileReplacements[kind])));
        }
    }

    [Fact]
    public void Non_player_glamourer_survives_round_trip()
    {
        var original = Sample();
        var restored = AppearanceManifestCodec.ToCharacterData(AppearanceManifestCodec.ToManifest(original));
        Assert.True(restored.GlamourerData.ContainsKey(ObjectKind.Pet));
        Assert.Equal(original.GlamourerData[ObjectKind.Pet], restored.GlamourerData[ObjectKind.Pet]);
    }

    [Fact]
    public void Round_trip_is_hash_stable()
    {
        var original = Sample();
        var restored = AppearanceManifestCodec.ToCharacterData(AppearanceManifestCodec.ToManifest(original));
        Assert.Equal(AppearanceManifestCodec.ComputeHash(original), AppearanceManifestCodec.ComputeHash(restored));
    }

    [Fact]
    public void Hash_matches_api_canonical_helper()
    {
        var manifest = AppearanceManifestCodec.ToManifest(Sample());
        Assert.Equal(ManifestCanonical.ComputeHash(manifest), AppearanceManifestCodec.ComputeHash(Sample()));
    }

    [Fact]
    public void Base64_sections_shed_inflation()
    {
        var data = Sample();
        var manifest = AppearanceManifestCodec.ToManifest(data);

        var penumbra = manifest.Sections.Single(s => s.SectionId == ManifestSectionId.PenumbraManip);
        var originalBase64Length = data.ManipulationData.Length;
        // Raw bytes of a base64 string are ~3/4 its length; assert we shed the inflation.
        Assert.True(penumbra.Payload.Length <= originalBase64Length * 0.77, $"payload {penumbra.Payload.Length} vs base64 {originalBase64Length}");
    }

    [Fact]
    public void Already_compressed_sections_stay_raw()
    {
        var manifest = AppearanceManifestCodec.ToManifest(Sample());
        Assert.Equal(SectionEncoding.Raw, manifest.Sections.Single(s => s.SectionId == ManifestSectionId.PenumbraManip).Encoding);
        Assert.Equal(SectionEncoding.Raw, manifest.Sections.Single(s => s.SectionId == ManifestSectionId.Glamourer).Encoding);
    }

    [Fact]
    public void Text_section_compresses_when_beneficial()
    {
        var data = Sample();
        data.MoodlesData = "{\"statuses\":[" + string.Join(",", Enumerable.Repeat("{\"id\":12345,\"stacks\":3}", 200)) + "]}";
        var manifest = AppearanceManifestCodec.ToManifest(data);
        Assert.Equal(SectionEncoding.Deflate, manifest.Sections.Single(s => s.SectionId == ManifestSectionId.Moodles).Encoding);
    }

    [Fact]
    public void Files_string_table_deduplicates_shared_paths()
    {
        var manifest = AppearanceManifestCodec.ToManifest(Sample());
        var filesSection = manifest.Sections.Single(s => s.SectionId == ManifestSectionId.Files);
        var raw = filesSection.Encoding == SectionEncoding.Deflate ? ManifestCompression.Inflate(filesSection.Payload) : filesSection.Payload;
        var files = MessagePack.MessagePackSerializer.Deserialize<FilesSection>(raw, MessagePack.MessagePackSerializerOptions.Standard);

        // "chara/shared.mtrl" is referenced by two Player entries but must appear once.
        Assert.Equal(files.StringTable.Length, files.StringTable.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(files.StringTable, p => string.Equals(p, "chara/shared.mtrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_section_is_ignored_on_apply()
    {
        var manifest = AppearanceManifestCodec.ToManifest(Sample());
        var extended = new AppearanceManifest
        {
            FormatVersion = 1,
            Sections = [.. manifest.Sections, new ManifestSection
            {
                SectionId = (ManifestSectionId)200,
                FormatVersion = 1,
                Encoding = SectionEncoding.Raw,
                Payload = [9, 9, 9],
            }],
        };

        var restored = AppearanceManifestCodec.ToCharacterData(extended);
        Assert.Equal(Sample().HeelsData, restored.HeelsData);
    }

    [Fact]
    public void Nullification_readers_agree_after_round_trip()
    {
        var original = Sample();
        var restored = AppearanceManifestCodec.ToCharacterData(AppearanceManifestCodec.ToManifest(original));

        Assert.True(GlamourerAppearanceReader.TryRead(original.GlamourerData[ObjectKind.Player], out var before));
        Assert.True(GlamourerAppearanceReader.TryRead(restored.GlamourerData[ObjectKind.Player], out var after));
        Assert.Equal(before, after);
    }
}
