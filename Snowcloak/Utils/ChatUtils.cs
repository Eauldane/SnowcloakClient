using Dalamud.Game.Text.SeStringHandling.Payloads;
using System.Security.Cryptography;
using System.Text;

namespace Snowcloak.Utils;

public static class ChatUtils
{
    // Based on https://git.anna.lgbt/anna/ExtraChat/src/branch/main/client/ExtraChat/Util/PayloadUtil.cs
    // This must store a Guid (16 bytes), as Chat 2 converts the data back to one

    public static RawPayload CreateExtraChatTagPayload(Guid guid)
    {
        var header = (byte[])[
            0x02,   // Payload.START_BYTE
            0x27,   // SeStringChunkType.Interactable
            2 + 16, // remaining length: ExtraChat sends 19 here but I think its an error
            0x20    // Custom ExtraChat InfoType
        ];

        var footer = (byte)0x03; // Payload.END_BYTE

        return new RawPayload([..header, ..guid.ToByteArray(), footer]);
    }

    // We have a unique identifier in the form of a GID, which can be consistently mapped to the same GUID
    public static RawPayload CreateExtraChatTagPayload(string gid)
    {
        var gidBytes = UTF8Encoding.UTF8.GetBytes(gid);
        var hashedBytes = MD5.HashData(gidBytes);
        var guid = new Guid(hashedBytes);
        return CreateExtraChatTagPayload(guid);
    }
}
