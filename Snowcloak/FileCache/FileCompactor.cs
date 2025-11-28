using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Snowcloak.Configuration;
using Snowcloak.Services;
using System.Runtime.InteropServices;

namespace Snowcloak.FileCache;

public sealed class FileCompactor
{
    private const uint FSCTL_SET_COMPRESSION = 0x9C040U;
    private const ushort COMPRESSION_FORMAT_NONE = 0x0000;
    private const ushort COMPRESSION_FORMAT_DEFAULT = 0x0001;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private readonly Dictionary<string, int> _clusterSizes;

    private readonly ILogger<FileCompactor> _logger;

    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly DalamudUtilService _dalamudUtilService;
    
    private bool _directoryCompressionEnsured;
    public FileCompactor(ILogger<FileCompactor> logger, SnowcloakConfigService snowcloakConfigService, DalamudUtilService dalamudUtilService)
    {
        _clusterSizes = new(StringComparer.Ordinal);
        _logger = logger;
        _snowcloakConfigService = snowcloakConfigService;
        _dalamudUtilService = dalamudUtilService;
        InitialiseDirectoryCompression();
    }

    public bool MassCompactRunning { get; private set; } = false;

    public string Progress { get; private set; } = string.Empty;

    public void CompactStorage(bool compress)
    {
        if (_dalamudUtilService.IsWine)
        {
            return;
        }

        MassCompactRunning = true;
        try
        {
            AdjustDirectoryCompressionState(compress);
        }
        finally
        {
            Progress = string.Empty;
            MassCompactRunning = false;
        }
    }

    public long GetFileSizeOnDisk(FileInfo fileInfo, bool? isNTFS = null)
    {
        bool ntfs = isNTFS ?? string.Equals(new DriveInfo(fileInfo.Directory!.Root.FullName).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);

        if (_dalamudUtilService.IsWine || !ntfs) return fileInfo.Length;

        var clusterSize = GetClusterSize(fileInfo);
        if (clusterSize == -1) return fileInfo.Length;
        var losize = GetCompressedFileSizeW(fileInfo.FullName, out uint hosize);
        var size = (long)hosize << 32 | losize;
        return ((size + clusterSize - 1) / clusterSize) * clusterSize;
    }

    public async Task WriteAllBytesAsync(string filePath, byte[] decompressedFile, CancellationToken token)
    {
        await File.WriteAllBytesAsync(filePath, decompressedFile, token).ConfigureAwait(false);
    }

    public void RenameAndCompact(string filePath, string originalFilePath)
    {
        try
        {
            File.Move(originalFilePath, filePath);
        }
        catch (IOException)
        {
            // File already exists
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode, IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, ref ushort lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
    [DllImport("kernel32.dll")]
    private static extern uint GetCompressedFileSizeW([In, MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
                                              [Out, MarshalAs(UnmanagedType.U4)] out uint lpFileSizeHigh);

    [DllImport("kernel32.dll", SetLastError = true, PreserveSig = true)]
    private static extern int GetDiskFreeSpaceW([In, MarshalAs(UnmanagedType.LPWStr)] string lpRootPathName,
           out uint lpSectorsPerCluster, out uint lpBytesPerSector, out uint lpNumberOfFreeClusters,
           out uint lpTotalNumberOfClusters);
    
    private int GetClusterSize(FileInfo fi)
    {
        if (!fi.Exists) return -1;
        var root = fi.Directory?.Root.FullName.ToLower() ?? string.Empty;
        if (string.IsNullOrEmpty(root)) return -1;
        if (_clusterSizes.TryGetValue(root, out int value)) return value;
        _logger.LogDebug("Getting Cluster Size for {path}, root {root}", fi.FullName, root);
        int result = GetDiskFreeSpaceW(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _);
        if (result == 0) return -1;
        _clusterSizes[root] = (int)(sectorsPerCluster * bytesPerSector);
        _logger.LogDebug("Determined Cluster Size for root {root}: {cluster}", root, _clusterSizes[root]);
        return _clusterSizes[root];
    }
    
    private void EnsureDirectoryCompression()
    {
        if (_directoryCompressionEnsured)
        {
            return;
        }

        var cacheFolder = _snowcloakConfigService.Current.CacheFolder;
        if (string.IsNullOrEmpty(cacheFolder))
        {
            return;
        }

        if (!EnsureCacheDirectoryExists(cacheFolder))
        {
            return;
        }
        if (!IsCompressionSupportedForPath(cacheFolder))
        {
            return;
        }

        SetCompression(cacheFolder, compress: true, isDirectory: true);
        _directoryCompressionEnsured = true;
    }

    private void InitialiseDirectoryCompression()
    {
        if (_dalamudUtilService.IsWine || !_snowcloakConfigService.Current.UseCompactor)
        {
            return;
        }
        EnsureDirectoryCompression();
    }

    private void SetCompression(string path, bool compress, bool isDirectory)    {
        try
        {
            var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
                isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0U, IntPtr.Zero);

            using (handle)
            {
                if (handle.IsInvalid)
                {
                    _logger.LogWarning("Failed to acquire handle for {path} while setting compression", path);
                    return;
                }

                ushort compression = compress ? COMPRESSION_FORMAT_DEFAULT : COMPRESSION_FORMAT_NONE;
                if (!DeviceIoControl(handle, FSCTL_SET_COMPRESSION, ref compression, sizeof(ushort), IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    _logger.LogWarning("Failed to set compression on {path}: 0x{error:X}", path, error);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error setting compression on {path}", path);        }
    }

    private void AdjustDirectoryCompressionState(bool compress)
    {
        var cacheFolder = _snowcloakConfigService.Current.CacheFolder;
        if (string.IsNullOrEmpty(cacheFolder))
        {
            return;
        }
        if (!EnsureCacheDirectoryExists(cacheFolder))
        {
            return;
        }
        if (!IsCompressionSupportedForPath(cacheFolder))
        {
            _directoryCompressionEnsured = false;
            return;
        }


        SetCompression(cacheFolder, compress, isDirectory: true);
        _directoryCompressionEnsured = compress;
    }

    private bool EnsureCacheDirectoryExists(string cacheFolder)    {
        try
        {
            if (!Directory.Exists(cacheFolder))
            {
                Directory.CreateDirectory(cacheFolder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure cache directory exists at {path}", cacheFolder);
            return false;
        }
        return Directory.Exists(cacheFolder);
    }
    private bool IsCompressionSupportedForPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                _logger.LogDebug("Unable to determine drive root for {path}", fullPath);
                return false;
            }

            var driveInfo = new DriveInfo(root);
            if (!string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Skipping compression for {path} because drive format is {format} instead of NTFS", fullPath, driveInfo.DriveFormat);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine drive format for {path}", path);
            return false;
        }
    }
}