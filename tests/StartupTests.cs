using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NexusM
{
    internal static class StartupTests
    {
        private static string root, report;
        private static readonly List<object> checks = new List<object>();
        private const string ReadyPage = "<!doctype html><title>Local startup fixture</title><body><p>Fixture ready</p><script>window.NexusM={ready:true};chrome.webview.postMessage({action:'uiReady'});</script>";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            root = Path.Combine(Path.GetTempPath(), "NexusM-StartupTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            report = Path.GetFullPath(args[0]);
            using (var host = new Form { Opacity = 0, ShowInTaskbar = false, Size = new Size(1, 1) }) {
                host.Shown += async delegate {
                    try {
                        await Check("startup deadlines and stale view generations cannot report a failed window as ready", StateBoundaries);
                        await Check("missing app files expose native retry and recover without replacing the controller or library", MissingFiles);
                        await Check("script startup failures expose native recovery and diagnostics exclude page-supplied secrets", ScriptFailure);
                        await Check("a stalled app document exposes native timeout recovery and ignores late readiness", StartupTimeout);
                        await Check("failed local navigation exposes recovery and a retry opens the app again", NavigationFailure);
                        await Check("a crashed renderer recovers once, preserves transfers, and offers retry after repeated failure", RendererFailure);
                        await Check("responses from a replaced view cannot resolve requests in the new interface", ReplacedViewResponse);
                    } catch (Exception error) { checks.Add(Data.Obj("name", "Fixture runner", "passed", false, "error", error.ToString())); }
                    finally { host.Close(); }
                };
                Application.Run(host);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            File.WriteAllText(report, Data.Json.Serialize(Data.Obj("passed", checks.All(x => Data.Bool(x, "passed")), "checks", checks)), new UTF8Encoding(false));
            string absolute = Path.GetFullPath(root), prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\NexusM-StartupTests-";
            if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe startup fixture cleanup path.");
            for (int attempt = 0; attempt < 8; attempt++) {
                try { if (Directory.Exists(absolute)) Directory.Delete(absolute, true); break; }
                catch (IOException) { Thread.Sleep(150); } catch (UnauthorizedAccessException) { Thread.Sleep(150); }
            }
            return checks.All(x => Data.Bool(x, "passed")) ? 0 : 1;
        }
        private static async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(Data.Obj("name", name, "passed", true)); }
            catch (Exception error) { checks.Add(Data.Obj("name", name, "passed", false, "error", error.ToString())); }
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            File.WriteAllText(report, Data.Json.Serialize(Data.Obj("running", true, "checks", checks)), new UTF8Encoding(false));
        }
        private static void Expect(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
        private static async Task Until(Func<bool> complete, int milliseconds = 12000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < deadline) { if (complete()) return; await Task.Delay(25); }
            throw new TimeoutException("The startup fixture did not reach the expected state.");
        }
        private sealed class Fixture : IDisposable
        {
            internal readonly Storage Store;
            internal readonly MainForm Form;
            internal readonly string Page;
            private readonly List<CancellationTokenSource> transfers = new List<CancellationTokenSource>();
            internal Fixture(string name, string html)
            {
                string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
                string ui = Path.Combine(folder, "ui"); Directory.CreateDirectory(ui); Page = Path.Combine(ui, "index.html");
                if (html != null) File.WriteAllText(Page, html);
                Store = new Storage(Path.Combine(folder, "state"));
                Store.Mods.Add(Data.Obj("gameDomain", "fixturegame", "modId", 1, "name", "Saved fixture mod", "lastDownloadedAt", 12345));
                Form = new MainForm(Store, null, null, "startup-fixture-" + name, ui, true) { Opacity = 0, ShowInTaskbar = false };
                Expect(Form.Controller.Api == null, "The fixture loaded a personal account key.");
            }
            internal void Show() { Form.Show(); }
            internal CancellationTokenSource AddTransfer()
            {
                var cancel = new CancellationTokenSource(); transfers.Add(cancel);
                var active = (Dictionary<string, CancellationTokenSource>)typeof(Controller).GetField("transfers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Form.Controller);
                active["fixturegame:1:2"] = cancel; return cancel;
            }
            public void Dispose() { Form.Close(); Form.Dispose(); Form.Controller.Dispose(); foreach (var cancel in transfers) cancel.Dispose(); }
        }
        private static Task StateBoundaries()
        {
            var startup = new BrowserStartup(); var now = DateTime.UtcNow; int previous = startup.Begin(now);
            Expect(!startup.TimedOut(now.AddSeconds(19), TimeSpan.FromSeconds(20)) && startup.TimedOut(now.AddSeconds(20), TimeSpan.FromSeconds(20)), "The startup deadline did not use its full allotted time.");
            startup.Fail(previous, "timeout"); Expect(!startup.Complete(previous), "A timed-out attempt became ready without a retry.");
            int current = startup.Begin(now.AddSeconds(21)); Expect(!startup.Complete(previous) && startup.Complete(current), "A previous view's readiness was accepted.");
            return Task.FromResult(0);
        }
        private static async Task MissingFiles()
        {
            using (var fixture = new Fixture("missing-files", null)) {
                var controller = fixture.Form.Controller; fixture.Show();
                await Until(delegate { return fixture.Form.StartupFailure == "ui-files-missing"; });
                Expect(fixture.Form.StartupFallbackVisible && !fixture.Form.InterfaceReady, "Missing files left an empty window rather than native retry.");
                File.WriteAllText(fixture.Page, ReadyPage); await fixture.Form.RetryInterface();
                await Until(delegate { return fixture.Form.InterfaceReady; });
                Expect(!fixture.Form.StartupFallbackVisible && ReferenceEquals(controller, fixture.Form.Controller) && fixture.Store.Mods.Count == 1 && Data.Num(fixture.Store.Mods[0], "lastDownloadedAt") == 12345, "Retry replaced the controller or lost the saved library.");
            }
        }
        private static async Task ScriptFailure()
        {
            const string secret = "fixture-sensitive-value-do-not-log";
            using (var fixture = new Fixture("script-failure", "<!doctype html><script>chrome.webview.postMessage({action:'uiStartupError',payload:{message:'" + secret + "'}});throw new Error('" + secret + "');</script>")) {
                fixture.Show(); await Until(delegate { return !String.IsNullOrEmpty(fixture.Form.StartupFailure); });
                Expect(fixture.Form.StartupFallbackVisible, "A failed script did not expose native recovery.");
                string log = File.ReadAllText(Path.Combine(fixture.Store.Root, "startup.log"));
                Expect(log.Contains("ui-startup-error") && !log.Contains(secret) && !log.Contains(fixture.Page), "Startup diagnostics retained page-supplied values or local paths.");
                for (int i = 0; i < 500; i++) StartupDiagnostics.Record(fixture.Store.Root, "fixture-failure");
                Expect(new FileInfo(Path.Combine(fixture.Store.Root, "startup.log")).Length < 33000, "The startup diagnostic log grew without a bound.");
                File.WriteAllText(fixture.Page, ReadyPage); await fixture.Form.RetryInterface(); await Until(delegate { return fixture.Form.InterfaceReady; });
            }
        }
        private static async Task StartupTimeout()
        {
            using (var fixture = new Fixture("startup-timeout", ReadyPage)) {
                fixture.Show(); await Until(delegate { return fixture.Form.InterfaceReady; });
                var core = fixture.Form.InterfaceBrowser.CoreWebView2;
                var navigating = new TaskCompletionSource<bool>();
                core.NavigationStarting += delegate { navigating.TrySetResult(true); };
                // Keep this renderer busy independently from the native UI thread.
                // The finite loop also bounds cleanup if a fixture assertion fails.
                File.WriteAllText(fixture.Page, "<!doctype html><script>var end=Date.now()+2500;while(Date.now()<end){};window.NexusM={ready:true};chrome.webview.postMessage({action:'uiReady'});</script>");
                core.Navigate("https://nexusm.local/index.html?stalled-script");
                await Until(delegate { return navigating.Task.IsCompleted; }); await Task.Delay(100);
                fixture.Form.CheckStartupTimeout(DateTime.UtcNow.AddSeconds(21));
                Expect(fixture.Form.StartupFailure == "startup-timeout" && fixture.Form.StartupFallbackVisible, "A stalled document left no native timeout recovery.");
                await Task.Delay(100);
                Expect(fixture.Form.StartupFailure == "startup-timeout" && !fixture.Form.InterfaceReady, "Late readiness hid the failed attempt's native recovery.");
                File.WriteAllText(fixture.Page, ReadyPage); await fixture.Form.RetryInterface(); await Until(delegate { return fixture.Form.InterfaceReady; });
            }
        }
        private static async Task NavigationFailure()
        {
            using (var fixture = new Fixture("navigation-failure", ReadyPage)) {
                fixture.Show(); await Until(delegate { return fixture.Form.InterfaceReady; });
                fixture.Form.InterfaceBrowser.CoreWebView2.Navigate("https://nexusm.local/missing-fixture.html");
                await Until(delegate { return !String.IsNullOrEmpty(fixture.Form.StartupFailure); });
                Expect(fixture.Form.StartupFallbackVisible && !fixture.Form.InterfaceReady, "Failed navigation left a blank browser in front of native recovery.");
                await fixture.Form.RetryInterface(); await Until(delegate { return fixture.Form.InterfaceReady; });
            }
        }
        private static async Task Crash(MainForm form)
        {
            try {
                var crash = form.InterfaceBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.crash", "{}");
                if (await Task.WhenAny(crash, Task.Delay(1500)) == crash) await crash;
            }
            catch (Exception) { /* A target crash rejects its pending DevTools call. */ }
        }
        private static async Task RendererFailure()
        {
            using (var fixture = new Fixture("renderer-failure", ReadyPage)) {
                fixture.Show(); await Until(delegate { return fixture.Form.InterfaceReady; });
                var controller = fixture.Form.Controller; var active = fixture.AddTransfer(); var first = fixture.Form.InterfaceBrowser;
                await Crash(fixture.Form);
                await Until(delegate { return !ReferenceEquals(first, fixture.Form.InterfaceBrowser) && fixture.Form.InterfaceReady; });
                Expect(ReferenceEquals(controller, fixture.Form.Controller) && !active.IsCancellationRequested && controller.HasActiveTransfers, "Renderer recovery interrupted a transfer or replaced the controller.");
                var second = fixture.Form.InterfaceBrowser; await Crash(fixture.Form);
                await Until(delegate { return !String.IsNullOrEmpty(fixture.Form.StartupFailure); });
                await Task.Delay(250);
                Expect(ReferenceEquals(second, fixture.Form.InterfaceBrowser) && fixture.Form.StartupFallbackVisible, "Repeated renderer failure caused an unbounded recovery loop.");
                await fixture.Form.RetryInterface(); await Until(delegate { return fixture.Form.InterfaceReady; });
                Expect(!active.IsCancellationRequested && controller.HasActiveTransfers, "Manual recovery cancelled an active transfer.");
            }
        }
        private sealed class DelayedValidation : HttpMessageHandler
        {
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(), Release = new TaskCompletionSource<bool>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Entered.TrySetResult(true); await Release.Task;
                token.ThrowIfCancellationRequested(); return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            }
        }
        private static async Task ReplacedViewResponse()
        {
            using (var fixture = new Fixture("stale-response", ReadyPage)) {
                var handler = new DelayedValidation(); var api = new NexusApi("fixture-key-not-a-real-account");
                var client = typeof(NexusApi).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic); ((HttpClient)client.GetValue(api)).Dispose(); client.SetValue(api, new HttpClient(handler)); fixture.Form.Controller.Api = api;
                try {
                    fixture.Show(); await Until(delegate { return fixture.Form.InterfaceReady; });
                    await fixture.Form.InterfaceBrowser.ExecuteScriptAsync("chrome.webview.postMessage({id:'old-request',action:'initialize',payload:{}})");
                    await Until(delegate { return handler.Entered.Task.IsCompleted; });
                    await fixture.Form.RetryInterface(); await Until(delegate { return fixture.Form.InterfaceReady; });
                    await fixture.Form.InterfaceBrowser.ExecuteScriptAsync("window.oldReplies=0;chrome.webview.addEventListener('message',e=>{if(e.data.id==='old-request')window.oldReplies++})");
                    handler.Release.TrySetResult(true);
                    var initialization = (Task)typeof(Controller).GetField("initializationTask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Form.Controller);
                    await initialization; await Task.Delay(100);
                    Expect(await fixture.Form.InterfaceBrowser.ExecuteScriptAsync("window.oldReplies") == "0", "A response from the old view was delivered into the replacement interface.");
                } finally { handler.Release.TrySetResult(true); }
            }
        }
    }
}
