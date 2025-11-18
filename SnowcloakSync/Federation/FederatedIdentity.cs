using MareSynchronos.API.Data;
using System.IO.Hashing;
using System.Text;

namespace SnowcloakSync.Federation;

public class FederatedIdentity
{
    // Just a way to make DB indexing easier. CRC64 isn't technically cryptographically secure, but if we have 6000
    // servers in the network the odds of a collision are just shy of 1 in a trillion. It's fine.
    public static string? GetServerHash(string serverUrl)
    {
        return Crc64.Hash(Encoding.ASCII.GetBytes(serverUrl)).ToString();
    }
}
