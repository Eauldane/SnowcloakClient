using Blake3;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Snowcloak.Utils;

public static class Crypto
{
    public static async Task<string> GetFileHashAsync(this string filePath)
    {
        var hasher = Hasher.New();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        FileStream? fileStream = null;

        try
        {
            fileStream = new FileStream(
                filePath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    Share = FileShare.Read,
                    BufferSize = buffer.Length
                });

            int bytesRead;
            while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
            {
                hasher.Update(buffer.AsSpan(0, bytesRead));
            }
        }
        finally
        {
            if (fileStream != null)
                await fileStream.DisposeAsync().ConfigureAwait(false);
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
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stringToCompute)));
    }
}
