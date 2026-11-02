using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly List<object> browserEvents = new List<object>();
        private readonly List<string> crashDumps = new List<string>();
        private readonly TaskCompletionSource<bool> browserProcessExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private CoreWebView2Environment environment;
        private List<object> checks = new List<object>();
        private string phase = "Starting UI fixture", runtimeVersion = "", failure = "", failurePhase = "";
        private string failureReportFolder = "";
        private uint browserProcessId;
        private object failureControlState;
        private bool disposing;

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
            Width = 1340; Height = 880; ShowInTaskbar = false; Opacity = 1;
            // Exercise the app's opaque compositing path without displaying a test window.
            StartPosition = FormStartPosition.Manual;
            var desktop = SystemInformation.VirtualScreen;
            Location = new System.Drawing.Point(desktop.Right + 100, desktop.Bottom + 100);
            browser.Dock = DockStyle.Fill; Controls.Add(browser);
            browser.Disposed += delegate { if (!disposing) RecordBrowserEvent("Unexpected control disposal", ""); };
            browser.HandleCreated += delegate { if (!disposing) RecordBrowserEvent("Control handle created", ""); };
            browser.HandleDestroyed += delegate { if (!disposing) RecordBrowserEvent("Control handle destroyed", ""); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!disposing) RecordBrowserEvent("Unexpected form close", e.CloseReason.ToString()); };
            Shown += async delegate { await Run(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        private async Task Run()
        {
            bool passed = false;
            try
            {
                SetPhase("Creating WebView2 environment");
                var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-background-timer-throttling", IsCustomCrashReportingEnabled = true };
                environment = await AwaitOperation(CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "profile"), options), 30000);
                runtimeVersion = environment.BrowserVersionString;
                failureReportFolder = environment.FailureReportFolderPath;
                environment.BrowserProcessExited += delegate(object sender, CoreWebView2BrowserProcessExitedEventArgs e)
                {
                    browserProcessExited.TrySetResult(true);
                    if (!disposing) RecordBrowserEvent("Browser process exited", e.BrowserProcessExitKind + " (PID " + e.BrowserProcessId + ")");
                };
                SetPhase("Initializing WebView2 control");
                await AwaitOperation(browser.EnsureCoreWebView2Async(environment), 30000);
                browserProcessId = browser.CoreWebView2.BrowserProcessId;
                browser.CoreWebView2.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e)
                {
                    if (!disposing) RecordBrowserEvent(e.ProcessFailedKind.ToString(), e.Reason + " (exit 0x" + e.ExitCode.ToString("X8") + "): " + e.ProcessDescription + " " + e.FailureSourceModulePath);
                };
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
                browser.CoreWebView2.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) { ready.TrySetResult(e.IsSuccess); if (!e.IsSuccess) RecordBrowserEvent("Navigation failed", e.WebErrorStatus.ToString()); };
                SetPhase("Loading app document");
                browser.CoreWebView2.Navigate("https://nexusm.local/index.html");
                if (await Task.WhenAny(ready.Task, Task.Delay(15000)) != ready.Task || !ready.Task.Result) throw new IOException("The app UI did not load.");
                SetPhase("Waiting for app readiness");
                if (await Task.WhenAny(interfaceReady.Task, Task.Delay(5000)) != interfaceReady.Task || !interfaceReady.Task.Result || stateRequestedBeforeReady)
                    throw new IOException("The app UI did not report a successful initial render before requesting its library.");
                SetPhase("Running browser behavior checks");
                await Eval(File.ReadAllText(Path.Combine(workspace, "tests", "ui-checks.js")));
                object result = null;
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    await Task.Delay(100);
                    result = Data.Json.DeserializeObject(await Eval("window.__uiChecks || {}"));
                    checks = Data.List(Data.Get(result, "checks"));
                    if (Data.Str(result, "status") == "complete") break;
                }
                checks = Data.List(Data.Get(result, "checks"));
                checks.Insert(0, Data.Obj("test", "UI readiness precedes saved-library and account work", "passed", true));
                await Eval("document.querySelector('.toasts').replaceChildren()");
                foreach (int width in new[] { 980, 1060, 1070, 1100, 1120, 1340, 1370, 1380, 1600 })
                {
                    ClientSize = new System.Drawing.Size(width, 880);
                    SetPhase("Library layout at " + width + "px");
                    await Task.Delay(100);
                    checks.Add(Data.Json.DeserializeObject(await Eval(File.ReadAllText(Path.Combine(workspace, "tests", "library-layout-checks.js")))));
                    if (width == 1100)
                    {
                        await Eval("document.querySelector('.mods-section').scrollIntoView({block:'start'})");
                        using (var image = File.Create(Path.Combine(workspace, "artifacts", "library-layout.png")))
                            await AwaitOperation(browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image));
                        await Eval("window.scrollTo(0,0)");
                    }
                }
                foreach (double zoom in new[] { 1.25, 1.5, 2.0 })
                {
                    browser.ZoomFactor = zoom;
                    SetPhase("Library layout at " + zoom + " zoom");
                    ClientSize = new System.Drawing.Size(980, 880);
                    await Task.Delay(100);
                    checks.Add(Data.Json.DeserializeObject(await Eval(File.ReadAllText(Path.Combine(workspace, "tests", "library-layout-checks.js")))));
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
                SetPhase("Capturing final UI preview");
                await Eval("document.querySelector('.toasts').replaceChildren(); window.NexusM.navigate('settings')");
                await Task.Delay(200);
                using (var image = File.Create(Path.ChangeExtension(report, ".png")))
                    await AwaitOperation(browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image));
            }
            catch (Exception error) { passed = false; failure = error.ToString(); failurePhase = phase; failureControlState = ControlState(); WriteReport(true, false); }
            // Await shutdown outside finally for the C# 5 compiler. WebView2 must
            // finish using its user-data folder before that folder is removed.
            try
            {
                disposing = true;
                SetPhase("Disposing UI browser");
                browser.Dispose();
                if (browserProcessId != 0)
                {
                    SetPhase("Waiting for UI browser process exit");
                    await AwaitOperation(browserProcessExited.Task, 10000);
                }
                if (!passed) PreserveCrashReports();
                SetPhase("Cleaning UI fixture profile");
                string absolute = Path.GetFullPath(root);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\NexusM-UiTests-";
                if (!absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe UI fixture cleanup path.");
                try { if (Directory.Exists(absolute)) Directory.Delete(absolute, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            catch (Exception error)
            {
                passed = false;
                if (String.IsNullOrEmpty(failure)) { failure = error.ToString(); failurePhase = phase; failureControlState = ControlState(); }
                else failure += Environment.NewLine + "Cleanup: " + error;
            }
            finally
            {
                phase = "Complete";
                WriteReport(false, passed);
                Environment.ExitCode = passed ? 0 : 1; Close();
            }
        }

        private async Task CheckAppLayout(List<object> checks, int width, int height, double zoom)
        {
            browser.ZoomFactor = zoom;
            SetPhase("App layout at " + width + " x " + height + ", zoom " + zoom);
            ClientSize = new System.Drawing.Size(width, height);
            await Task.Delay(100);
            bool capture = (width == 1340 && zoom == 1) || ((width == 980 || width == 640) && zoom == 2);
            await Eval("window.__captureAppLayout = " + (capture ? "true" : "false"));
            await Eval(File.ReadAllText(Path.Combine(workspace, "tests", "app-layout-checks.js")));
            object result = null;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(20);
                result = Data.Json.DeserializeObject(await Eval("window.__appLayoutChecks || {}"));
                string scene = Data.Str(result, "capture");
                if (capture && !String.IsNullOrEmpty(scene))
                {
                    string name = String.Format("app-layout-{0}-{1}-{2}.png", width, (int)(zoom * 100), scene);
                    using (var image = File.Create(Path.Combine(workspace, "artifacts", name)))
                        await AwaitOperation(browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image));
                    await Eval("window.__resumeAppLayout && window.__resumeAppLayout()");
                }
                if (Data.Str(result, "status") == "complete") break;
            }
            if (Data.Str(result, "status") != "complete") throw new IOException("App layout fixtures did not complete.");
            checks.AddRange(Data.List(Data.Get(result, "checks")));
        }

        private void SetPhase(string value) { phase = value; WriteReport(true, false); }
        private void RecordBrowserEvent(string kind, string detail)
        {
            browserEvents.Add(Data.Obj("kind", kind, "detail", detail, "phase", phase, "elapsedMilliseconds", elapsed.ElapsedMilliseconds, "controlState", ControlState()));
            WriteReport(true, false);
        }
        private void WriteReport(bool running, bool passed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            using (var stream = new FileStream(report, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (var writer = new StreamWriter(stream))
                writer.Write(Data.Json.Serialize(Data.Obj("passed", passed, "running", running, "phase", phase, "failurePhase", failurePhase, "runtimeVersion", runtimeVersion, "browserProcessId", browserProcessId, "elapsedMilliseconds", elapsed.ElapsedMilliseconds, "error", failure, "browserEvents", browserEvents, "failureControlState", failureControlState, "controlState", ControlState(), "crashDumps", crashDumps, "checks", checks)));
        }
        private object ControlState() { return Data.Obj("disposed", browser.IsDisposed, "handleCreated", browser.IsHandleCreated, "visible", browser.Visible, "formVisible", Visible); }
        private void PreserveCrashReports()
        {
            // Only the fresh, offline fixture profile is eligible; never read app profiles.
            if (String.IsNullOrEmpty(failureReportFolder)) return;
            string source = Path.GetFullPath(failureReportFolder);
            if (!source.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                if (!Directory.Exists(source)) return;
                string destination = Path.Combine(Path.GetDirectoryName(report), "ui-browser-crashes");
                Directory.CreateDirectory(destination);
                foreach (string dump in Directory.GetFiles(source, "*.dmp", SearchOption.AllDirectories))
                {
                    string copy = Path.Combine(destination, Path.GetFileName(dump));
                    File.Copy(dump, copy, true); crashDumps.Add(copy);
                }
            }
            catch (IOException error) { browserEvents.Add(Data.Obj("kind", "Crash report copy failed", "detail", error.Message)); }
            catch (UnauthorizedAccessException error) { browserEvents.Add(Data.Obj("kind", "Crash report copy failed", "detail", error.Message)); }
        }
        private async Task AwaitOperation(Task task, int milliseconds = 10000)
        {
            if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task) throw new TimeoutException(phase + " timed out after " + milliseconds + " ms.");
            await task;
        }
        private async Task<T> AwaitOperation<T>(Task<T> task, int milliseconds = 10000)
        {
            await AwaitOperation((Task)task, milliseconds);
            return await task;
        }
        private Task<string> Eval(string script) { return AwaitOperation(browser.ExecuteScriptAsync(script)); }
    }
}
