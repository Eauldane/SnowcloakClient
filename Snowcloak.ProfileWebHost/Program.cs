using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Snowcloak.Core.Profiles;

namespace Snowcloak.ProfileWebHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        if (args.Length == 1 && args[0] == "--embedding-self-test") { Application.Run(new EmbeddingSelfTest()); return; }
        if (args.Length == 1 && args[0] == "--self-test") { Application.Run(new BrowserHost(null, "self-test", 0)); return; }
        if (args.Length != 3 || !args[0].StartsWith("snowcloak-profile-", StringComparison.Ordinal) || !int.TryParse(args[2], out var parent)) return;
        Application.Run(new BrowserHost(args[0], args[1], parent));
    }
}

internal sealed partial class BrowserHost : Form
{
    private readonly string? _pipeName;
    private readonly string _session;
    private readonly int _parentProcess;
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1);
    private readonly System.Windows.Forms.Timer _lease = new() { Interval = 100 };
    private readonly Dictionary<string, List<(CoreWebView2WebResourceRequestedEventArgs Request, CoreWebView2Deferral Deferral)>> _requests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _allowedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _images = new(StringComparer.Ordinal);
    private long _imageBytes;
    private NamedPipeClientStream? _pipe;
    private DateTime _lastViewport;
    private CoreWebView2Environment? _environment;
    private JsonElement _state;
    private string? _browserDataDirectory;
    private bool _attachmentReported;
    private bool _attachmentFailureReported;
    private string? _presentationReason;
    private nint _ownerWindow;
    private bool _requestedVisible;
    private string _requestedHideReason = "closed";
    protected override bool ShowWithoutActivation => true;

    public BrowserHost(string? pipeName, string session, int parentProcess)
    {
        _pipeName = pipeName; _session = session; _parentProcess = parentProcess;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; Size = new(1000, 720); Opacity = 0;
        Controls.Add(_browser);
        Load += async (_, _) => await InitializeAsync();
        _lease.Tick += (_, _) =>
        {
            // Game frame callbacks may pause/throttle while focus moves to another window
            if (_pipeName != null && _attachmentReported) ApplyPresentation();
        };
        FormClosed += (_, _) =>
        {
            _lease.Stop(); _lease.Dispose(); _stop.Cancel();
            foreach (var hash in _requests.Keys.ToArray()) CompleteImage(hash, null);
            _images.Clear(); _imageBytes = 0;
            _pipe?.Dispose(); _browser.Dispose();
        };
    }

    private async Task InitializeAsync()
    {
        var stage = "pipe";
        try
        {
            if (_pipeName != null)
            {
                _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(10000, _stop.Token);
                if (!GetNamedPipeServerProcessId(_pipe.SafePipeHandle.DangerousGetHandle(), out var server) || server != _parentProcess) throw new InvalidDataException("Unexpected browser host owner.");
            }
            stage = "privileges";
            using (var identity = WindowsIdentity.GetCurrent())
                if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("The browser host must run without elevation.");
            stage = "runtimeOverrides";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER")))
                throw new InvalidOperationException("Browser runtime overrides are not permitted.");
            stage = "runtime";
            // Helpers have independent lifetimes and window environments. Sharing a browser
            // process/data folder caused controller creation to fail with ERROR_INVALID_STATE.
            _browserDataDirectory = Path.Combine(Path.GetTempPath(), "Snowcloak", "ProfileWebView", Guid.NewGuid().ToString("N"));
            _environment = await CoreWebView2Environment.CreateAsync(null, _browserDataDirectory);
            var browserDirectory = _browserDataDirectory;
            _environment.BrowserProcessExited += (_, _) =>
            {
                var cacheRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Snowcloak", "ProfileWebView"));
                var target = Path.GetFullPath(browserDirectory);
                if (!string.Equals(Path.GetDirectoryName(target), cacheRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(Path.GetFileName(target), "N", out _)) return;
                try { Directory.Delete(target, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            };
            stage = "controller";
            await _browser.EnsureCoreWebView2Async(_environment);
            stage = "settings";
            var web = _browser.CoreWebView2;
            var settings = web.Settings;
            settings.AreHostObjectsAllowed = false; settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false; settings.AreDevToolsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false; settings.IsStatusBarEnabled = false;
            settings.IsPasswordAutosaveEnabled = false; settings.IsGeneralAutofillEnabled = false;
            settings.IsZoomControlEnabled = false;
            web.NavigationStarting += (_, e) => e.Cancel = e.Uri != ProfileWebResourcePolicy.Page;
            web.FrameNavigationStarting += (_, e) => e.Cancel = true;
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.DownloadStarting += (_, e) => e.Cancel = true;
            web.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            web.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
            web.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            web.ProcessFailed += async (_, _) =>
            {
                await SendAsync(JsonSerializer.Serialize(new { type = "hostError", session = _session,
                    reason = "renderer", message = "The browser renderer stopped. Your native draft is retained." }));
                Close();
            };
            _browser.GotFocus += (_, _) => _ = SendAsync(JsonSerializer.Serialize(new { type = "focus", session = _session, focused = true }));
            _browser.LostFocus += (_, _) => _ = SendAsync(JsonSerializer.Serialize(new { type = "focus", session = _session, focused = false }));
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += ResourceRequested;
            web.WebMessageReceived += async (_, e) =>
            {
                if (e.Source != ProfileWebResourcePolicy.Page || Encoding.UTF8.GetByteCount(e.WebMessageAsJson) > ProfileWebEditPolicyLimit) return;
                if (_pipeName == null) return;
                await SendAsync(e.WebMessageAsJson);
            };
            web.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    await SendAsync(JsonSerializer.Serialize(new { type = "hostError", session = _session, reason = "navigation" }));
                    return;
                }
                if (_pipeName == null) await RunSelfTestAsync();
                else await SendAsync(JsonSerializer.Serialize(new { type = "ready", session = _session }));
            };
            web.Navigate(ProfileWebResourcePolicy.Page);
            if (_pipe != null) _ = ReadPipeAsync();
            _lease.Start();
        }
        catch (Exception error)
        {
            await SendAsync(JsonSerializer.Serialize(new { type = "hostError", session = _session, reason = stage, failureType = error.GetType().Name, failureCode = error.HResult }));
            if (_pipeName == null) WriteSelfTestResult("FAIL: WebView2 initialization at " + stage + ": " + error.GetType().Name + $" (0x{error.HResult:X8})");
            Close();
        }
    }

    private const int ProfileWebEditPolicyLimit = 65536;
    private void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var resource = ProfileWebResourcePolicy.Resource(e.Request.Uri);
        if (e.Request.Method != "GET" || resource == null) { Respond(e, null, "text/plain"); return; }
        var hash = ProfileWebResourcePolicy.ImageHash(e.Request.Uri);
        if (hash != null)
        {
            if (!_allowedImages.Contains(hash)) { Respond(e, null, "text/plain"); return; }
            if (_images.TryGetValue(hash, out var image)) { Respond(e, image, "image/png"); return; }
            var deferral = e.GetDeferral();
            if (!_requests.TryGetValue(hash, out var requests))
            {
                requests = []; _requests[hash] = requests;
                _ = SendAsync(JsonSerializer.Serialize(new { type = "assetRequest", session = _session,
                    ident = _state.GetProperty("ident").GetString(), revision = _state.GetProperty("revision").GetInt64(), hash }));
            }
            requests.Add((e, deferral)); return;
        }
        var path = Path.Combine(AppContext.BaseDirectory, "Web", resource);
        Respond(e, File.Exists(path) ? File.ReadAllBytes(path) : null,
            resource.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : resource.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/html");
    }

    private void Respond(CoreWebView2WebResourceRequestedEventArgs request, byte[]? bytes, string mime)
    {
        request.Response = _environment!.CreateWebResourceResponse(new MemoryStream(bytes ?? []), bytes == null ? 403 : 200,
            bytes == null ? "Forbidden" : "OK", "Content-Type: " + mime + "; charset=utf-8\r\nContent-Security-Policy: " + ProfileWebResourcePolicy.ContentSecurityPolicy +
            "\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nCache-Control: no-store");
    }

    private async Task ReadPipeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var bytes = await ProfileWebTransport.ReadAsync(_pipe!, 16 * 1024 * 1024, _stop.Token);
                if (bytes == null) break;
                using var json = JsonDocument.Parse(bytes);
                var message = json.RootElement.Clone();
                BeginInvoke(() => Receive(message));
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception) { }
        finally { if (!IsDisposed) BeginInvoke(Close); }
    }

    private void Receive(JsonElement message)
    {
        if (message.GetProperty("session").GetString() != _session) return;
        switch (message.GetProperty("type").GetString())
        {
            case "viewport":
                _lastViewport = DateTime.UtcNow;
                var parent = new nint(message.GetProperty("parent").GetInt64());
                GetWindowThreadProcessId(parent, out var pid);
                if (pid != _parentProcess) { AttachmentFailed(); return; }
                if (GetWindow(Handle, 4) != parent)
                {
                    // Keep our own top-level compositor surface. A foreign WS_CHILD does not
                    // become part of the game's DirectX/ImGui render target. Ownership keeps
                    // the browser above the game without global topmost or cross-process reparenting.
                    // Never thought I'd miss working with Vulkan. 
                    SetWindowLongPtr(Handle, -8, parent);
                    if (GetWindow(Handle, 4) != parent) { AttachmentFailed(); return; }
                }
                _ownerWindow = parent;
                var x = message.GetProperty("x").GetInt32();
                var y = message.GetProperty("y").GetInt32();
                var width = Math.Clamp(message.GetProperty("width").GetInt32(), 1, 4096);
                var height = Math.Clamp(message.GetProperty("height").GetInt32(), 1, 4096);
                if (!GetWindowRect(Handle, out var currentBounds) || currentBounds.Left != x || currentBounds.Top != y ||
                    currentBounds.Right - currentBounds.Left != width || currentBounds.Bottom - currentBounds.Top != height)
                    if (!SetWindowPos(Handle, 0, x, y, width, height, 0x0010 | 0x0004)) { AttachmentFailed(); return; }
                if (Opacity != 1) Opacity = 1;
                if (!ApplyOcclusion(message, width, height)) { Present(false, "occlusion"); return; }
                Present(message.GetProperty("visible").GetBoolean(), "dialog");
                if (!_attachmentReported)
                {
                    _attachmentReported = true;
                    _ = SendAsync(JsonSerializer.Serialize(new { type = "attached", session = _session }));
                }
                break;
            case "state":
                _state = message;
                _allowedImages.Clear();
                foreach (var image in message.GetProperty("imageHashes").EnumerateArray()) _allowedImages.Add(image.GetString()!);
                foreach (var removed in _images.Keys.Where(h => !_allowedImages.Contains(h)).ToArray()) { _imageBytes -= _images[removed].Length; _images.Remove(removed); }
                foreach (var removed in _requests.Keys.Where(h => !_allowedImages.Contains(h)).ToArray()) CompleteImage(removed, null);
                _browser.CoreWebView2.PostWebMessageAsJson(message.GetRawText()); break;
            case "asset":
                var hash = message.GetProperty("hash").GetString()!;
                byte[]? data = message.GetProperty("bytes").ValueKind == JsonValueKind.Null ? null : message.GetProperty("bytes").GetBytesFromBase64();
                CompleteImage(hash, _allowedImages.Contains(hash) ? data : null); break;
            case "flush":
            case "error": _browser.CoreWebView2.PostWebMessageAsJson(message.GetRawText()); break;
            case "hide": Present(false, "closed"); break;
            case "close": Close(); break;
        }
    }

    private void Present(bool visible, string reason)
    {
        _requestedVisible = visible;
        _requestedHideReason = reason;
        ApplyPresentation();
    }

    private void ApplyPresentation()
    {
        var visible = _requestedVisible;
        var reason = _requestedHideReason;
        var foreground = GetAncestor(GetForegroundWindow(), 2);
        GetWindowThreadProcessId(foreground, out var foregroundProcess);
        var browserFocused = foreground == Handle;
        if (visible && _ownerWindow != 0 && (!IsWindowVisible(_ownerWindow) || IsIconic(_ownerWindow)))
        { visible = false; reason = "owner"; }
        if (visible && !browserFocused && foregroundProcess != _parentProcess)
        { visible = false; reason = "focus"; }
        if (visible && !browserFocused && DateTime.UtcNow - _lastViewport > TimeSpan.FromSeconds(2))
        { visible = false; reason = "frames"; }
        if (visible && !Visible) Show(); else if (!visible && Visible) Hide();
        var state = visible ? "visible" : reason;
        if (_presentationReason == state) return;
        _presentationReason = state;
        _ = SendAsync(JsonSerializer.Serialize(new { type = "presentation", session = _session,
            visible = IsWindowVisible(Handle), reason = state }));
    }

    private void AttachmentFailed()
    {
        _requestedVisible = false;
        Hide();
        if (_attachmentFailureReported) return;
        _attachmentFailureReported = true;
        _ = SendAsync(JsonSerializer.Serialize(new { type = "hostError", session = _session, reason = "attachment" }));
    }

    private void CompleteImage(string hash, byte[]? bytes)
    {
        if (!_requests.Remove(hash, out var requests)) return;
        if (bytes is { Length: > 0 and <= 8 * 1024 * 1024 })
        {
            while (_imageBytes + bytes.Length > 64 * 1024 * 1024 && _images.Count > 0)
            { var oldest = _images.Keys.First(); _imageBytes -= _images[oldest].Length; _images.Remove(oldest); }
            if (_images.Remove(hash, out var previous)) _imageBytes -= previous.Length;
            _images[hash] = bytes; _imageBytes += bytes.Length;
        }
        else bytes = null;
        foreach (var request in requests) { Respond(request.Request, bytes, "image/png"); request.Deferral.Complete(); }
    }

    private async Task SendAsync(string json)
    {
        if (_pipe == null || !_pipe.IsConnected) return;
        try
        {
            await _write.WaitAsync(_stop.Token);
            try { await ProfileWebTransport.WriteAsync(_pipe, Encoding.UTF8.GetBytes(json), _stop.Token); }
            finally { _write.Release(); }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (IOException) { }
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    private static nint SetWindowLongPtr(nint window, int index, nint value) => SetWindowLongPtrW(window, index, value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint process);
}
