using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NexusM
{
    // Real app UI with an isolated browser profile, no account, and no host mutations.
    internal sealed class UiTests : Form
    {
        private readonly WebView2 browser = new WebView2();
        private readonly string workspace, report, root;

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new UiTests(Path.GetFullPath(args[0]), Path.GetFullPath(args[1])));
        }

        private UiTests(string project, string output)
        {
            workspace = project; report = output;
            root = Path.Combine(Path.GetTempPath(), "NexusM-UiTests-" + Guid.NewGuid().ToString("N"));
            Width = 1340; Height = 880; ShowInTaskbar = false; Opacity = 0;
            browser.Dock = DockStyle.Fill; Controls.Add(browser);
            Shown += async delegate { await Run(); };
        }

        private async Task Run()
        {
            bool passed = false;
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "profile"));
                await browser.EnsureCoreWebView2Async(environment);
                browser.CoreWebView2.SetVirtualHostNameToFolderMapping("nexusm.local", Path.Combine(workspace, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
                browser.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                browser.CoreWebView2.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
                {
                    Uri uri;
                    if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "nexusm.local")
                        e.Response = environment.CreateWebResourceResponse(new MemoryStream(new byte[0]), 403, "Fixture network blocked", "Content-Type: text/plain");
                };
                var interfaceReady = new TaskCompletionSource<bool>();
                bool stateRequestedBeforeReady = false;
                browser.CoreWebView2.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e)
                {
                    var request = Data.Map(Data.Json.DeserializeObject(e.WebMessageAsJson));
                    if (Data.Str(request, "action") == "uiReady") { interfaceReady.TrySetResult(true); return; }
                    if (Data.Str(request, "action") == "uiStartupError") { interfaceReady.TrySetResult(false); return; }
                    if (Data.Str(request, "action") == "getState" && !interfaceReady.Task.IsCompleted) stateRequestedBeforeReady = true;
                    object response = Data.Str(request, "action") == "getState"
                        ? Data.Obj("id", Data.Get(request, "id"), "ok", true, "result", Data.Obj("games", new object[0], "mods", new object[0], "downloads", new object[0], "user", null, "fileDownloads", Data.Obj(), "settings", Data.Obj("hasApiKey", false, "downloadFolder", "C:\\Downloads\\NexusM", "yourGamesOnly", true), "clearingDownloadFolder", false))
                        : Data.Obj("id", Data.Get(request, "id"), "ok", false, "error", "Host mutations are blocked by the offline UI harness.");
                    browser.CoreWebView2.PostWebMessageAsJson(Data.Json.Serialize(response));
                };
                var ready = new TaskCompletionSource<bool>();
                browser.CoreWebView2.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) { ready.TrySetResult(e.IsSuccess); };
                browser.CoreWebView2.Navigate("https://nexusm.local/index.html");
                if (await Task.WhenAny(ready.Task, Task.Delay(15000)) != ready.Task || !ready.Task.Result) throw new IOException("The app UI did not load.");
                if (await Task.WhenAny(interfaceReady.Task, Task.Delay(5000)) != interfaceReady.Task || !interfaceReady.Task.Result || stateRequestedBeforeReady)
                    throw new IOException("The app UI did not report a successful initial render before requesting its library.");
                await browser.ExecuteScriptAsync(File.ReadAllText(Path.Combine(workspace, "tests", "ui-checks.js")));
                object result = null;
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    await Task.Delay(100);
                    result = Data.Json.DeserializeObject(await browser.ExecuteScriptAsync("window.__uiChecks || {}"));
                    if (Data.Str(result, "status") == "complete") break;
                }
                var checks = Data.List(Data.Get(result, "checks"));
                checks.Insert(0, Data.Obj("test", "UI readiness precedes saved-library and account work", "passed", true));
                await browser.ExecuteScriptAsync("document.querySelector('.toasts').replaceChildren()");
                foreach (int width in new[] { 980, 1060, 1070, 1100, 1120, 1340, 1370, 1380, 1600 })
                {
                    ClientSize = new System.Drawing.Size(width, 880);
                    await Task.Delay(100);
                    checks.Add(Data.Json.DeserializeObject(await browser.ExecuteScriptAsync(File.ReadAllText(Path.Combine(workspace, "tests", "library-layout-checks.js")))));
                    if (width == 1100)
                    {
                        await browser.ExecuteScriptAsync("document.querySelector('.mods-section').scrollIntoView({block:'start'})");
                        using (var image = File.Create(Path.Combine(workspace, "artifacts", "library-layout.png")))
                            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                        await browser.ExecuteScriptAsync("window.scrollTo(0,0)");
                    }
                }
                foreach (double zoom in new[] { 1.25, 1.5, 2.0 })
                {
                    browser.ZoomFactor = zoom;
                    ClientSize = new System.Drawing.Size(980, 880);
                    await Task.Delay(100);
                    checks.Add(Data.Json.DeserializeObject(await browser.ExecuteScriptAsync(File.ReadAllText(Path.Combine(workspace, "tests", "library-layout-checks.js")))));
                }
                await CheckAppLayout(checks, 980, 700, 1);
                await CheckAppLayout(checks, 1060, 500, 1);
                await CheckAppLayout(checks, 1340, 880, 1);
                await CheckAppLayout(checks, 1600, 900, 1);
                await CheckAppLayout(checks, 980, 700, 1.25);
                await CheckAppLayout(checks, 980, 700, 1.5);
                await CheckAppLayout(checks, 980, 700, 2);
                await CheckAppLayout(checks, 640, 700, 2);
                browser.ZoomFactor = 1;
                ClientSize = new System.Drawing.Size(1340, 880);
                passed = Data.Str(result, "status") == "complete" && Data.Bool(result, "passed");
                foreach (object check in checks) if (!Data.Bool(check, "passed")) passed = false;
                File.WriteAllText(report, Data.Json.Serialize(Data.Obj("passed", passed, "checks", checks)));
                await browser.ExecuteScriptAsync("document.querySelector('.toasts').replaceChildren(); window.NexusM.navigate('settings')");
                await Task.Delay(200);
                using (var image = File.Create(Path.ChangeExtension(report, ".png")))
                    await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            catch (Exception error) { passed = false; File.WriteAllText(report, Data.Json.Serialize(Data.Obj("passed", false, "error", error.ToString()))); }
            finally
            {
                browser.Dispose(); Environment.ExitCode = passed ? 0 : 1; Close();
                string absolute = Path.GetFullPath(root);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\NexusM-UiTests-";
                if (!absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe UI fixture cleanup path.");
                try { if (Directory.Exists(absolute)) Directory.Delete(absolute, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private async Task CheckAppLayout(List<object> checks, int width, int height, double zoom)
        {
            browser.ZoomFactor = zoom;
            ClientSize = new System.Drawing.Size(width, height);
            await Task.Delay(100);
            bool capture = (width == 1340 && zoom == 1) || ((width == 980 || width == 640) && zoom == 2);
            await browser.ExecuteScriptAsync("window.__captureAppLayout = " + (capture ? "true" : "false"));
            await browser.ExecuteScriptAsync(File.ReadAllText(Path.Combine(workspace, "tests", "app-layout-checks.js")));
            object result = null;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(20);
                result = Data.Json.DeserializeObject(await browser.ExecuteScriptAsync("window.__appLayoutChecks || {}"));
                string scene = Data.Str(result, "capture");
                if (capture && !String.IsNullOrEmpty(scene))
                {
                    string name = String.Format("app-layout-{0}-{1}-{2}.png", width, (int)(zoom * 100), scene);
                    using (var image = File.Create(Path.Combine(workspace, "artifacts", name)))
                        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                    await browser.ExecuteScriptAsync("window.__resumeAppLayout && window.__resumeAppLayout()");
                }
                if (Data.Str(result, "status") == "complete") break;
            }
            if (Data.Str(result, "status") != "complete") throw new IOException("App layout fixtures did not complete.");
            checks.AddRange(Data.List(Data.Get(result, "checks")));
        }
    }
}
