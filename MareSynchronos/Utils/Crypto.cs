using System.Security.Cryptography;
using System.Text;
using System.Buffers;
using Blake3;

namespace MareSynchronos.Utils;

public static class Crypto
{
#pragma warning disable SYSLIB0021 // Type or member is obsolete

    private static readonly SHA256CryptoServiceProvider _sha256CryptoProvider = new();

    public static string GetFileHash(this string filePath)
    {
        using var fileStream = File.OpenRead(filePath);

        var hasher = Hasher.New();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            int bytesRead;
            while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.Update(buffer.AsSpan(0, bytesRead));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hasher.Finalize().ToString().ToUpperInvariant();
    }

    public static string GetHash256(this string stringToHash)
    {
        return GetOrComputeHashSHA256(stringToHash);
    }

    private static string GetOrComputeHashSHA256(string stringToCompute)
    {
        return BitConverter.ToString(_sha256CryptoProvider.ComputeHash(Encoding.UTF8.GetBytes(stringToCompute))).Replace("-", "", StringComparison.Ordinal);
    }
#pragma warning restore SYSLIB0021 // Type or member is obsolete
}