namespace Snowcloak.Game.Scheduling;

public interface IFrameTickHandle : IDisposable
{
    string Name { get; }
}
