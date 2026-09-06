using Snowcloak.CacheFile;

namespace Snowcloak.Infrastructure.Transfers;

public static class ClientScfIngestion
{
    public static Task<ScfValidationResult> ValidateAndExtractAsync(Stream input, string destinationPath,
        string expectedHash, long? representationLength, CancellationToken cancellationToken)
    {
        return ScfFile.ValidateAndExtractToPathAsync(input, destinationPath,
            new ScfValidationRequest(new ScfExpectedRawHash(expectedHash), ScfReadLimits.Default)
            {
                ExpectedRepresentationLength = representationLength
            }, cancellationToken);
    }
}
