namespace Snowcloak.Core.Replay;

public interface IIpcCommandSink
{
    void Record(IpcCommand command);
}

public sealed class NullIpcCommandSink : IIpcCommandSink
{
    public static NullIpcCommandSink Instance { get; } = new();

    public void Record(IpcCommand command)
    {
    }
}

public sealed class RecordingIpcCommandSink : IIpcCommandSink
{
    private readonly List<IpcCommand> _commands = [];

    public IReadOnlyList<IpcCommand> Commands => _commands;

    public void Record(IpcCommand command) => _commands.Add(command);

    public void Clear() => _commands.Clear();

    public Trace ToTrace()
        => new(Trace.CurrentVersion, _commands.Select(c => (TraceEvent)new IpcOutEvent(c)).ToList());
}
