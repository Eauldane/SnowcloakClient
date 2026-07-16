using Snowcloak.API.Data;
using Snowcloak.Core.Chat;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.Services.Chat;

public sealed class ChatIdentityResolver : IChatIdentityResolver
{
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;

    public ChatIdentityResolver(ApiController apiController, PairManager pairManager)
    {
        _apiController = apiController;
        _pairManager = pairManager;
    }

    public string SelfUid => _apiController.UID;

    public SenderDisplay Resolve(UserData user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var current = _pairManager.GetPairByUID(user.UID)?.UserData ?? user;
        return new SenderDisplay(current.AliasOrUID, ParseColour(current.DisplayColour), ParseColour(current.DisplayGlowColour),
            current.DisplayColour ?? string.Empty, current.DisplayGlowColour ?? string.Empty);
    }

    public SenderDisplay? Resolve(string uid)
    {
        if (string.Equals(uid, SelfUid, StringComparison.Ordinal))
        {
            return new SenderDisplay(_apiController.DisplayName, ParseColour(_apiController.DisplayColour), ParseColour(_apiController.DisplayGlowColour),
                _apiController.DisplayColour, _apiController.DisplayGlowColour);
        }

        var pair = _pairManager.GetPairByUID(uid);
        return pair == null ? null : Resolve(pair.UserData);
    }

    public string ResolveName(string uid) => Resolve(uid)?.Name ?? uid;

    private static Vector4? ParseColour(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalised = value.Trim().TrimStart('#');
        if (normalised.Length != 6 || !uint.TryParse(normalised, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var colour))
        {
            return null;
        }

        return new Vector4(
            ((colour >> 16) & 0xff) / 255f,
            ((colour >> 8) & 0xff) / 255f,
            (colour & 0xff) / 255f,
            1f);
    }
}
