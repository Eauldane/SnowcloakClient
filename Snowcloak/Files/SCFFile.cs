using Blake3;
using K4os.Compression.LZ4.Streams;
using System.Text;
using ZstdSharp;
using ZstdSharp.Unsafe;
using System.Buffers;

namespace Snowcloak.Files;

public enum CompressionType : byte
{
    ZSTD = 0,
    LZ4 = 1,
    None = 2
}

public enum FileExtension : byte
{
    // Standard game file extensions
    MDL = 0,  TEX = 1, MTRL = 2, TMB = 3, PAP = 4, AVFX = 5, ATEX = 6, SKLB = 7, EID = 8, PHYB = 9, PBD = 10, SCD = 11,
    SKP = 12, SHPK = 13,
    // Plugin specific ones (may or may not actually be sent through Snowcloak, but may as well include them just in case)
    MCDF = 14, PCP = 15,
    // Anything added later
    DDS = 16
}

public static class SCFFile
{

    public static ReadOnlySpan<byte> Magic => "SNOW"u8; // Forces UTF-8 for easier cross-compat with Go libs
    public const byte SCFVersion = 3;
    public const byte MinimumSupportedVersion = 1;
    public const int HeaderLengthV1 = 79;
    public const int HeaderLengthV2 = 95;
    public const int HeaderLengthV3 = 67;

    // Feels icky not having this as a struct but that might be C brain talking
    // This is apparently "the C# way" because every wheel needs reinventing
    public sealed record SCFFileHeader(
        byte[] HashBytes, // 32-byte Blake3 hash
        CompressionType CompressionType,
        FileExtension FileExtension,
        uint UncompressedSize,
        uint CompressedSize,
        long TriangleCount = -1,
        long VramUsage = -1,
        byte[]? OptionalMetadata = null
    )
    {
        public string HashHex => Convert.ToHexString(HashBytes);

        // Compatibility shim while callers migrate to HashBytes/HashHex.
        public string Hash => HashHex;

        public byte[] OptionalMetadataBytes => OptionalMetadata ?? [];

        public IReadOnlyDictionary<string, byte[]> GetMetadataFields() =>
            SCFMetadataEnvelope.Parse(OptionalMetadataBytes);
    }

    public static SCFFileHeader CreateHeader(string hash, CompressionType compressionType, FileExtension fileExtension,
        uint uncompressedSize, uint compressedSize, long triangleCount = -1, long vramUsage = -1,
        byte[]? optionalMetadata = null)
    {
        if (hash is null)
        {
            throw new ArgumentNullException(nameof(hash));
        }

        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("SCF: Invalid hash - needs to be a Blake3 hash represented as hexadecimal (64 characters).",
                nameof(hash));
        }

        return CreateHeader(Convert.FromHexString(hash), compressionType, fileExtension, uncompressedSize,
            compressedSize, triangleCount, vramUsage, optionalMetadata);
    }

    public static SCFFileHeader CreateHeader(ReadOnlySpan<byte> hashBytes, CompressionType compressionType,
        FileExtension fileExtension, uint uncompressedSize, uint compressedSize, long triangleCount = -1,
        long vramUsage = -1, byte[]? optionalMetadata = null)
    {
        if (hashBytes.Length != 32)
        {
            throw new ArgumentException("SCF: Invalid hash - needs to be a Blake3 hash (32 bytes).", nameof(hashBytes));
        }

        return new SCFFileHeader(hashBytes.ToArray(), compressionType, fileExtension, uncompressedSize,
            compressedSize, triangleCount, vramUsage, optionalMetadata?.ToArray());
    }

    public static async Task<SCFFileHeader> CreateSCFFile(Stream rawInput, Stream scfOutput, FileExtension ext,
        IProgress<(string phase, long bytes)>? progress = null, CancellationToken ct = default, int compressionLevel = 3,
        bool multithreaded = false, long triangleCount = -1, long vramUsage = -1,
        CompressionType compressionType = CompressionType.ZSTD, byte[]? optionalMetadata = null,
        IReadOnlyDictionary<string, byte[]>? optionalMetadataFields = null)
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

        if (optionalMetadata is not null && optionalMetadataFields is not null)
        {
            throw new ArgumentException("SCF: Provide either optionalMetadata or optionalMetadataFields, not both.");
        }

        optionalMetadata ??= SCFMetadataEnvelope.Create(optionalMetadataFields);

        scfOutput.Position = 0;
        try
        {
            scfOutput.SetLength(0); // Just in case scfOutput isn't "clean" 
        }
        catch (Exception ex)
        {
            throw new IOException("SCF: Failed to truncate output stream before writing header", ex);
        }

        // Placeholder header. TODO: Add validation to read/write methods that the placeholders aren't still placeholders
        SCFFileHeader placeholder = CreateHeader(new byte[32], compressionType, ext, 0, 0, triangleCount,
            vramUsage, optionalMetadata);
        WriteHeader(scfOutput, placeholder);

        long dataStart = scfOutput.Position; // Should be header length
        long uncompressed = 0;
        using var hasher = Hasher.New();
        // Level 3 is the sweetspot in benchmarks. Anything below 9 should be fine really
        // but higher levels = more time to compress. Level 2 gives a 2.10x ratio on a
        // mod pack I tested, so level 3 should hit that while
        // compressing at >200MB/s on any CPU that FF14 supports
        //
        // As long as this is never set above 10 we should be fine. Server can recompress to level 19
        // or 22 if it really wants to eke out that last 5% compression.
        var level = Math.Clamp(compressionLevel, 3, 9);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            switch (compressionType)
            {
                case CompressionType.ZSTD:
                    using (var zstd = new CompressionStream(scfOutput, level: level, leaveOpen: true))
                    {
                        if (multithreaded && Environment.ProcessorCount > 1)
                        {
                            zstd.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, Environment.ProcessorCount);
                        }

                        while ((read = await rawInput.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                        {
                            hasher.Update(buffer.AsSpan(0, read));
                            await zstd.WriteAsync(buffer.AsMemory(0, read), ct);
                            uncompressed += read;
                            progress?.Report(("Compressing", uncompressed));
                        }

                        await zstd.FlushAsync(ct);
                    }
                    break;
                case CompressionType.LZ4:
                    using (var lz4 = LZ4Stream.Encode(scfOutput, leaveOpen: true))
                    {
                        while ((read = await rawInput.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                        {
                            hasher.Update(buffer.AsSpan(0, read));
                            await lz4.WriteAsync(buffer.AsMemory(0, read), ct);
                            uncompressed += read;
                            progress?.Report(("Compressing", uncompressed));
                        }

                        await lz4.FlushAsync(ct);
                    }
                    break;
                case CompressionType.None:
                    while ((read = await rawInput.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        hasher.Update(buffer.AsSpan(0, read));
                        await scfOutput.WriteAsync(buffer.AsMemory(0, read), ct);
                        uncompressed += read;
                        progress?.Report(("Copying", uncompressed));
                    }
                    break;
                default:
                    throw new NotSupportedException($"SCF: Compression type {compressionType} not supported.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        long dataEnd = scfOutput.Position;
        uint compressedSize = checked((uint)(dataEnd - dataStart));
        uint uncompressedSize = checked((uint)uncompressed);
        var hashBytes = hasher.Finalize().AsSpan();
        // Patch header
        scfOutput.Position = 0;
        SCFFileHeader finalHeader =
            CreateHeader(hashBytes, compressionType, ext, uncompressedSize, compressedSize, triangleCount,
                vramUsage, optionalMetadata);
        WriteHeader(scfOutput, finalHeader); // Overwrite placeholder. Compressed data should be after the header for decoding
        scfOutput.Position = dataEnd;
        return finalHeader; // For validation
    }

    public static async Task<string> ExtractSCFFile(Stream scfInput, string snowcloakCacheDir, CancellationToken ct = default)
    {
        if (scfInput is null) { throw new ArgumentNullException(nameof(scfInput)); }
        if (snowcloakCacheDir is null) { throw new ArgumentNullException(nameof(snowcloakCacheDir)); }
        
        SCFFileHeader header = ReadHeader(scfInput);
        FileExtension fileExtension = header.FileExtension;
        string hash = header.HashHex;
        
        string finalPath = Path.Combine(snowcloakCacheDir, $"{hash}.{fileExtension}");
        string tempPath = finalPath + ".tmp";

        try
        {
            await using FileStream outFile = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string extractedHash = await ExtractSCFToStream(scfInput, outFile, header, ct).ConfigureAwait(false);
            await outFile.FlushAsync(ct);
            if (!string.Equals(extractedHash, hash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"SCF: Invalid hash. Expected {hash}, but extracted {extractedHash}.");
            }
        }

        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
        
        File.Move(tempPath, finalPath, overwrite: true);
        return finalPath;
    }

    public static async Task<string> ExtractSCFToStream(Stream scfInput, Stream rawOutput,
        CancellationToken ct = default)
    {
        if (scfInput is null) throw new ArgumentNullException(nameof(scfInput));
        if (rawOutput is null) throw new ArgumentNullException(nameof(rawOutput));
        if (!rawOutput.CanWrite)
            throw new ArgumentException("SCF: Output needs to be writable", nameof(rawOutput));

        var header = ReadHeader(scfInput);
        return await ExtractSCFToStream(scfInput, rawOutput, header, ct).ConfigureAwait(false);
    }

    private static async Task<string> ExtractSCFToStream(Stream scfInput, Stream rawOutput, SCFFileHeader header,
        CancellationToken ct = default)
    {
        using var hasher = Blake3.Hasher.New();
        var buf = ArrayPool<byte>.Shared.Rent(81920);
        long uncompressedBytes = 0;

        try
        {
            switch (header.CompressionType)
            {
                case CompressionType.ZSTD:
                {
                    using var boundedInput = new ReadLimitStream(scfInput, header.CompressedSize);
                    using var zstd = new DecompressionStream(boundedInput, leaveOpen: true);
                    int read;
                    while ((read = await zstd.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                    {
                        hasher.Update(buf.AsSpan(0, read));
                        await rawOutput.WriteAsync(buf.AsMemory(0, read), ct);
                        uncompressedBytes += read;
                    }

                    if (boundedInput.RemainingBytes != 0)
                    {
                        throw new EndOfStreamException("SCF: Unexpected end of stream while reading compressed ZSTD data.");
                    }

                    break;
                }
                case CompressionType.LZ4:
                {
                    using var boundedInput = new ReadLimitStream(scfInput, header.CompressedSize);
                    using var lz4 = LZ4Stream.Decode(boundedInput, leaveOpen: true);
                    int read;
                    while ((read = await lz4.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                    {
                        hasher.Update(buf.AsSpan(0, read));
                        await rawOutput.WriteAsync(buf.AsMemory(0, read), ct);
                        uncompressedBytes += read;
                    }

                    if (boundedInput.RemainingBytes != 0)
                    {
                        throw new EndOfStreamException("SCF: Unexpected end of stream while reading compressed LZ4 data.");
                    }

                    break;
                }
                case CompressionType.None:
                {
                    long remaining = header.CompressedSize;
                    while (remaining > 0)
                    {
                        int read = await scfInput.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)),
                            ct);
                        if (read == 0)
                        {
                            throw new EndOfStreamException("SCF: Unexpected end of stream while copying raw data.");
                        }

                        hasher.Update(buf.AsSpan(0, read));
                        await rawOutput.WriteAsync(buf.AsMemory(0, read), ct);
                        remaining -= read;
                        uncompressedBytes += read;
                    }

                    break;
                }
                default:
                    throw new NotSupportedException($"SCF: Compression type {header.CompressionType} not supported yet.");
            }

            if (uncompressedBytes != header.UncompressedSize)
            {
                throw new InvalidDataException($"SCF: Invalid uncompressed size. Expected {header.UncompressedSize}, extracted {uncompressedBytes}.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return Convert.ToHexString(hasher.Finalize().AsSpan()).ToUpperInvariant();
    }

    public static void WriteHeader(Stream output, SCFFileHeader header)
    {
        using var bw = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        bw.Write(Magic);
        bw.Write(SCFVersion);
        bw.Write(header.HashBytes);
        bw.Write((byte)header.CompressionType);
        bw.Write((byte)header.FileExtension);
        bw.Write(header.UncompressedSize);
        bw.Write(header.CompressedSize);
        bw.Write(header.TriangleCount);
        bw.Write(header.VramUsage);
        bw.Write((uint)header.OptionalMetadataBytes.Length);
        bw.Write(header.OptionalMetadataBytes);
    }
    
    public static SCFFileHeader ReadHeader(Stream input)
    {
        using var br = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        Span<byte> m = stackalloc byte[4];
        if (br.Read(m) != 4 || !m.SequenceEqual(Magic))
            throw new InvalidDataException("SCF: bad magic (expected 'SNOW').");

        var version = br.ReadByte();
        if (version is < MinimumSupportedVersion or > SCFVersion)
            throw new InvalidDataException($"SCF: unsupported version {version}.");

        byte[] hashBytes;
        if (version >= 3)
        {
            hashBytes = br.ReadBytes(32);
            if (hashBytes.Length != 32)
            {
                throw new EndOfStreamException("SCF: Unexpected end of stream while reading hash bytes.");
            }
        }
        else
        {
            string hash = Encoding.ASCII.GetString(br.ReadBytes(64));
            hashBytes = Convert.FromHexString(hash);
        }

        CompressionType comp = (CompressionType)br.ReadByte();
        FileExtension ext = (FileExtension)br.ReadByte();
        uint uncompressedSize = br.ReadUInt32();
        uint compressedSize= br.ReadUInt32();
        long triangleCount = -1;
        long vramUsage = -1;
        if (version >= 2)
        {
            triangleCount = br.ReadInt64();
            vramUsage = br.ReadInt64();
        }

        uint optionalMetadataLength = 0;
        if (version >= 3)
        {
            optionalMetadataLength = br.ReadUInt32();
        }

        var optionalMetadata = br.ReadBytes(checked((int)optionalMetadataLength));
        if (optionalMetadata.Length != optionalMetadataLength)
        {
            throw new EndOfStreamException("SCF: Unexpected end of stream while reading optional metadata.");
        }

        return CreateHeader(hashBytes, comp, ext, uncompressedSize, compressedSize, triangleCount, vramUsage,
            optionalMetadata);
    }
    
    public static int GetHeaderLength(byte version = SCFVersion, uint optionalMetadataLength = 0)
    {
        return version switch
        {
            1 => HeaderLengthV1,
            2 => HeaderLengthV2,
            3 => checked(HeaderLengthV3 + (int)optionalMetadataLength),
            _ => throw new InvalidDataException($"SCF: unsupported version {version}.")
        };
    }
}
