using Snowcloak.PlayerData.Handlers;

namespace Snowcloak.FileCache;

public sealed record TransientRecord(GameObjectHandler Owner, string GamePath, string FilePath, bool AlreadyTransient)
{
    public bool AddTransient { get; set; }
}
