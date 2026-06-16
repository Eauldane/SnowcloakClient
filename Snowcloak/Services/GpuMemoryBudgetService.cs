using Dalamud.Interface;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace Snowcloak.Services;

public sealed partial class GpuMemoryBudgetService
{
    private delegate bool AdapterEnumerator(uint index, out IDXGIAdapter1? adapter);

    private readonly Lock _syncRoot = new();
    private readonly IUiBuilder _uiBuilder;
    private readonly ILogger<GpuMemoryBudgetService> _logger;
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(2);
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private GpuMemoryBudgetSnapshot? _cachedSnapshot;
    private bool _loggedUnavailable;

    public GpuMemoryBudgetService(ILogger<GpuMemoryBudgetService> logger, IUiBuilder uiBuilder)
    {
        _logger = logger;
        _uiBuilder = uiBuilder;
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
            var liveSnapshot = TryQueryActiveDeviceSnapshot() ?? TryQueryLiveDxgiSnapshot();
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
                return TryCreateSnapshot(selectedAdapter);
            }
            catch (Exception ex)
            {
                LogDxgiAdapterProbingFailed(_logger, ex, selectedDescription.Description);
                return null;
            }
        }
        catch (Exception ex)
        {
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                LogUnableToQueryDxgiBudget(_logger, ex);
            }

            var registrySnapshot = TryQueryRegistryAdapterMemory();
            return registrySnapshot;
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

    private GpuMemoryBudgetSnapshot? TryQueryActiveDeviceSnapshot()
    {
        var deviceHandle = _uiBuilder.DeviceHandle;
        if (deviceHandle == IntPtr.Zero)
        {
            return null;
        }

        var dxgiDeviceId = typeof(IDXGIDevice).GUID;
        var queryResult = Marshal.QueryInterface(deviceHandle, in dxgiDeviceId, out var dxgiDevicePointer);
        if (queryResult != 0 || dxgiDevicePointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var dxgiDevice = new IDXGIDevice(dxgiDevicePointer);
            IDXGIAdapter? adapter = null;
            try
            {
                if (!dxgiDevice.GetAdapter(out adapter).Success || adapter == null)
                {
                    return null;
                }

                using var adapterOwner = adapter;
                adapter = null;
                using var adapter1 = adapterOwner.QueryInterfaceOrNull<IDXGIAdapter1>();
                return adapter1 == null ? null : TryCreateSnapshot(adapter1);
            }
            finally
            {
                adapter?.Dispose();
            }
        }
        catch (Exception ex)
        {
            LogActiveDeviceAdapterQueryFailed(_logger, ex);
            return null;
        }
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

        for (uint index = 0; index < uint.MaxValue; index++)
        {
            IDXGIAdapter1? adapter = null;
            var success = enumerateAdapter(index, out adapter);
            if (!success || adapter == null)
            {
                break;
            }

            var description = adapter.Description1;
            if (description.Flags.HasFlag(AdapterFlags.Software))
            {
                adapter.Dispose();
                continue;
            }

            var candidateMemory = GetFallbackBudgetBytes(description);
            var candidateDedicated = description.DedicatedVideoMemory > 0;
            var selectedDedicated = selectedAdapter?.Description1.DedicatedVideoMemory > 0;
            if (selectedAdapter == null
                || (candidateDedicated && !selectedDedicated)
                || (candidateDedicated == selectedDedicated && candidateMemory > selectedMemory))
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

        for (uint index = 0; index < uint.MaxValue; index++)
        {
            IDXGIAdapter1? adapter = null;
            try
            {
                var success = enumerateAdapter(index, out adapter);
                if (!success || adapter == null)
                {
                    break;
                }

                using var adapterOwner = adapter;
                adapter = null;
                var description = adapterOwner.Description1;
                if (description.Flags.HasFlag(AdapterFlags.Software))
                {
                    continue;
                }

                var candidateSnapshot = TryCreateSnapshot(adapterOwner);
                if (candidateSnapshot == null)
                {
                    continue;
                }

                var candidateDedicated = description.DedicatedVideoMemory > 0;
                var selectedDedicated = selectedSnapshot?.IsDedicatedLocalMemory == true;
                if (selectedSnapshot == null
                    || (candidateDedicated && !selectedDedicated)
                    || (candidateDedicated == selectedDedicated && candidateSnapshot.TotalBytes > selectedSnapshot.TotalBytes))
                {
                    selectedSnapshot = candidateSnapshot;
                }
            }
            finally
            {
                adapter?.Dispose();
            }
        }

        return selectedSnapshot;
    }

    private GpuMemoryBudgetSnapshot? TryCreateSnapshot(IDXGIAdapter1 adapter)
    {
        var description = adapter.Description1;
        using var adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
        if (adapter3 != null)
        {
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

                if ((budgetBytes > 0 || currentUsageBytes > 0) && totalBytes > 0)
                {
                    return new GpuMemoryBudgetSnapshot(
                        description.Description,
                        totalBytes,
                        budgetBytes,
                        currentUsageBytes,
                        availableBytes,
                        false,
                        description.DedicatedVideoMemory > 0);
                }
            }
            catch (Exception ex)
            {
                LogDxgiMemoryQueryFailed(_logger, ex, description.Description);
            }
        }

        var fallbackBytes = GetFallbackBudgetBytes(description);
        return fallbackBytes <= 0
            ? null
            : new GpuMemoryBudgetSnapshot(
                description.Description,
                fallbackBytes,
                fallbackBytes,
                -1,
                fallbackBytes,
                true,
                description.DedicatedVideoMemory > 0);
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

                    var score = ScoreAdapterMatch(preferredIdentity, driverDesc);
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
                true,
                true);
        }
        catch (Exception ex)
        {
            LogRegistryGpuMemoryQueryFailed(_logger, ex);
            return null;
        }
    }

    private static AdapterIdentity TryGetPreferredAdapterIdentity()
    {
        try
        {
            using var adapter = SelectAdapter();
            return new AdapterIdentity(adapter?.Description1.Description);
        }
        catch
        {
            return new AdapterIdentity(null);
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

    private static int ScoreAdapterMatch(AdapterIdentity preferredIdentity, string? driverDesc)
    {
        var score = 0;

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

    [LoggerMessage(EventId = 0, Level = LogLevel.Trace, Message = "DXGI adapter probing failed for {AdapterName}")]
    private static partial void LogDxgiAdapterProbingFailed(ILogger logger, Exception exception, string adapterName);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Unable to query DXGI video memory budget")]
    private static partial void LogUnableToQueryDxgiBudget(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Trace, Message = "DXGI active device adapter query failed")]
    private static partial void LogActiveDeviceAdapterQueryFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Trace, Message = "DXGI memory query failed for {AdapterName}")]
    private static partial void LogDxgiMemoryQueryFailed(ILogger logger, Exception exception, string adapterName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Trace, Message = "Registry GPU memory query failed")]
    private static partial void LogRegistryGpuMemoryQueryFailed(ILogger logger, Exception exception);
}

public sealed record AdapterIdentity(string? Name);

public sealed record GpuMemoryBudgetSnapshot(
    string AdapterName,
    long TotalBytes,
    long BudgetBytes,
    long CurrentUsageBytes,
    long AvailableBytes,
    bool IsEstimated,
    bool IsDedicatedLocalMemory);
