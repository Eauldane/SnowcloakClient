using System.Text.Json;

namespace Snowcloak.ProfileWebHost;

internal sealed partial class BrowserHost
{
    private static void WriteSelfTestResult(string text)
    {
        var path = Environment.GetEnvironmentVariable("SNOWCLOAK_WEB_SELFTEST_RESULT");
        if (!string.IsNullOrWhiteSpace(path)) File.WriteAllText(path, text);
    }

    private async Task RunSelfTestAsync()
    {
        try
        {
            var test = """
            (()=>{
              window.__profileInjected = false;
              const element={id:'test',kind:4,title:'',text:'<img src="https://invalid.example/test" onerror="window.__profileInjected=true"><script>window.__profileInjected=true</script><svg onload=window.__profileInjected=true></svg><iframe srcdoc=attack></iframe><a href=javascript:alert(1)>link</a>[b]Safe bold[/b]',audience:1,children:[],references:[],background:'#00000000',foreground:'#ffffffff',padding:8,spacing:8,rounding:4,border:0,fontScale:1,font:0,useThemeForeground:true};
              const state={type:'state',session:'self-test',ident:'test',revision:1,preview:false,busy:false,comparison:{},comparisonAvailable:true,catalogue:[],document:{characterName:'Test',title:'',pronouns:'',tagline:'',hooks:[],builder:{theme:{background:'#181b22ff',foreground:'#ffffffff',accent:'#8dafddff',backgroundHash:null},layout:[element],fields:[],preferences:[],assets:[]}}};
              window.dispatchEvent(new MessageEvent('message',{data:state}));
              return true;
            })()
            """;
            // The app receives data through WebView2's event, not window.message. A fixed self-test helper
            // delivers the fixture through the same production PostWebMessageAsJson route below.
            using var fixture = JsonDocument.Parse("""
            {"type":"state","session":"self-test","ident":"test","revision":1,"preview":false,"busy":false,"comparison":{},"comparisonAvailable":true,"catalogue":[],"imageHashes":[],"document":{"characterName":"Test","title":"","pronouns":"","tagline":"","hooks":[],"builder":{"theme":{"background":"#181b22ff","foreground":"#ffffffff","accent":"#8dafddff","backgroundHash":null},"layout":[{"id":"test","kind":4,"title":"","text":"<img src=\"https://invalid.example/test\" onerror=\"window.__profileInjected=true\"><script>window.__profileInjected=true</script><svg onload=window.__profileInjected=true></svg><iframe srcdoc=attack></iframe><a href=javascript:alert(1)>link</a>[b]Safe bold[/b]","audience":1,"children":[],"references":[],"background":"#00000000","foreground":"#ffffffff","padding":8,"spacing":8,"rounding":4,"border":0,"fontScale":1,"font":0,"useThemeForeground":true}],"fields":[],"preferences":[],"assets":[]}}}
            """);
            _browser.CoreWebView2.PostWebMessageAsJson(fixture.RootElement.GetRawText());
            await Task.Delay(250);
            var checks = await _browser.CoreWebView2.ExecuteScriptAsync("""
            (()=>({literal:document.getElementById('page').textContent.includes('<script>'),
                    bold:document.querySelector('#page strong')?.textContent==='Safe bold',
                    injected:!!window.__profileInjected,
                    remoteImages:document.querySelectorAll('#page img').length,
                    executableNodes:document.querySelectorAll('#page script,#page iframe,#page svg,#page [onerror],#page [onload],#page a[href^="javascript:"]').length,
                    toolbar:document.getElementById('add')!==null}))()
            """);
            using var result = JsonDocument.Parse(checks);
            var root = result.RootElement;
            if (!root.GetProperty("literal").GetBoolean() || !root.GetProperty("bold").GetBoolean() || root.GetProperty("injected").GetBoolean() || root.GetProperty("remoteImages").GetInt32() != 0 || root.GetProperty("executableNodes").GetInt32() != 0 || !root.GetProperty("toolbar").GetBoolean())
                throw new InvalidDataException("Browser content boundary failed.");
            var navigationFixture = System.Text.Json.Nodes.JsonNode.Parse(fixture.RootElement.GetRawText())!;
            navigationFixture["preview"] = true;
            navigationFixture["mode"] = "view";
            var builder = navigationFixture["document"]!["builder"]!;
            builder["version"] = 2;
            builder["startPageId"] = "home";
            builder["pages"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["id"] = "home", ["title"] = "Home", ["audience"] = 0, ["layout"] = builder["layout"]!.DeepClone() },
                new System.Text.Json.Nodes.JsonObject { ["id"] = "more", ["title"] = "More", ["audience"] = 0, ["layout"] = builder["layout"]!.DeepClone() });
            builder["theme"]!["navigation"] = 0;
            webState(navigationFixture);
            await Task.Delay(250);
            if (await _browser.CoreWebView2.ExecuteScriptAsync("!document.getElementById('profile-pages').hidden && document.querySelectorAll('#profile-pages button').length===2") != "true")
                throw new InvalidDataException("Page navbar failed.");
            builder["theme"]!["navigation"] = 3;
            webState(navigationFixture);
            await Task.Delay(250);
            if (await _browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('profile-pages').hidden && document.getElementById('page').textContent.includes('<script>') && document.querySelectorAll('#page script,#page iframe,#page svg,#page img').length===0 && !window.__profileInjected") != "true")
                throw new InvalidDataException("Hidden navbar or viewer content boundary failed.");
            void webState(System.Text.Json.Nodes.JsonNode value) => _browser.CoreWebView2.PostWebMessageAsJson(value.ToJsonString());
            using var pixels = new MemoryStream();
            await _browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, pixels);
            pixels.Position = 0;
            using var bitmap = new Bitmap(pixels);
            var colours = new HashSet<int>();
            for (var y = 0; y < bitmap.Height; y += 20)
                for (var x = 0; x < bitmap.Width; x += 20) colours.Add(bitmap.GetPixel(x, y).ToArgb());
            if (bitmap.Width < 640 || bitmap.Height < 400 || colours.Count < 4)
                throw new InvalidDataException("Browser produced blank pixels.");
            var web = _browser.CoreWebView2;
            if (web.Settings.AreHostObjectsAllowed || web.Settings.AreDefaultScriptDialogsEnabled || web.Settings.AreDevToolsEnabled) throw new InvalidDataException("Browser host restrictions failed.");
            var before = web.Source;
            web.Navigate("https://example.com"); await Task.Delay(100);
            if (web.Source != before) throw new InvalidDataException("Navigation boundary failed.");
            WriteSelfTestResult("PASS: actual WebView2 UI/nonblank pixels, optional page navbar, literal hostile markup in editor/viewer, predefined rich text, disabled host objects/dialogs/devtools, blocked navigation");
        }
        catch (Exception) { WriteSelfTestResult("FAIL: browser self-test"); }
        finally { Close(); }
    }
}
