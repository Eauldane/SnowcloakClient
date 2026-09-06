using Snowcloak.API.Data.Enum;

namespace Snowcloak.WebAPI.Files.Models;

public sealed record DownloadPartialDescriptor
{
    public int SchemaVersion { get; init; } = 1;
    public required string OperationGeneration { get; init; }
    public required string RawHash { get; init; }
    public required string RepresentationId { get; init; }
    public required string EntityTag { get; init; }
    public required FileContainerVersion ContainerVersion { get; init; }
    public required FileRepresentationCodec Codec { get; init; }
    public required string Profile { get; init; }
    public required long EncodedSize { get; init; }
    public int ChunkSize { get; set; }
    public int ChunkCount { get; set; }
    public long ValidatedEncodedOffset { get; set; }
    public int ValidatedChunkCount { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
