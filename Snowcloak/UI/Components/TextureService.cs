using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Dalamud.Game;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace Snowcloak.UI.Components;

public sealed class TextureService : IDisposable
{
    private const int MaxRemoteImageBytes = 8 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private const int MaxConcurrentDownloads = 8;
    private const int MaxRemoteCacheEntries = 256;
    private static readonly TimeSpan RemoteImageTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] AllowedRemoteImageSchemes = [Uri.UriSchemeHttp, Uri.UriSchemeHttps];
    private readonly ITextureProvider _textureProvider;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadSemaphore = new(MaxConcurrentDownloads);
    private readonly Lock _sync = new();
    private readonly Dictionary<string, IDalamudTextureWrap> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _remoteCacheOrder = new();
    private readonly CancellationTokenSource _disposeCts = new();

    public TextureService(ITextureProvider textureProvider)
    {
        _textureProvider = textureProvider;
        _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    public IDalamudTextureWrap LoadImage(byte[] imageData)
    {
        ArgumentNullException.ThrowIfNull(imageData);

        if (imageData.Length == 0)
        {
            return _textureProvider.CreateEmpty(new()
            {
                Width = 256,
                Height = 256,
                DxgiFormat = 3,
                Pitch = 1024,
            }, cpuRead: false, cpuWrite: false);
        }

        return _textureProvider.CreateFromImageAsync(imageData).Result;
    }

    public ISharedImmediateTexture GetGameIcon(uint iconId, bool itemHq = false, bool hiRes = true, ClientLanguage? language = null)
    {
        return _textureProvider.GetFromGameIcon(new GameIconLookup(iconId, itemHq, hiRes, language));
    }

    public bool TryGetGameIcon(uint iconId, out ISharedImmediateTexture? texture, bool itemHq = false, bool hiRes = true, ClientLanguage? language = null)
    {
        texture = null;
        try
        {
            var lookup = new GameIconLookup(iconId, itemHq, hiRes, language);
            if (_textureProvider.TryGetFromGameIcon(in lookup, out var shared))
            {
                texture = shared;
                return true;
            }
        }
        catch (Exception ex) when (IsExpectedImageLoadFailure(ex))
        {
            texture = null;
        }

        return false;
    }

    public IDalamudTextureWrap? GetImage(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        return Uri.TryCreate(source, UriKind.Absolute, out var uri) && IsAllowedRemoteImageScheme(uri)
            ? GetRemoteImage(source)
            : null;
    }

    public IDalamudTextureWrap? GetFile(string path)
    {
        return File.Exists(path) ? GetOrAdd(path, () => File.ReadAllBytes(path)) : null;
    }

    private IDalamudTextureWrap? GetOrAdd(string key, Func<byte[]> loadAction)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            if (_failed.Contains(key)) return null;
        }

        try
        {
            var data = loadAction.Invoke();
            var texture = _textureProvider.CreateFromImageAsync(data).Result;
            lock (_sync)
            {
                _cache[key] = texture;
            }
            return texture;
        }
        catch (Exception ex) when (IsExpectedImageLoadFailure(ex))
        {
            lock (_sync)
            {
                _failed.Add(key);
            }
        }

        return null;
    }

    private IDalamudTextureWrap? GetRemoteImage(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !IsAllowedRemoteImageScheme(uri))
        {
            lock (_sync)
            {
                _failed.Add(source);
            }

            return null;
        }

        lock (_sync)
        {
            if (_cache.TryGetValue(source, out var cached)) return cached;
            if (_failed.Contains(source)) return null;
            if (_loading.ContainsKey(source)) return null;

            _loading[source] = Task.Run(() => LoadRemoteImageAsync(source, uri, _disposeCts.Token));
        }

        return null;
    }

    private async Task LoadRemoteImageAsync(string source, Uri uri, CancellationToken disposeToken)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(RemoteImageTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(disposeToken, timeoutCts.Token);
            var data = await DownloadRemoteImageAsync(uri, linkedCts.Token).ConfigureAwait(false);
            var texture = await _textureProvider.CreateFromImageAsync(data, cancellationToken: disposeToken).ConfigureAwait(false);
            if (disposeToken.IsCancellationRequested)
            {
                texture.Dispose();
                return;
            }

            lock (_sync)
            {
                _cache[source] = texture;
                _remoteCacheOrder.Enqueue(source);
                TrimRemoteCache();
                _loading.Remove(source);
            }
        }
        catch (Exception ex) when (IsExpectedImageLoadFailure(ex))
        {
            lock (_sync)
            {
                _failed.Add(source);
                _loading.Remove(source);
            }
        }
        finally
        {
            lock (_sync)
            {
                _loading.Remove(source);
            }
        }
    }

    private void TrimRemoteCache()
    {
        while (_remoteCacheOrder.Count > MaxRemoteCacheEntries)
        {
            var oldest = _remoteCacheOrder.Dequeue();
            if (_cache.Remove(oldest, out var texture))
            {
                texture.Dispose();
            }
        }
    }

    private async Task<byte[]> DownloadRemoteImageAsync(Uri uri, CancellationToken token)
    {
        var currentUri = uri;
        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            if (!IsAllowedRemoteImageScheme(currentUri) || !await IsPublicHostAsync(currentUri.Host, token).ConfigureAwait(false))
            {
                throw new InvalidDataException("Remote image host is not allowed.");
            }

            await _downloadSemaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (IsRedirectStatusCode(response.StatusCode))
                {
                    var location = response.Headers.Location
                        ?? throw new InvalidDataException("Redirect response had no location.");
                    currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (contentType != null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Remote response was not an image.");
                }

                if (response.Content.Headers.ContentLength is > MaxRemoteImageBytes)
                {
                    throw new InvalidDataException("Remote image exceeds the maximum allowed size.");
                }

                return await ReadImageBodyAsync(response, token).ConfigureAwait(false);
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }

        throw new InvalidDataException("Remote image redirected too many times.");
    }

    private static async Task<byte[]> ReadImageBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaxRemoteImageBytes)
            {
                throw new InvalidDataException("Remote image exceeds the maximum allowed size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<bool> IsPublicHostAsync(string host, CancellationToken token)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                return false;
            }
        }

        return addresses.Length > 0 && Array.TrueForAll(addresses, IsPublicAddress);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;

            var v6Bytes = address.GetAddressBytes();
            return (v6Bytes[0] & 0xFE) != 0xFC;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            0 => false,
            10 => false,
            127 => false,
            169 when bytes[1] == 254 => false,
            172 when bytes[1] is >= 16 and <= 31 => false,
            192 when bytes[1] == 168 => false,
            100 when bytes[1] is >= 64 and <= 127 => false,
            _ when bytes[0] >= 224 => false,
            _ => true,
        };
    }

    private static bool IsAllowedRemoteImageScheme(Uri uri)
        => AllowedRemoteImageSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    private static bool IsExpectedImageLoadFailure(Exception ex)
        => ex is HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or ObjectDisposedException
            or WebException;

    public void Dispose()
    {
        _disposeCts.Cancel();
        List<IDalamudTextureWrap> textures;
        lock (_sync)
        {
            textures = _cache.Values.ToList();
            _cache.Clear();
            _loading.Clear();
            _failed.Clear();
            _remoteCacheOrder.Clear();
        }

        foreach (var texture in textures)
        {
            texture.Dispose();
        }

        _disposeCts.Dispose();
        _downloadSemaphore.Dispose();
        _httpClient.Dispose();
    }
}
