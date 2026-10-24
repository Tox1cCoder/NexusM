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
                passed = Data.Str(result, "status") == "complete" && Data.Bool(result, "passed");
                var checks = Data.List(Data.Get(result, "checks"));
                checks.Insert(0, Data.Obj("test", "UI readiness precedes saved-library and account work", "passed", true));
                File.WriteAllText(report, Data.Json.Serialize(Data.Obj("passed", passed, "checks", checks)));
                await browser.ExecuteScriptAsync("document.querySelector('.toasts').replaceChildren(); window.NexusM.navigate('settings')");
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
    }
}
