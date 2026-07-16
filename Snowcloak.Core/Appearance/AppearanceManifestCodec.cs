using System.Text;
using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Manifest;

namespace Snowcloak.Core.Appearance;

public static class AppearanceManifestCodec
{
    private const int MaxExtensionSectionBytes = 136 * 1024;
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard;
    private static readonly MessagePackSerializerOptions UntrustedOptions = Options.WithSecurity(MessagePackSecurity.UntrustedData);

    public static AppearanceManifest ToManifest(CharacterData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var sections = new List<ManifestSection>();

        var files = BuildFilesSection(data.FileReplacements);
        if (files.Groups.Length > 0)
        {
            sections.Add(BuildSection(ManifestSectionId.Files, Serialize(files), allowCompression: true));
        }

        if (!string.IsNullOrEmpty(data.ManipulationData))
        {
            sections.Add(BuildSection(ManifestSectionId.PenumbraManip, Convert.FromBase64String(data.ManipulationData), allowCompression: false));
        }

        var glamourer = BuildBlobSection(data.GlamourerData, static value => Convert.FromBase64String(value));
        if (glamourer.Length > 0)
        {
            sections.Add(BuildSection(ManifestSectionId.Glamourer, Serialize(new GlamourerSection
            {
                Entries = glamourer.Select(pair => new GlamourerEntry { ObjectKind = pair.Kind, State = pair.Payload }).ToArray(),
            }), allowCompression: false));
        }

        var customize = BuildBlobSection(data.CustomizePlusData, static value => Convert.FromBase64String(value));
        if (customize.Length > 0)
        {
            sections.Add(BuildSection(ManifestSectionId.CustomizePlus, Serialize(new CustomizePlusSection
            {
                Entries = customize.Select(pair => new CustomizePlusEntry { ObjectKind = pair.Kind, Profile = pair.Payload }).ToArray(),
            }), allowCompression: true));
        }

        if (!string.IsNullOrEmpty(data.MoodlesData))
        {
            sections.Add(BuildSection(ManifestSectionId.Moodles, Encoding.UTF8.GetBytes(data.MoodlesData), allowCompression: true));
        }

        if (!string.IsNullOrEmpty(data.HonorificData))
        {
            sections.Add(BuildSection(ManifestSectionId.Honorific, Convert.FromBase64String(data.HonorificData), allowCompression: true));
        }

        if (!string.IsNullOrEmpty(data.HeelsData))
        {
            sections.Add(BuildSection(ManifestSectionId.Heels, Encoding.UTF8.GetBytes(data.HeelsData), allowCompression: true));
        }

        if (!string.IsNullOrEmpty(data.PetNamesData))
        {
            sections.Add(BuildSection(ManifestSectionId.PetNames, Encoding.UTF8.GetBytes(data.PetNamesData), allowCompression: true));
        }

        if (data.ExtensionData.Count > 0)
        {
            var extensionData = new ExtensionDataSection
            {
                Entries = data.ExtensionData
                    .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .Select(static entry => new ExtensionDataEntry { Key = entry.Key, Data = entry.Value })
                    .ToArray(),
            };
            sections.Add(BuildSection(ManifestSectionId.ExtensionData, Serialize(extensionData), allowCompression: true));
        }

        sections.Sort(static (a, b) => a.SectionId.CompareTo(b.SectionId));
        return new AppearanceManifest { FormatVersion = 1, Sections = sections.ToArray() };
    }

    public static CharacterData ToCharacterData(AppearanceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var data = new CharacterData();
        foreach (var section in manifest.Sections ?? [])
        {
            if (section.FormatVersion != 1)
            {
                continue;
            }

            var payload = RawPayload(section);
            switch (section.SectionId)
            {
                case ManifestSectionId.Files:
                    ApplyFiles(data, Deserialize<FilesSection>(payload));
                    break;
                case ManifestSectionId.PenumbraManip:
                    data.ManipulationData = Convert.ToBase64String(payload);
                    break;
                case ManifestSectionId.Glamourer:
                    foreach (var entry in Deserialize<GlamourerSection>(payload).Entries)
                    {
                        data.GlamourerData[(ObjectKind)entry.ObjectKind] = Convert.ToBase64String(entry.State);
                    }

                    break;
                case ManifestSectionId.CustomizePlus:
                    foreach (var entry in Deserialize<CustomizePlusSection>(payload).Entries)
                    {
                        data.CustomizePlusData[(ObjectKind)entry.ObjectKind] = Convert.ToBase64String(entry.Profile);
                    }

                    break;
                case ManifestSectionId.Moodles:
                    data.MoodlesData = Encoding.UTF8.GetString(payload);
                    break;
                case ManifestSectionId.Honorific:
                    data.HonorificData = Convert.ToBase64String(payload);
                    break;
                case ManifestSectionId.Heels:
                    data.HeelsData = Encoding.UTF8.GetString(payload);
                    break;
                case ManifestSectionId.PetNames:
                    data.PetNamesData = Encoding.UTF8.GetString(payload);
                    break;
                case ManifestSectionId.ExtensionData:
                    data.ExtensionData = ReadExtensionEntries(payload);
                    break;
                default:
                    break;
            }
        }

        return data;
    }

    public static string ComputeHash(CharacterData data)
    {
        return ManifestCanonical.ComputeHash(ToManifest(data));
    }

    private static FilesSection BuildFilesSection(Dictionary<ObjectKind, List<FileReplacementData>> replacements)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, list) in replacements)
        {
            foreach (var replacement in list)
            {
                foreach (var gamePath in replacement.GamePaths)
                {
                    if (!string.IsNullOrEmpty(gamePath))
                    {
                        paths.Add(gamePath);
                    }
                }

                if (!string.IsNullOrEmpty(replacement.FileSwapPath))
                {
                    paths.Add(replacement.FileSwapPath);
                }
            }
        }

        var table = paths.ToArray();
        var index = new Dictionary<string, int>(table.Length, StringComparer.Ordinal);
        for (var i = 0; i < table.Length; i++)
        {
            index[table[i]] = i;
        }

        var groups = new List<FilesGroup>();
        foreach (var kind in replacements.Keys.OrderBy(static k => (byte)k))
        {
            var list = replacements[kind];
            if (list is not { Count: > 0 })
            {
                continue;
            }

            var entries = new List<FileEntry>(list.Count);
            foreach (var replacement in list)
            {
                var refs = replacement.GamePaths
                    .Where(static p => !string.IsNullOrEmpty(p))
                    .Select(p => index[p])
                    .Distinct()
                    .OrderBy(static i => i)
                    .ToArray();
                var swapRef = string.IsNullOrEmpty(replacement.FileSwapPath) ? -1 : index[replacement.FileSwapPath];
                entries.Add(new FileEntry { GamePathRefs = refs, Hash = replacement.Hash ?? string.Empty, FileSwapRef = swapRef });
            }

            entries.Sort((a, b) =>
            {
                var pathA = a.GamePathRefs.Length > 0 ? table[a.GamePathRefs[0]] : string.Empty;
                var pathB = b.GamePathRefs.Length > 0 ? table[b.GamePathRefs[0]] : string.Empty;
                var compare = string.CompareOrdinal(pathA, pathB);
                return compare != 0 ? compare : string.CompareOrdinal(a.Hash, b.Hash);
            });

            groups.Add(new FilesGroup { ObjectKind = (byte)kind, Entries = entries.ToArray() });
        }

        return new FilesSection { StringTable = table, Groups = groups.ToArray() };
    }

    private static (byte Kind, byte[] Payload)[] BuildBlobSection(Dictionary<ObjectKind, string> source, Func<string, byte[]> decode)
    {
        return source.Keys
            .Where(kind => !string.IsNullOrEmpty(source[kind]))
            .OrderBy(static k => (byte)k)
            .Select(kind => ((byte)kind, decode(source[kind])))
            .ToArray();
    }

    private static void ApplyFiles(CharacterData data, FilesSection files)
    {
        var table = files.StringTable ?? [];
        foreach (var group in files.Groups ?? [])
        {
            var list = new List<FileReplacementData>(group.Entries.Length);
            foreach (var entry in group.Entries)
            {
                list.Add(new FileReplacementData
                {
                    GamePaths = entry.GamePathRefs.Select(i => PathAt(table, i)).ToArray(),
                    Hash = entry.Hash ?? string.Empty,
                    FileSwapPath = entry.FileSwapRef >= 0 ? PathAt(table, entry.FileSwapRef) : string.Empty,
                });
            }

            data.FileReplacements[(ObjectKind)group.ObjectKind] = list;
        }
    }

    private static string PathAt(string[] table, int index)
    {
        return (uint)index < (uint)table.Length ? table[index] : string.Empty;
    }

    private static ManifestSection BuildSection(ManifestSectionId id, byte[] rawPayload, bool allowCompression)
    {
        if (allowCompression)
        {
            var deflated = ManifestCompression.Deflate(rawPayload);
            if (deflated.Length < rawPayload.Length)
            {
                return new ManifestSection { SectionId = id, FormatVersion = 1, Encoding = SectionEncoding.Deflate, Payload = deflated };
            }
        }

        return new ManifestSection { SectionId = id, FormatVersion = 1, Encoding = SectionEncoding.Raw, Payload = rawPayload };
    }

    private static byte[] RawPayload(ManifestSection section)
    {
        if (section.SectionId == ManifestSectionId.ExtensionData)
        {
            if (section.Encoding == SectionEncoding.Deflate)
            {
                return ManifestCompression.Inflate(section.Payload, MaxExtensionSectionBytes);
            }

            if (section.Payload.Length > MaxExtensionSectionBytes)
            {
                throw new InvalidDataException("Extension data section exceeds its size limit.");
            }
        }

        return section.Encoding == SectionEncoding.Deflate
            ? ManifestCompression.Inflate(section.Payload)
            : section.Payload;
    }

    private static byte[] Serialize<T>(T value)
    {
        return MessagePackSerializer.Serialize(value, Options);
    }

    private static Dictionary<string, string> ReadExtensionEntries(byte[] payload)
        => MessagePackSerializer.Deserialize<ExtensionDataSection>(payload, UntrustedOptions).Entries
            .Where(static entry => !string.IsNullOrEmpty(entry.Key))
            .GroupBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last().Data, StringComparer.Ordinal);

    private static T Deserialize<T>(byte[] bytes)
    {
        return MessagePackSerializer.Deserialize<T>(bytes, Options);
    }
}
