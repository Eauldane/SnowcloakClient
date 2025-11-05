using System.Security.Cryptography;
using System.Text;
using ZstdSharp;
using Blake3;

namespace SnowcloakSync.Files;

public enum CompressionType : byte
{
    Uncompressed = 0, ZSTD = 1
}

public enum FileExtension : byte
{
    // Standard game file extensions
    MDL = 0,  TEX = 1, MTRL = 2, TMB = 3, PAP = 4, AVFX = 5, ATEX = 6, SKLB = 7, EID = 8, PHYB = 9, PBD = 10, SCD = 11,
    SKP = 12, SHPK = 13,
    // Plugin specific ones (may or may not actually be sent through Snowcloak, but may as well include them just in case)
    MCDF = 14, PCP = 15
    // Anything added later
}

public static class SCFFile
{

    public static ReadOnlySpan<byte> Magic => "SNOW"u8; // Forces UTF-8 for easier cross-compat with Go libs
    public const byte SCFVersion = 1;

    // Feels icky not having this as a struct but that might be C brain talking
    // This is apparently "the C# way" because every wheel needs reinventing
    public sealed record SCFFileHeader(
        string Hash, // 64 characters, SHA-256 Hex string
        CompressionType CompressionType,
        FileExtension FileExtension,
        uint UncompressedSize,
        uint CompressedSize
    );

    public static SCFFileHeader CreateHeader(string hash, CompressionType compressionType, FileExtension fileExtension,
        uint uncompressedSize, uint compressedSize)
    {
        if (hash is null)
        {
            throw new ArgumentNullException(nameof(hash));

        }

        if (hash.Length != 64 || hash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("SCF: Invalid hash - needs to be a SHA-256 hash represented as hexadecimal.",
                nameof(hash));
        }

        // Old mare standardised on uppercase hashes, we may as well too
        return new SCFFileHeader(hash.ToUpperInvariant(), compressionType, fileExtension, uncompressedSize,
            compressedSize);
    }

    public static async Task<SCFFileHeader> CreateSCFFile(Stream rawInput, Stream scfOutput, FileExtension ext,
        IProgress<(string phase, long bytes)>? progress = null, CancellationToken ct = default)
    {
        if (!rawInput.CanRead)
        {
            throw new ArgumentException("SCF: Input needs to be readable for compression", nameof(rawInput));
        }

        if (!scfOutput.CanWrite)
        {
            throw new ArgumentException("SCF: Output needs to be writable", nameof(scfOutput));
        }

        if (!scfOutput.CanSeek)
        {
            throw new ArgumentException("SCF: Output needs to be seekable for patching header", nameof(scfOutput));
        }

        scfOutput.Position = 0;
        try
        {
            scfOutput.SetLength(0); // Just in case scfOutput isn't "clean" 
        }
        catch
        {
            // Just carry on if SetLength isn't supported for some reason - should probably make this a returnable error tho
        }

        // Placeholder header. TODO: Add validation to read/write methods that the placeholders aren't still placeholders
        SCFFileHeader placeholder = CreateHeader(new string('0', 64), CompressionType.Uncompressed, ext, 0, 0);
        WriteHeader(scfOutput, placeholder);

        long dataStart = scfOutput.Position; // Should be 79
        long uncompressed = 0;
        using var hasher = Blake3.Hasher.New();
        using (var zstd = new CompressionStream(scfOutput, level: 3, leaveOpen: true))
        {
            var buffer =
                new byte[81920]; // Weird number, don't like it, but anything over 80KB is considered large by .NET
            int read;
            while ((read = await rawInput.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                hasher.Update(buffer);
                await zstd.WriteAsync(buffer.AsMemory(0, read), ct);
                uncompressed += read;
                progress?.Report(("Compressing", uncompressed));

            }

            await zstd.FlushAsync(ct);
        }

        long dataEnd = scfOutput.Position;
        uint compressedSize = checked((uint)(dataEnd - dataStart));
        uint uncompressedSize = checked((uint)uncompressed);
        string hash = Convert.ToHexString(hasher.Finalize().AsSpan()).ToUpperInvariant();
        hasher.Dispose();
        // Patch header
        scfOutput.Position = 0;
        SCFFileHeader finalHeader =
            CreateHeader(hash, CompressionType.Uncompressed, ext, uncompressedSize, compressedSize);
        WriteHeader(scfOutput, finalHeader); // Overwrite placeholder. Compressed data should be after the 79th byte for decoding
        scfOutput.Position = dataEnd;
        return finalHeader; // For validation
    }

    public static async Task<string> ExtractSCFFile(Stream scfInput, string snowcloakCacheDir, CancellationToken ct = default)
    {
        if (scfInput is null) { throw new ArgumentNullException(nameof(scfInput)); }
        if (snowcloakCacheDir is null) { throw new ArgumentNullException(nameof(snowcloakCacheDir)); }
        
        SCFFileHeader header = ReadHeader(scfInput);
        FileExtension fileExtension = header.FileExtension;
        string hash = header.Hash.Trim('\0').Trim();
        
        string finalPath = Path.Combine(snowcloakCacheDir, $"{hash}.{fileExtension}");
        string tempPath = finalPath + ".tmp";
        
        await using FileStream outFile = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.SequentialScan);
        Hasher hasher = Blake3.Hasher.New();
        var buf = new byte[81920];

        switch (header.CompressionType)
        {
            case CompressionType.Uncompressed:
                {
                    int read;
                    while ((read = await scfInput.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                    {
                        hasher.Update(buf);
                        await outFile.WriteAsync(buf.AsMemory(0, read), ct);
                    }

                    break;
                }
            case CompressionType.ZSTD:
                {
                    using var zstd = new DecompressionStream(scfInput, leaveOpen: true);
                    int read;
                    while ((read = await zstd.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                    {
                        hasher.Update(buf);
                        await outFile.WriteAsync(buf.AsMemory(0, read), ct);
                    }

                    break;
                }
            default:
                throw new NotSupportedException($"SCF: Compression type {header.CompressionType} not supported yet.");

        }
        await outFile.FlushAsync(ct);
        var extractedHash = Convert.ToHexString(hasher!.Finalize().AsSpan()).ToUpperInvariant();
        hasher.Dispose();
        if (!string.Equals(extractedHash, hash, StringComparison.Ordinal))
        {
           outFile.Dispose();
           File.Delete(tempPath);
           throw new InvalidDataException("SCF: Invalid hash");
        }
        
        outFile.Dispose();
        File.Move(tempPath, finalPath, overwrite: true);
        return finalPath;
    }

    public static void WriteHeader(Stream output, SCFFileHeader header)
    {
        using var bw = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        bw.Write(Magic);
        bw.Write(SCFVersion);
        var buf = new byte[64];
        var src = Encoding.ASCII.GetBytes(header.Hash);
        Array.Copy(src, 0, buf, 0, Math.Min(src.Length, 64));
        bw.Write(buf);
        bw.Write((byte)header.CompressionType);
        bw.Write((byte)header.FileExtension);
        bw.Write(header.UncompressedSize);
        bw.Write(header.CompressedSize);
    }
    
    public static SCFFileHeader ReadHeader(Stream input)
    {
        using var br = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        Span<byte> m = stackalloc byte[4];
        if (br.Read(m) != 4 || !m.SequenceEqual(Magic))
            throw new InvalidDataException("SCF: bad magic (expected 'SNOW').");

        var version = br.ReadByte();
        if (version != SCFVersion)
            throw new InvalidDataException($"SCF: unsupported version {version}.");

        string hash = Encoding.ASCII.GetString(br.ReadBytes(64));
        CompressionType comp = (CompressionType)br.ReadByte();
        FileExtension ext = (FileExtension)br.ReadByte();
        uint uncompressedSize = br.ReadUInt32();
        uint compressedSize= br.ReadUInt32();

        return CreateHeader(hash, comp, ext, uncompressedSize, compressedSize);
    }
    
    private static string GetExtensionString(FileExtension ext) => ext.ToString().ToLowerInvariant();
    
    public static FileExtension GetExtensionEnum(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            throw new ArgumentNullException(nameof(ext));

        // normalize
        ext = ext.Trim().TrimStart('.').ToLowerInvariant();

        return ext switch
        {
            "mdl"  => FileExtension.MDL,
            "tex"  => FileExtension.TEX,
            "mtrl" => FileExtension.MTRL,
            "tmb"  => FileExtension.TMB,
            "pap"  => FileExtension.PAP,
            "avfx" => FileExtension.AVFX,
            "atex" => FileExtension.ATEX,
            "sklb" => FileExtension.SKLB,
            "eid"  => FileExtension.EID,
            "phyb" => FileExtension.PHYB,
            "pbd"  => FileExtension.PBD,
            "scd"  => FileExtension.SCD,
            "skp"  => FileExtension.SKP,
            "shpk" => FileExtension.SHPK,
            "mcdf" => FileExtension.MCDF,
            "pcp"  => FileExtension.PCP,
            _ => throw new NotSupportedException($"Unsupported extension: {ext}")
        };
    }

    
}

