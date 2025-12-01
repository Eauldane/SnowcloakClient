using Blake3;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Snowcloak.Utils;

public static class Crypto
{
#pragma warning disable SYSLIB0021 // Type or member is obsolete

    private static readonly SHA256CryptoServiceProvider _sha256CryptoProvider = new();

    public static async Task<string> GetFileHashAsync(this string filePath)
    {
        using var fileStream = File.OpenRead(filePath);

        var hasher = Hasher.New();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            int bytesRead;
            while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
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
    
    public static string GetFileHash(this string filePath)
    {
        return GetFileHashAsync(filePath).GetAwaiter().GetResult();
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