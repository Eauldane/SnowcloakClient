using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Snowcloak.Core.Profiles;

namespace Snowcloak.ProfileWebHost;

/// <summary>Disposable, invisible owner window exercises the production cross-process host path.</summary>
internal sealed class EmbeddingSelfTest : Form
{
    public EmbeddingSelfTest()
    {
        Opacity = 0; ShowInTaskbar = false; Size = new(1000, 800);
        Load += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        Process? child = null;
        var stage = "startup";
        var resultPath = Environment.GetEnvironmentVariable("SNOWCLOAK_WEB_SELFTEST_RESULT");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var session = Guid.NewGuid().ToString("N"); var name = "snowcloak-profile-" + session;
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Snowcloak.ProfileWebHost.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(name); start.ArgumentList.Add(session); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            child = Process.Start(start) ?? throw new InvalidOperationException();
            await pipe.WaitForConnectionAsync(timeout.Token);
            stage = "page readiness";
            using (await ReadUntilAsync(pipe, "ready", timeout.Token)) { }
            var point = PointToScreen(new Point(30, 40));
            var viewport = new { type = "viewport", session, parent = (long)Handle, x = point.X, y = point.Y, width = 640, height = 400, visible = true };
            await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(viewport), timeout.Token);
            stage = "attachment acknowledgement";
            using (await ReadUntilAsync(pipe, "attached", timeout.Token)) { }
            await Task.Delay(150, timeout.Token);
            nint embedded = 0;
            stage = "owned window bounds";
            EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var pid); if (pid == child.Id && GetWindow(window, 4) == Handle) embedded = window; return true; }, 0);
            if (embedded == 0 || !GetWindowRect(embedded, out var bounds) || bounds.Right - bounds.Left != 640 || bounds.Bottom - bounds.Top != 400)
                throw new InvalidDataException("Owned window bounds failed.");
            // Do not steal foreground from the user to run this test. A background owner is
            // deliberately hidden by the production policy; foreground changes may occur here.
            if (OwnerOrBrowserIsForeground(embedded) && !IsWindowVisible(embedded))
                throw new InvalidDataException("Foreground browser was hidden.");
            if (((long)GetWindowLongPtrW(embedded, -16) & 0x40000000L) != 0)
                throw new InvalidDataException("Browser is still a child window.");
            if (((long)GetWindowLongPtrW(embedded, -20) & 0x8L) != 0)
                throw new InvalidDataException("Browser is globally topmost.");
            if (bounds.Left != point.X || bounds.Top != point.Y)
                throw new InvalidDataException("Owned window screen position failed.");
            stage = "throttled game frames";
            await Task.Delay(850, timeout.Token);
            if (OwnerOrBrowserIsForeground(embedded) && !IsWindowVisible(embedded))
                throw new InvalidDataException("A short frame pause hid the foreground designer.");
            stage = "ImGui overlap region";
            await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { viewport.type, viewport.session, viewport.parent,
                viewport.x, viewport.y, viewport.width, viewport.height, viewport.visible,
                regions = new[] { new { x = 100, y = 50, width = 120, height = 90 } } }), timeout.Token);
            await Task.Delay(100, timeout.Token);
            var region = CreateRectRgn(0,0,0,0);
            try
            {
                if (GetWindowRgn(embedded, region) == 0 || PtInRegion(region,110,70) || !PtInRegion(region,10,10))
                    throw new InvalidDataException("The front ImGui rectangle was not excluded from the browser region.");
                await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(viewport), timeout.Token);
                await Task.Delay(100, timeout.Token);
                if (GetWindowRgn(embedded, region) != 0) throw new InvalidDataException("The browser region did not restore after overlap cleared.");
            }
            finally { DeleteObject(region); }
            // Verify bundled JS is reachable over the production pipe, including publication flush.
            using var state = JsonDocument.Parse("""
                {"type":"state","ident":"test","revision":1,"preview":false,"busy":false,"comparison":{},"comparisonAvailable":true,"catalogue":[],"imageHashes":[],"document":{"characterName":"Test","title":"","pronouns":"","tagline":"","hooks":[],"builder":{"theme":{"background":"#181b22ff","foreground":"#ffffffff","accent":"#8dafddff","backgroundHash":null},"layout":[],"fields":[],"preferences":[],"assets":[]}}}
                """);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(state.RootElement.GetRawText())!;
            data["session"] = JsonSerializer.SerializeToElement(session);
            await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(data), timeout.Token);
            data["busy"] = JsonSerializer.SerializeToElement(true);
            await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(data), timeout.Token);
            await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { type = "flush", session, ident = "test", revision = 1, requestId = "busy-test" }), timeout.Token);
            stage = "busy flush cancellation";
            using (var cancelled = await ReadUntilAsync(pipe, "flushCancelled", timeout.Token))
                if (cancelled.RootElement.GetProperty("reason").GetString() != "busy" || cancelled.RootElement.GetProperty("requestId").GetString() != "busy-test")
                    throw new InvalidDataException("Busy flush did not cancel with its request identity.");
            data["busy"] = JsonSerializer.SerializeToElement(false);
            await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(data), timeout.Token);
            await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { type = "flush", session, ident = "test", revision = 1, requestId = "switch-test" }), timeout.Token);
            stage = "publication flush";
            using (var completed = await ReadUntilAsync(pipe, "flushComplete", timeout.Token))
                if (completed.RootElement.GetProperty("requestId").GetString() != "switch-test") throw new InvalidDataException("Flush completion did not match its request.");
            await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(viewport with { parent = 0L }), timeout.Token);
            stage = "invalid-parent failure";
            using (var rejected = await ReadUntilAsync(pipe, "hostError", timeout.Token))
                if (rejected.RootElement.GetProperty("reason").GetString() != "attachment") throw new InvalidDataException("Invalid parent was not reported.");
            await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { type = "hide", session }), timeout.Token);
            stage = "hide and close";
            await Task.Delay(100, timeout.Token);
            if (IsWindowVisible(embedded)) throw new InvalidDataException("Child window did not hide.");
            await ProfileWebTransport.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { type = "close", session }), timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            if (resultPath != null) File.WriteAllText(resultPath, "PASS: production helper process/pipe, 640x400 owned compositor window (no WS_CHILD/topmost), screen bounds, focus-aware visibility, UI flush, invalid-owner failure, hide/close");
        }
        catch (Exception e) { if (resultPath != null) File.WriteAllText(resultPath, "FAIL: embedding self-test at " + stage + ": " + e.GetType().Name + (e is HostTestFailure ? " (" + e.Message + ")" : "")); }
        finally
        {
            try { if (child is { HasExited: false }) child.Kill(); } catch (InvalidOperationException) { }
            child?.Dispose(); Close();
        }
    }

    private static async Task<JsonDocument> ReadUntilAsync(Stream pipe, string type, CancellationToken token)
    {
        while (true)
        {
            var message = JsonDocument.Parse(await ProfileWebTransport.ReadAsync(pipe, 65536, token) ?? throw new EndOfStreamException());
            if (message.RootElement.GetProperty("type").GetString() == type) return message;
            if (message.RootElement.GetProperty("type").GetString() == "hostError")
            {
                var failure = message.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : "unknown";
                var error = message.RootElement.TryGetProperty("failureType", out var failureType) ? failureType.GetString() : "unknown";
                var code = message.RootElement.TryGetProperty("failureCode", out var failureCode) ? $"0x{failureCode.GetInt32():X8}" : "unknown";
                message.Dispose(); throw new HostTestFailure(failure + ": " + error + ": " + code);
            }
            message.Dispose();
        }
    }

    private sealed class HostTestFailure(string message) : Exception(message);

    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);

    private static bool OwnerOrBrowserIsForeground(nint browser)
    {
        var root = GetAncestor(GetForegroundWindow(), 2);
        GetWindowThreadProcessId(root, out var process);
        return root == browser || process == Environment.ProcessId;
    }

    private delegate bool WindowCallback(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
