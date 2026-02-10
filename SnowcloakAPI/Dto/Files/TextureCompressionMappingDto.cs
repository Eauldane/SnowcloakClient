using MessagePack;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record TextureCompressionMappingDto(string OriginalHash, string CompressedHash);
