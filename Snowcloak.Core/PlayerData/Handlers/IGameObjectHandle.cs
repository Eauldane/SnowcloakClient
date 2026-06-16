namespace Snowcloak.PlayerData.Handlers;

public interface IGameObjectHandle
{
    nint Address { get; }

    ushort? ObjectIndex { get; }
}
