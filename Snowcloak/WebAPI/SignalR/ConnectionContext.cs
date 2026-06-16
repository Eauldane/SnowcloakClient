using Snowcloak.API.Data;
using Snowcloak.API.Dto;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Reflection;

namespace Snowcloak.WebAPI.SignalR;

internal sealed record ConnectionContext(ConnectionDto? Dto)
{
    public static readonly ConnectionContext Empty = new((ConnectionDto?)null);

    public Version CurrentClientVersion => Dto?.CurrentClientVersion ?? new Version(0, 0, 0);
    public string DisplayColour => Dto?.User.DisplayColour ?? string.Empty;
    public string DisplayGlowColour => Dto?.User.DisplayGlowColour ?? string.Empty;
    public string DisplayName => Dto?.User.AliasOrUID ?? string.Empty;
    public bool HasPersistentKey => Dto?.HasPersistentKey ?? false;
    public bool HexAllowed => Dto?.HexAllowed ?? false;
    public bool IsCurrentVersion => (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0)) >= (Dto?.CurrentClientVersion ?? new Version(0, 0, 0, 0));
    public ServerInfo ServerInfo => Dto?.ServerInfo ?? new ServerInfo();
    public string UID => Dto?.User.UID ?? string.Empty;
    public string? VanityId => Dto?.User.Alias;

    public static ConnectionContext From(ConnectionDto dto)
    {
        return new ConnectionContext(dto);
    }

    public ConnectionContext WithUser(UserData user)
    {
        return Dto == null ? this : new ConnectionContext(Dto with { User = user });
    }
}
