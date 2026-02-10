using MessagePack;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record TextureCompressionMappingBatchDto(IReadOnlyList<TextureCompressionMappingDto> Mappings);
