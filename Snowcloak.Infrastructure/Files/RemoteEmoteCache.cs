using System.Net;
using System.Text.RegularExpressions;

namespace Snowcloak.Infrastructure.Files;

public sealed partial class RemoteEmoteCache(string directory, Action<Exception> onFailure, HttpMessageHandler? handler = null) : IDisposable
{
    public static readonly Uri IndexUri = new("https://eauldane.com/emotes/");
    private const int MaxIndexBytes = 4 * 1024 * 1024;
    private const int MaxPngBytes = 2 * 1024 * 1024;
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MaxIndexBytes,
    };
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _downloads = new(4);
    private readonly object _sync = new();
    private readonly Dictionary<string, (Task<string?> Task, DateTime RetryAfter)> _pending = new(StringComparer.Ordinal);
    private Task<IReadOnlyDictionary<string, string>>? _discovery;
    private DateTime _discoverAfter;
    private bool _disposed;

    public Task<IReadOnlyDictionary<string, string>> DiscoverEmoteFiles()
    {
        lock (_sync)
        {
            if (!_disposed && (_discovery == null || (_discovery.IsCompleted && DateTime.UtcNow >= _discoverAfter)))
            {
                _discoverAfter = DateTime.UtcNow.AddMinutes(5);
                _discovery = Task.Run(DiscoverAsync);
            }
            return _discovery ?? Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }
    }

    public string? GetCachedFile(string source)
    {
        if (!TryGetFileName(source, out var fileName)) return null;
        lock (_sync)
        {
            if (_disposed) return null;
            if (_pending.TryGetValue(fileName, out var load))
            {
                if (!load.Task.IsCompletedSuccessfully) return null;
                if (load.Task.Result != null) return load.Task.Result;
                if (DateTime.UtcNow < load.RetryAfter) return null;
            }
            _pending[fileName] = (Task.Run(() => LoadAsync(fileName)), DateTime.UtcNow.AddMinutes(1));
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> DiscoverAsync()
    {
        var indexPath = Path.Combine(directory, "index.html");
        try
        {
            var bytes = await DownloadAsync(IndexUri, MaxIndexBytes).ConfigureAwait(false);
            var mappings = ParseIndex(System.Text.Encoding.UTF8.GetString(bytes));
            if (mappings.Count == 0) throw new InvalidDataException("Emote index contains no PNG links.");
            await StoreAsync(indexPath, bytes).ConfigureAwait(false);
            return mappings;
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            if (!_shutdown.IsCancellationRequested) onFailure(ex);
            try
            {
                if (File.Exists(indexPath) && new FileInfo(indexPath).Length <= MaxIndexBytes)
                    return ParseIndex(await File.ReadAllTextAsync(indexPath, _shutdown.Token).ConfigureAwait(false));
            }
            catch (Exception cacheEx) when (IsLoadFailure(cacheEx)) { }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static IReadOnlyDictionary<string, string> ParseIndex(string html)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HrefRegex().Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            if (!TryGetFileName(href, out var fileName)) continue;
            mappings.TryAdd(Path.GetFileNameWithoutExtension(fileName), new Uri(IndexUri, fileName).AbsoluteUri);
        }
        return mappings;
    }

    private static bool TryGetFileName(string href, out string fileName)
    {
        fileName = string.Empty;
        if (!Uri.TryCreate(IndexUri, href, out var uri) || uri.Scheme != IndexUri.Scheme
            || uri.Authority != IndexUri.Authority || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.StartsWith(IndexUri.AbsolutePath, StringComparison.Ordinal)) return false;
        var name = Uri.UnescapeDataString(uri.AbsolutePath[IndexUri.AbsolutePath.Length..]);
        // Flat, portable names only: excludes parent/subdirectory links, encoded separators and device names.
        if (!FileNameRegex().IsMatch(name) || name.Contains("..", StringComparison.Ordinal)) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        fileName = name;
        return true;
    }

    private async Task<string?> LoadAsync(string fileName)
    {
        var entered = false;
        try
        {
            await _downloads.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            entered = true;
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path) && new FileInfo(path).Length <= MaxPngBytes)
            {
                var cached = await File.ReadAllBytesAsync(path, _shutdown.Token).ConfigureAwait(false);
                if (IsPng(cached)) return path;
            }
            var bytes = await DownloadAsync(new Uri(IndexUri, fileName), MaxPngBytes).ConfigureAwait(false);
            if (!IsPng(bytes)) throw new InvalidDataException("Emote response is not a PNG.");
            await StoreAsync(path, bytes).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            if (!_shutdown.IsCancellationRequested) onFailure(ex);
            return null;
        }
        finally { if (entered) _downloads.Release(); }
    }

    private async Task<byte[]> DownloadAsync(Uri uri, int limit)
    {
        using var response = await _http.GetAsync(uri, _shutdown.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Emote response is too large.");
        var bytes = await response.Content.ReadAsByteArrayAsync(_shutdown.Token).ConfigureAwait(false);
        if (bytes.Length > limit) throw new InvalidDataException("Emote response is too large.");
        return bytes;
    }

    private async Task StoreAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, _shutdown.Token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsPng(byte[] bytes)
        => bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8);

    private static bool IsLoadFailure(Exception ex)
        => ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException
            or ArgumentException or ObjectDisposedException or RegexMatchTimeoutException;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _http.Dispose();
            var tasks = _pending.Values.Select(load => (Task)load.Task).ToList();
            if (_discovery != null) tasks.Add(_discovery);
            _ = Task.WhenAll(tasks).ContinueWith(_ => { _downloads.Dispose(); _shutdown.Dispose(); }, TaskScheduler.Default);
        }
    }

    [GeneratedRegex("\\bhref\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_+\-][a-zA-Z0-9_+\-.]{0,180}\.png$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex FileNameRegex();
}
