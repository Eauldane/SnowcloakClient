using MareSynchronos.MareConfiguration;
using Microsoft.Extensions.Logging;

namespace MareSynchronos.Services;

public class DatabaseService
{
    private readonly CapabilityRegistry _capabilityRegistry;
    private readonly ILogger<CapabilityRegistry> _logger;

    public DatabaseService(CapabilityRegistry capabilityRegistry, ILogger<CapabilityRegistry> logger)
    {
        _capabilityRegistry = capabilityRegistry;
        _logger = logger;
        
        _capabilityRegistry.RegisterCapability("ClientDB", 0f);
    }
}
