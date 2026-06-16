using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Core.Replay;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcTraceRecorder
{
    private readonly ILogger<IpcTraceRecorder> _logger;
    private readonly SnowcloakConfigService _configService;
    private readonly Lock _gate = new();
    private SymbolTable _symbols = new();
    private RecordingIpcCommandSink _sink = new();

    public IpcTraceRecorder(ILogger<IpcTraceRecorder> logger, SnowcloakConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    public bool IsCapturing { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            _symbols = new SymbolTable();
            _sink = new RecordingIpcCommandSink();
            IsCapturing = true;
        }

        _logger.LogInformation("IPC trace capture started");
    }

    public string? Stop()
    {
        Trace trace;
        lock (_gate)
        {
            if (!IsCapturing)
            {
                return null;
            }

            IsCapturing = false;
            trace = _sink.ToTrace();
        }

        var directory = Path.Combine(_configService.ConfigurationDirectory, "traces");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"ipc-trace-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, TraceJson.Serialize(trace));
        _logger.LogInformation("IPC trace written to {path} ({count} events)", path, trace.Events.Count);
        return path;
    }

    public void Record(IpcCommand command)
    {
        lock (_gate)
        {
            if (IsCapturing)
            {
                _sink.Record(command);
            }
        }
    }

    public string Collection(Guid id)
    {
        lock (_gate)
        {
            return _symbols.Collection(id);
        }
    }

    public string Application(Guid id)
    {
        lock (_gate)
        {
            return _symbols.Application(id);
        }
    }

    public string CustomizeId(Guid id)
    {
        lock (_gate)
        {
            return _symbols.CustomizeId(id);
        }
    }

    public string Handle(nint address)
    {
        lock (_gate)
        {
            return _symbols.Handle(address);
        }
    }

    public string HandleIndex(int objectIndex)
    {
        lock (_gate)
        {
            return _symbols.Symbol("handle", "idx:" + objectIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
