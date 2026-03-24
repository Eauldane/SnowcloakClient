using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Management;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace Snowcloak.Services;

public sealed class GpuMemoryBudgetService
{
    private delegate bool AdapterEnumerator(uint index, out IDXGIAdapter1? adapter);

    private readonly Lock _syncRoot = new();
    private readonly ILogger<GpuMemoryBudgetService> _logger;
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(2);
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private GpuMemoryBudgetSnapshot? _cachedSnapshot;
    private bool _loggedUnavailable;

    public GpuMemoryBudgetService(ILogger<GpuMemoryBudgetService> logger)
    {
        _logger = logger;
    }

    public GpuMemoryBudgetSnapshot? GetCurrentBudget()
    {
        lock (_syncRoot)
        {
            if (_cachedSnapshot != null && DateTime.UtcNow - _lastRefreshUtc <= _cacheLifetime)
            {
                return _cachedSnapshot;
            }

            _cachedSnapshot = QueryCurrentBudget();
            _lastRefreshUtc = DateTime.UtcNow;
            return _cachedSnapshot;
        }
    }

    private GpuMemoryBudgetSnapshot? QueryCurrentBudget()
    {
        try
        {
            var liveSnapshot = TryQueryLiveDxgiSnapshot();
            if (liveSnapshot != null)
            {
                return liveSnapshot;
            }

            using var selectedAdapter = SelectAdapter();

            if (selectedAdapter == null)
            {
                return null;
            }

            var selectedDescription = selectedAdapter.Description1;
            try
            {
                using var adapter3 = selectedAdapter.QueryInterfaceOrNull<IDXGIAdapter3>();
                if (adapter3 != null)
                {
                    try
                    {
                        var memoryInfo = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                        var budgetBytes = (long)memoryInfo.Budget;
                        var currentUsageBytes = (long)memoryInfo.CurrentUsage;
                        var availableBytes = Math.Max(0L, budgetBytes - currentUsageBytes);

                        if (budgetBytes > 0 || currentUsageBytes > 0)
                        {
                            return new GpuMemoryBudgetSnapshot(
                                selectedDescription.Description,
                                GetFallbackBudgetBytes(selectedDescription),
                                budgetBytes,
                                currentUsageBytes,
                                availableBytes,
                                false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogTrace(ex, "DXGI adapter budget query failed for {AdapterName}, falling back to adapter memory", selectedDescription.Description);
                    }
                }

                var fallbackBytes = GetFallbackBudgetBytes(selectedDescription);
                if (fallbackBytes <= 0)
                {
                    return null;
                }

                return new GpuMemoryBudgetSnapshot(
                    selectedDescription.Description,
                    fallbackBytes,
                    fallbackBytes,
                    -1,
                    fallbackBytes,
                    true);
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "DXGI adapter probing failed for {AdapterName}", selectedDescription.Description);
                return null;
            }
        }
        catch (Exception ex)
        {
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                _logger.LogDebug(ex, "Unable to query DXGI video memory budget");
            }

            var registrySnapshot = TryQueryRegistryAdapterMemory();
            if (registrySnapshot != null)
            {
                return registrySnapshot;
            }

            return TryQueryWmiAdapterMemory();
        }
    }

    private GpuMemoryBudgetSnapshot? TryQueryLiveDxgiSnapshot()
    {
        using var factory1 = CreateDXGIFactory1<IDXGIFactory1>();

        using var factory6 = factory1.QueryInterfaceOrNull<IDXGIFactory6>();
        var preferredSnapshot = factory6 != null
            ? TryEnumerateBestLiveSnapshot((uint index, out IDXGIAdapter1? adapter) => factory6.EnumAdapterByGpuPreference(index, GpuPreference.HighPerformance, out adapter).Success)
            : null;

        return preferredSnapshot
            ?? TryEnumerateBestLiveSnapshot((uint index, out IDXGIAdapter1? adapter) => factory1.EnumAdapters1(index, out adapter).Success);
    }

    private static IDXGIAdapter1? SelectAdapter()
    {
        using var factory1 = CreateDXGIFactory1<IDXGIFactory1>();

        using var factory6 = factory1.QueryInterfaceOrNull<IDXGIFactory6>();
        var preferredAdapter = factory6 != null
            ? TryEnumerateBestAdapter((uint index, out IDXGIAdapter1? adapter) => factory6.EnumAdapterByGpuPreference(index, GpuPreference.HighPerformance, out adapter).Success)
            : null;
        if (preferredAdapter != null)
        {
            return preferredAdapter;
        }

        return TryEnumerateBestAdapter((uint index, out IDXGIAdapter1? adapter) => factory1.EnumAdapters1(index, out adapter).Success);
    }

    private static IDXGIAdapter1? TryEnumerateBestAdapter(AdapterEnumerator enumerateAdapter)
    {
        IDXGIAdapter1? selectedAdapter = null;
        long selectedMemory = -1;

        for (uint index = 0; ; index++)
        {
            var success = enumerateAdapter(index, out var adapter);
            if (!success || adapter == null)
            {
                break;
            }

            var description = adapter.Description1;
            if ((description.Flags & AdapterFlags.Software) != 0)
            {
                adapter.Dispose();
                continue;
            }

            var candidateMemory = GetFallbackBudgetBytes(description);
            if (candidateMemory > selectedMemory)
            {
                selectedAdapter?.Dispose();
                selectedAdapter = adapter;
                selectedMemory = candidateMemory;
            }
            else
            {
                adapter.Dispose();
            }
        }

        return selectedAdapter;
    }

    private GpuMemoryBudgetSnapshot? TryEnumerateBestLiveSnapshot(AdapterEnumerator enumerateAdapter)
    {
        GpuMemoryBudgetSnapshot? selectedSnapshot = null;

        for (uint index = 0; ; index++)
        {
            var success = enumerateAdapter(index, out var adapter);
            if (!success || adapter == null)
            {
                break;
            }

            using (adapter)
            {
                var description = adapter.Description1;
                if ((description.Flags & AdapterFlags.Software) != 0)
                {
                    continue;
                }

                using var adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
                if (adapter3 == null)
                {
                    continue;
                }

                try
                {
                    var memoryInfo = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                    var budgetBytes = (long)memoryInfo.Budget;
                    var currentUsageBytes = (long)memoryInfo.CurrentUsage;
                    var availableBytes = Math.Max(0L, budgetBytes - currentUsageBytes);
                    var totalBytes = GetFallbackBudgetBytes(description);
                    if (totalBytes <= 0)
                    {
                        totalBytes = Math.Max(budgetBytes, availableBytes);
                    }

                    if ((budgetBytes <= 0 && currentUsageBytes <= 0) || totalBytes <= 0)
                    {
                        continue;
                    }

                    var candidateSnapshot = new GpuMemoryBudgetSnapshot(
                        description.Description,
                        totalBytes,
                        budgetBytes,
                        currentUsageBytes,
                        availableBytes,
                        false);

                    if (selectedSnapshot == null || candidateSnapshot.TotalBytes > selectedSnapshot.TotalBytes)
                    {
                        selectedSnapshot = candidateSnapshot;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "DXGI live memory query failed for {AdapterName}", description.Description);
                }
            }
        }

        return selectedSnapshot;
    }

    private static long GetFallbackBudgetBytes(AdapterDescription1 description)
    {
        var dedicated = (long)description.DedicatedVideoMemory;
        if (dedicated > 0)
        {
            return dedicated;
        }

        return (long)description.SharedSystemMemory;
    }

    private GpuMemoryBudgetSnapshot? TryQueryRegistryAdapterMemory()
    {
        try
        {
            var preferredIdentity = TryGetPreferredAdapterIdentity();
            using var videoRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if (videoRoot == null)
            {
                return null;
            }

            string? bestName = null;
            long bestMemory = -1;
            var bestScore = int.MinValue;

            foreach (var adapterKeyName in videoRoot.GetSubKeyNames())
            {
                using var adapterKey = videoRoot.OpenSubKey(adapterKeyName);
                if (adapterKey == null)
                {
                    continue;
                }

                foreach (var childKeyName in adapterKey.GetSubKeyNames())
                {
                    using var childKey = adapterKey.OpenSubKey(childKeyName);
                    if (childKey == null)
                    {
                        continue;
                    }

                    var driverDesc = childKey.GetValue("DriverDesc")?.ToString();
                    var matchingDeviceId = childKey.GetValue("MatchingDeviceId")?.ToString();
                    var memorySize = ReadRegistryMemorySize(childKey);
                    if (memorySize <= 0)
                    {
                        continue;
                    }

                    var score = ScoreAdapterMatch(preferredIdentity, driverDesc, matchingDeviceId);
                    if (score > bestScore || (score == bestScore && memorySize > bestMemory))
                    {
                        bestScore = score;
                        bestMemory = memorySize;
                        bestName = !string.IsNullOrWhiteSpace(driverDesc)
                            ? driverDesc
                            : matchingDeviceId;
                    }
                }
            }

            if (bestMemory <= 0)
            {
                return null;
            }

            return new GpuMemoryBudgetSnapshot(
                bestName ?? preferredIdentity.Name ?? "Unknown GPU",
                bestMemory,
                bestMemory,
                -1,
                bestMemory,
                true);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Registry GPU memory query failed");
            return null;
        }
    }

    private GpuMemoryBudgetSnapshot? TryQueryWmiAdapterMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, Status FROM Win32_VideoController");
            using var results = searcher.Get();

            string? selectedName = null;
            long selectedMemory = -1;

            foreach (var result in results)
            {
                using var managementObject = (ManagementObject)result;
                var status = managementObject["Status"]?.ToString();
                if (!string.IsNullOrEmpty(status) && !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var adapterRam = TryConvertToInt64(managementObject["AdapterRAM"]);
                if (adapterRam <= selectedMemory)
                {
                    continue;
                }

                selectedMemory = adapterRam;
                selectedName = managementObject["Name"]?.ToString();
            }

            if (selectedMemory <= 0)
            {
                return null;
            }

            return new GpuMemoryBudgetSnapshot(
                selectedName ?? "Unknown GPU",
                selectedMemory,
                selectedMemory,
                -1,
                selectedMemory,
                true);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "WMI GPU memory query failed");
            return null;
        }
    }

    private static AdapterIdentity TryGetPreferredAdapterIdentity()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, Status FROM Win32_VideoController");
            using var results = searcher.Get();

            string? selectedName = null;
            string? selectedPnpDeviceId = null;

            foreach (var result in results)
            {
                using var managementObject = (ManagementObject)result;
                var status = managementObject["Status"]?.ToString();
                if (!string.IsNullOrEmpty(status) && !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                selectedName ??= managementObject["Name"]?.ToString();
                selectedPnpDeviceId ??= managementObject["PNPDeviceID"]?.ToString();

                if (!string.IsNullOrWhiteSpace(selectedName) || !string.IsNullOrWhiteSpace(selectedPnpDeviceId))
                {
                    break;
                }
            }

            return new AdapterIdentity(selectedName, selectedPnpDeviceId);
        }
        catch
        {
            return new AdapterIdentity(null, null);
        }
    }

    private static long ReadRegistryMemorySize(RegistryKey registryKey)
    {
        var qwordMemorySize = TryConvertToInt64(registryKey.GetValue("HardwareInformation.qwMemorySize"));
        if (qwordMemorySize > 0)
        {
            return qwordMemorySize;
        }

        return TryConvertToInt64(registryKey.GetValue("HardwareInformation.MemorySize"));
    }

    private static int ScoreAdapterMatch(AdapterIdentity preferredIdentity, string? driverDesc, string? matchingDeviceId)
    {
        var score = 0;

        if (!string.IsNullOrWhiteSpace(preferredIdentity.PnpDeviceId) && !string.IsNullOrWhiteSpace(matchingDeviceId))
        {
            var normalizedPnp = preferredIdentity.PnpDeviceId.ToLowerInvariant();
            var normalizedMatch = matchingDeviceId.ToLowerInvariant();
            if (normalizedPnp.Contains(normalizedMatch, StringComparison.Ordinal)
                || normalizedMatch.Contains(normalizedPnp, StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredIdentity.Name) && !string.IsNullOrWhiteSpace(driverDesc))
        {
            if (string.Equals(preferredIdentity.Name, driverDesc, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
            }
            else if (driverDesc.Contains(preferredIdentity.Name, StringComparison.OrdinalIgnoreCase)
                     || preferredIdentity.Name.Contains(driverDesc, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }
        }

        return score;
    }

    private static long TryConvertToInt64(object? value)
    {
        return value switch
        {
            null => -1,
            long longValue => longValue,
            int intValue => intValue,
            uint uintValue => uintValue,
            ulong ulongValue when ulongValue <= long.MaxValue => (long)ulongValue,
            ushort ushortValue => ushortValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            sbyte sbyteValue => sbyteValue,
            byte[] bytes when bytes.Length >= sizeof(long) => BitConverter.ToInt64(bytes, 0),
            byte[] bytes when bytes.Length >= sizeof(int) => BitConverter.ToUInt32(bytes, 0),
            string stringValue when long.TryParse(stringValue, out var parsed) => parsed,
            _ => -1,
        };
    }
}

public sealed record AdapterIdentity(string? Name, string? PnpDeviceId);

public sealed record GpuMemoryBudgetSnapshot(
    string AdapterName,
    long TotalBytes,
    long BudgetBytes,
    long CurrentUsageBytes,
    long AvailableBytes,
    bool IsEstimated);
