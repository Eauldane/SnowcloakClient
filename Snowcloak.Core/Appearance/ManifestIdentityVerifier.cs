using System.Security.Cryptography;
using Snowcloak.API.Dto.Manifest;

namespace Snowcloak.Core.Appearance;

public static class ManifestIdentityVerifier
{
    public const int HardMaxManifestBytes = 4 * 1024 * 1024;

    public static AppearanceManifest VerifyAndDeserialize(byte[] bytes, string expectedHash)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > HardMaxManifestBytes)
        {
            throw new InvalidDataException("Manifest size is outside the accepted range.");
        }

        var manifest = ManifestCanonical.Deserialize(bytes);
        var computedHash = ManifestCanonical.ComputeHash(manifest);
        if (!HashesEqual(expectedHash, computedHash))
        {
            throw new InvalidDataException("Manifest identity does not match its canonical content.");
        }

        return manifest;
    }

    private static bool HashesEqual(string expectedHash, string computedHash)
    {
        try
        {
            var expected = Convert.FromHexString(expectedHash ?? string.Empty);
            var computed = Convert.FromHexString(computedHash);
            return expected.Length == 32
                && computed.Length == 32
                && CryptographicOperations.FixedTimeEquals(expected, computed);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
