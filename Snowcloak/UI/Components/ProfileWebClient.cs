using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text.Json;
using System.Threading.Channels;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public sealed class ProfileWebClient : IDisposable
{
    private static readonly HashSet<ProfileWebClient> Clients = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(64);
    private readonly Channel<JsonElement> _incoming = Channel.CreateBounded<JsonElement>(32);
    private readonly NamedPipeServerStream? _pipe;
    private readonly Process? _process;
    private bool _disposed;
    public string Session { get; } = Guid.NewGuid().ToString("N");
    public string? Failure { get; private set; }
    public bool Connected { get; private set; }
    public bool Ready { get; private set; }
    public bool Attached { get; private set; }
    public string? PresentationStatus { get; private set; }
    private readonly DateTime _started = DateTime.UtcNow;
    public bool Focused { get; set; }
    public bool Hidden { get; private set; }
    public CancellationToken Lifetime => _stop.Token;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ProfileWebClient(string pluginDirectory)
    {
        lock (Clients) Clients.Add(this);
        var name = "snowcloak-profile-" + Session;
        try
        {
            _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            if (string.IsNullOrWhiteSpace(pluginDirectory)) throw new FileNotFoundException();
            var executable = Path.Combine(pluginDirectory, "ProfileWebHost", "Snowcloak.ProfileWebHost.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(name); start.ArgumentList.Add(Session); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _process = Process.Start(start) ?? throw new InvalidOperationException();
            _ = ConnectAsync();
        }
        catch (Exception) { Failure = "The bundled web builder host is unavailable. Rebuild/install the complete client package."; }
    }

    private async Task ConnectAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await _pipe!.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (_process == null || !GetNamedPipeClientProcessId(_pipe.SafePipeHandle.DangerousGetHandle(), out var client) || client != _process.Id)
                throw new InvalidDataException();
            Connected = true;
            var writer = WriteAsync();
            while (!_stop.IsCancellationRequested)
            {
                var bytes = await ProfileWebTransport.ReadAsync(_pipe, ProfileWebEditPolicy.MaxMessageBytes, _stop.Token).ConfigureAwait(false);
                if (bytes == null) break;
                using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                var message = parsed.RootElement;
                if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("session", out var session) || session.GetString() != Session) continue;
                if (message.TryGetProperty("type", out var type))
                {
                    if (type.GetString() == "ready") Ready = true;
                    if (type.GetString() == "attached") { Attached = true; continue; }
                    if (type.GetString() == "presentation")
                    {
                        var visible = message.GetProperty("visible").GetBoolean();
                        PresentationStatus = visible ? null : message.GetProperty("reason").GetString() switch
                        {
                            "frames" => "Designer paused while waiting for game frames.",
                            "dialog" => "Designer hidden while its dialog is open.",
                            "closed" => "Designer hidden while its page is closed.",
                            "owner" => "Designer hidden while the game window is minimized or hidden.",
                            "focus" => "Designer paused while another application is active.",
                            _ => "Designer surface is hidden.",
                        };
                        continue;
                    }
                    if (type.GetString() == "hostError")
                        Failure = HostFailureMessage(message);
                }
                await _incoming.Writer.WriteAsync(message.Clone(), _stop.Token).ConfigureAwait(false);
            }
            _stop.Cancel(); await writer.ConfigureAwait(false);
        }
        catch (Exception) when (_disposed) { }
        catch (Exception) { Failure ??= "The web builder connection stopped. Your native draft is retained; retry or use the native editor."; }
        finally { Connected = false; if (!_disposed && Failure == null) Failure = "The web builder closed. Your draft is retained."; }
    }

    private async Task WriteAsync()
    {
        await foreach (var bytes in _outgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            await ProfileWebTransport.WriteAsync(_pipe!, bytes, _stop.Token).ConfigureAwait(false);
    }

    public bool TryRead(out JsonElement message) => _incoming.Reader.TryRead(out message);
    public void Fail(string message) => Failure = message;
    public static string HostFailureMessage(JsonElement message)
    {
        var reason = message.TryGetProperty("reason", out var value) ? value.GetString() : null;
        if (reason == "attachment") return "The designer could not attach to the game window. Your draft is retained; retry or use the native editor.";
        if (reason == "renderer") return "The browser renderer stopped. Your draft is retained; retry the builder.";
        var stage = reason switch
        {
            "pipe" => "local connection", "privileges" => "standard user privileges", "runtimeOverrides" => "browser runtime overrides",
            "runtime" => "WebView2 runtime", "controller" => "browser controller", "settings" => "browser settings", "navigation" => "bundled designer page",
            _ => "browser startup",
        };
        var code = message.TryGetProperty("failureCode", out var failureCode) && failureCode.TryGetInt32(out var number) ? $" (0x{number:X8})" : string.Empty;
        return $"The web designer failed at {stage}{code}. Your draft is retained. Check the complete host package and WebView2 runtime, then retry.";
    }
    public string? StartupStatus
    {
        get
        {
            if (Failure == null && (!Ready || !Attached) && DateTime.UtcNow - _started > TimeSpan.FromSeconds(30))
                Failure = "The designer did not finish starting. Your draft is retained; retry the builder.";
            return !Connected ? "Starting the web profile builder..." : !Ready ? "Loading the designer page..." : !Attached ? "Attaching the designer to the game window…" : PresentationStatus;
        }
    }

    public static nint ResolveParentWindow(nint raw, nint platform)
    {
        foreach (var handle in new[] { raw, platform, Process.GetCurrentProcess().MainWindowHandle })
            if (handle != 0 && IsWindow(handle) && GetWindowThreadProcessId(handle, out var process) != 0 && process == Environment.ProcessId)
                return handle;
        return 0;
    }

    public static bool BrowserOwnsPointer()
    {
        lock (Clients) if (Clients.Count == 0) return false;
        if (!GetPhysicalCursorPos(out var point)) return false;
        var root = GetAncestor(WindowFromPhysicalPoint(point), 2);
        if (root == 0 || !IsWindowVisible(root)) return false;
        GetWindowThreadProcessId(root, out var process);
        lock (Clients) return Clients.Any(client => !client._disposed && client.Connected && client._process?.Id == process);
    }

    public static ProfileWebBounds[] CaptureOcclusion(Vector2 origin, Vector2 size, ProfileWebBounds bounds)
    {
        var context = Dalamud.Bindings.ImGui.ImGui.GetCurrentContext();
        var owner = context.CurrentWindow.RootWindow;
        var windows = new List<ProfileWebWindow>(context.Windows.Size);
        for (var index = 0; index < context.Windows.Size; index++)
        {
            var window = context.Windows[index];
            if ((!window.Active && !window.WasActive) || window.Hidden) continue;
            var topLayer = (window.Flags & (Dalamud.Bindings.ImGui.ImGuiWindowFlags.Tooltip | Dalamud.Bindings.ImGui.ImGuiWindowFlags.Popup)) != 0;
            windows.Add(new(window.ID, window.RootWindow.ID, window.ViewportId,
                true, topLayer,
                window.OuterRectClipped.Min, window.OuterRectClipped.Max));
        }
        var clip = context.CurrentWindow.ClipRect;
        return ProfileWebOcclusion.Cuts(windows, owner.ID, owner.ViewportId, origin, size, bounds, clip.Min, clip.Max);
    }

    public static bool TryMapViewport(nint parent, Vector2 origin, Vector2 size, Vector2 viewportOrigin,
        Vector2 viewportSize, out ProfileWebBounds bounds)
    {
        bounds = default;
        // Win32 screen coordinates must be physical pixels, regardless of the game's DPI mode.
        // Restore the game UI thread's original context immediately after these read-only calls.
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            var point = new NativePoint();
            if (!GetClientRect(parent, out var client) || !ClientToScreen(parent, ref point) ||
                client.Right <= client.Left || client.Bottom <= client.Top || viewportSize.X <= 0 || viewportSize.Y <= 0) return false;
            bounds = ProfileWebViewport.Map(origin, size, viewportOrigin, viewportSize, new(point.X, point.Y),
                new(client.Right - client.Left, client.Bottom - client.Top));
            return true;
        }
        finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
    }
    public void Send(object message)
    {
        if (_disposed || Failure != null) return;
        if (!_outgoing.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions)))
            Failure = "The web builder could not keep up with updates. Your draft is retained; retry the builder.";
    }

    public void SendViewport(object message, bool visible) { Hidden = !visible; Send(message); }
    public void Hide() { Focused = false; if (Hidden) return; Hidden = true; Send(new { type = "hide", session = Session }); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; lock (Clients) Clients.Remove(this);
        _stop.Cancel(); _pipe?.Dispose(); _outgoing.Writer.TryComplete();
        try { if (_process is { HasExited: false }) _process.Kill(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process?.Dispose();
    }

    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
