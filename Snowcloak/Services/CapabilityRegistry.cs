using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.ServerConfiguration;

namespace Snowcloak.Services;

public class CapabilityRegistry
{
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<CapabilityRegistry> _logger;
    private readonly IChatGui _chatGui;
    private SortedDictionary<string, float> _capabilities = new();
    private readonly Dictionary<string, string> _longNames = new();
    public CapabilityRegistry(ServerConfigurationManager serverConfigurationManager, ILogger<CapabilityRegistry> logger,
        IChatGui chatGui)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
        _chatGui = chatGui;
        _longNames.Add("SCF", "Snowcloak Cache Files");
        _longNames.Add("Hash", "Hashing Version");
        _longNames.Add("Compress", "File Compression");
        _longNames.Add("ClientDB", "Client Database");
    }

    public void RegisterCapability(string capability, float version)
    {
        try
        {
            _capabilities.Add(capability, version);
            _logger.Log(LogLevel.Information, "Added capability {capability} with level {version}", capability, version);
#if DEBUG
            _chatGui.Print($"Registered capability {capability} with level {version}");
#endif
        }
        catch (ArgumentException)
        {
            _logger.Log(LogLevel.Information, "Capability {capability} already registered with. Skipping...", capability, version);

        }

    }

    public bool HasCapability(string requestedCapability, float level)
    {
        if (!_capabilities.TryGetValue(requestedCapability, out float clientCapability)) return false;
        return clientCapability >= level;
    }

    public SortedDictionary<string, float> GetCapabilities()
    {
        return _capabilities;
    }

    public string GetCapabilityFullName(string requestedCapability)
    {
        if (!_longNames.TryGetValue(requestedCapability, out string capabilityName)) return requestedCapability;
        return capabilityName;
    }
}
