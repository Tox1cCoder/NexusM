using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NexusM
{
    // Real WebView fixtures use a fresh profile and intercept every resource.
    // No Nexus session, API key, private endpoint, or archive is accessed.
    internal sealed class FreeDownloadFlowTests : Form
    {
        private readonly WebView2 browser = new WebView2();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly List<object> results = new List<object>();
        private readonly string root;
        private readonly string report;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly TaskCompletionSource<string> browserFailure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> browserExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private CoreWebView2Environment environment;
        private bool browserInitialized;
        private readonly List<string> captureEvents = new List<string>();
        private TaskCompletionSource<bool> handoff = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private string fixture = "", navigation, phase = "Starting fixture", currentCheck = "", runtimeVersion = "", lifecycle = "";
        private bool finishing, failureRecorded;
        private int failures, captured;
        private static readonly string selected = "https://www.nexusmods.com/baldursgate3/mods/42?tab=files&file_id=123&nmm=1";

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new FreeDownloadFlowTests(args.Length == 0 ? Path.Combine(Environment.CurrentDirectory, "free-download-tests.json") : Path.GetFullPath(args[0])));
        }

        private FreeDownloadFlowTests(string output)
        {
            report = output;
            root = Path.Combine(Path.GetTempPath(), "NexusM-FreeDownloadTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Width = 800; Height = 600; ShowInTaskbar = false; Opacity = 0;
            browser.Dock = DockStyle.Fill; Controls.Add(browser);
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!finishing) RecordBrowserFailure("Fixture form closing: " + e.CloseReason); };
            browser.Disposed += delegate { if (!finishing) RecordBrowserFailure("Fixture browser disposed unexpectedly"); };
            Shown += async delegate { await Run(); };
        }

        private async Task Run()
        {
            try
            {
                WriteReport(true);
                SetPhase("Creating WebView2 environment");
                // Hidden fixture timers must run normally; this option is never shipped.
                var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-background-timer-throttling" };
                environment = await AwaitOperation(CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "profile"), options), phase, 30000);
                runtimeVersion = environment.BrowserVersionString;
                environment.BrowserProcessExited += delegate(object sender, CoreWebView2BrowserProcessExitedEventArgs e)
                {
                    browserExited.TrySetResult(true);
                    RecordBrowserFailure("WebView2 browser exited: " + e.BrowserProcessExitKind + " (PID " + e.BrowserProcessId + ")");
                };
                SetPhase("Initializing WebView2 control");
                await AwaitOperation(browser.EnsureCoreWebView2Async(environment), phase, 30000);
                browserInitialized = true;
                browser.CoreWebView2.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e)
                {
                    if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                        RecordBrowserFailure("WebView2 process failed: " + e.ProcessFailedKind + "; " + e.Reason + " (exit " + e.ExitCode + "): " + e.ProcessDescription);
                };
                browser.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                browser.CoreWebView2.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
                {
                    bool document = e.ResourceContext == CoreWebView2WebResourceContext.Document;
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(document ? fixture : "")), document ? 200 : 403, document ? "Local fixture" : "Network blocked",
                        "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'");
                };
                browser.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
                {
                    if (e.Uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; RecordCapture("NavigationStarting"); }
                    else if (navigation != null && e.Uri != navigation) e.Cancel = true;
                };
                browser.CoreWebView2.LaunchingExternalUriScheme += delegate(object sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
                {
                    e.Cancel = true;
                    if (e.Uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) RecordCapture("LaunchingExternalUriScheme");
                };

                await Check("ordinary slow click runs once and preserves the website countdown", async delegate
                {
                    await LoadFixture(selected, "<p id='phase'>Ready</p><button id='slowDownloadButton' data-download-url='#ERROR-download-location-not-found'>Slow download</button><script>window.clicks=0;document.querySelector('button').onclick=()=>{window.clicks++;document.querySelector('#phase').textContent='Countdown';window.countdownStarted=performance.now();setTimeout(()=>{window.countdownFinished=performance.now();document.querySelector('#phase').textContent='Done';location.href='nxm://baldursgate3/mods/42/files/123?key=fixture&expires=2000000000&user_id=1';},1400);};</script>");
                    Dictionary<string, object> state = await AdvanceReady();
                    Expect(Get(state, "status") == "waiting", "A normal download did not wait for Nexus.");
                    Expect(await Eval("window.clicks") == "1", "The slow-download button was not clicked.");
                    Expect(await Eval("document.querySelector('#phase').textContent") == "\"Countdown\"" && captured == 0, "The website countdown was skipped.");
                    await Advance(); await Advance();
                    Expect(await Eval("window.clicks") == "1", "Repeated polling clicked the slow button again.");
                    await WaitForHandoff();
                    // Allow clock precision and timer rounding, while still requiring the countdown.
                    Expect(await Eval("window.countdownFinished-window.countdownStarted >= 1390 && window.clicks===1 && document.querySelector('#phase').textContent==='Done'") == "true", "The website countdown did not finish naturally before its single handoff.");
                });
                await Check("disabled slow button waits until Nexus enables it", async delegate
                {
                    await LoadFixture(selected, "<button id='slowDownloadButton' disabled>Slow download</button><script>window.clicks=0;document.querySelector('button').onclick=()=>window.clicks++;</script>");
                    await AdvanceReady();
                    Expect(await Eval("window.clicks") == "0", "A disabled control was clicked.");
                    await Eval("document.querySelector('button').disabled=false"); await Advance();
                    Expect(await Eval("window.clicks") == "1", "An enabled slow button was not clicked.");
                });
                await Check("current Nexus web component uses its open shadow button and ordinary countdown", async delegate
                {
                    await LoadFixture(selected, "<button onclick='window.bad=true'>Slow download</button><mod-file-download style='display:block' file-id='123' game-domain='/baldursgate3' is-nmm-download='true' user-is-logged-in='true'></mod-file-download><script>window.clicks=0;const root=document.querySelector('mod-file-download').attachShadow({mode:'open'});root.innerHTML='<div><button>Fast download</button><span id=\"nested\"></span></div>';root.querySelector('button').onclick=()=>window.bad=true;const nested=root.querySelector('#nested').attachShadow({mode:'open'});nested.innerHTML='<button>Slow download</button>';nested.querySelector('button').onclick=()=>{window.clicks++;window.countdownStarted=performance.now();setTimeout(()=>{window.countdownFinished=performance.now();location.href='nxm://baldursgate3/mods/42/files/123?key=fixture&expires=2000000000&user_id=1';},1200);};</script>");
                    await AdvanceReady(); await Advance();
                    Expect(await Eval("window.clicks") == "1" && await Eval("window.bad===true") == "false", "The selected component's exact slow button was not clicked once.");
                    Expect(captured == 0, "The component countdown was skipped.");
                    await WaitForHandoff();
                    Expect(await Eval("window.countdownFinished-window.countdownStarted >= 1190 && window.clicks===1 && window.bad!==true") == "true", "The component countdown did not finish naturally before its single handoff.");
                });
                await Check("web components for another file, game, or manual mode are never automated", async delegate
                {
                    foreach (string attributes in new[] { "file-id='124' game-domain='/baldursgate3' is-nmm-download='true'", "file-id='123' game-domain='/skyrim' is-nmm-download='true'", "file-id='123' game-domain='/baldursgate3' is-nmm-download='false'", "file-id='123' game-domain='/baldursgate3' is-nmm-download='true' user-is-logged-in='false'" })
                    {
                        await LoadFixture(selected, "<mod-file-download style='display:block' " + attributes + "></mod-file-download><script>const root=document.querySelector('mod-file-download').attachShadow({mode:'open'});root.innerHTML='<button>Slow download</button>';root.querySelector('button').onclick=()=>window.bad=true;</script>");
                        Expect(Get(await AdvanceReady(), "status") == "needs-user" && await Eval("window.bad===true") == "false", "An unrelated or logged-out component was automated.");
                    }
                });
                await Check("matching requirements link continues without clicking dependencies or purchases", async delegate
                {
                    await LoadFixture(selected, "<section class='popup-mod-requirements'><a href='https://users.nexusmods.com/register/memberships'>Download</a><a href='/baldursgate3/mods/99?tab=files&file_id=456&nmm=1'>Mod manager download</a><a id='chosen' href='/baldursgate3/mods/42?tab=files&file_id=123&nmm=1'>Download</a></section><script>window.clicked=[];document.querySelectorAll('a').forEach(a=>a.onclick=e=>{e.preventDefault();window.clicked.push(a.id||'wrong');});</script>");
                    await AdvanceReady(); await Advance();
                    Expect(await Eval("JSON.stringify(window.clicked)") == "\"[\\\"chosen\\\"]\"", "The chosen requirements link did not advance exactly once.");
                });
                await Check("unrecognised requirements need the user", async delegate
                {
                    await LoadFixture(selected, "<section class='popup-mod-requirements'><a href='/baldursgate3/mods/42?tab=files&file_id=123'>Manual download</a><button onclick='window.bad=true'>Accept</button></section>");
                    Expect(Get(await AdvanceReady(), "status") == "needs-user", "Unknown requirements were automated.");
                    Expect(await Eval("window.bad===true") == "false", "A general consent control was clicked.");
                });
                await Check("new slow-button markup works without selecting fast download", async delegate
                {
                    await LoadFixture(selected, "<button onclick='window.bad=true'>Fast download</button><button onclick='window.good=true'>Slow download</button>");
                    await AdvanceReady();
                    Expect(await Eval("window.good===true && window.bad!==true") == "true", "The exact Slow download fallback did not work.");
                });
                await Check("hidden controls, links, and premium controls are ignored", async delegate
                {
                    await LoadFixture(selected, "<button id='slowDownloadButton' style='display:none' onclick='window.bad=true'>Slow download</button><a href='nxm://baldursgate3/mods/42/files/123'>Slow download</a><button onclick='window.bad=true'>Fast download</button>");
                    await AdvanceReady();
                    Expect(await Eval("window.bad===true") == "false" && captured == 0, "An unrelated or hidden control was used.");
                });
                await Check("Cloudflare and login pages require the user", async delegate
                {
                    await LoadFixture(selected, "<h1>Checking your browser</h1><div id='challenge-form'>Verify</div><button id='slowDownloadButton' onclick='window.bad=true'>Slow download</button>");
                    Expect(Get(await Advance(), "status") == "needs-user" && await Eval("window.bad===true") == "false", "The browser challenge was automated.");
                    await LoadFixture(selected, "<h1>Please log in</h1><button id='slowDownloadButton' onclick='window.bad=true'>Slow download</button>");
                    Expect(Get(await Advance(), "status") == "needs-user" && await Eval("window.bad===true") == "false", "A logged-out page was automated.");
                    await LoadFixture("https://users.nexusmods.com/auth/sign_in", "<button id='slowDownloadButton' onclick='window.bad=true'>Slow download</button>");
                    Expect(Get(await Advance(), "status") == "needs-user" && await Eval("window.bad===true") == "false", "Sign-in controls were automated.");
                });
                await Check("hidden login modal does not block the normal signed-in page", async delegate
                {
                    await LoadFixture(selected, "<section style='display:none'><h1>Please log in</h1><input type='password'></section><button id='slowDownloadButton' onclick='window.good=true'>Slow download</button>");
                    await AdvanceReady();
                    Expect(await Eval("window.good===true") == "true", "The hidden site login popup blocked a normal download.");
                });
                await Check("cookie consent is shown for an explicit user choice", async delegate
                {
                    await LoadFixture(selected, "<div id='onetrust-banner-sdk'>Cookie settings</div><button id='slowDownloadButton' onclick='window.bad=true'>Slow download</button>");
                    Expect(Get(await Advance(), "status") == "needs-user" && await Eval("window.bad===true") == "false", "Cookie preferences were bypassed.");
                });
                await Check("wrong host, game, mod, and file are never clicked", async delegate
                {
                    foreach (string wrong in new[] { selected.Replace("www.nexusmods.com", "nexusmods.com.evil.example"), selected.Replace("baldursgate3", "skyrim"), selected.Replace("mods/42", "mods/43"), selected.Replace("file_id=123", "file_id=124"), selected.Replace("https:", "http:") })
                    {
                        await LoadFixture(wrong, "<button id='slowDownloadButton' onclick='window.bad=true'>Slow download</button>");
                        Expect(Get(await Advance(), "status") == "needs-user" && await Eval("window.bad===true") == "false", "An unrelated file page was automated.");
                    }
                });
                await Check("unknown markup and stalled handoffs stop after a bounded wait", async delegate
                {
                    await LoadFixture(selected, "<p>Changed website markup</p>"); await Advance();
                    await Eval("window.__nexusMFreeDownload.startedAt=Date.now()-16000");
                    Expect(Get(await Advance(), "status") == "needs-user", "Unknown markup waited forever.");
                    await Eval("window.__nexusMFreeDownload.startedAt=Date.now()-46000;window.__nexusMFreeDownload.slowClicked=true");
                    Expect(Get(await Advance(), "status") == "needs-user", "A stalled authorization waited forever.");
                });
                await Check("script arguments reject injection and unsafe identifiers", delegate
                {
                    foreach (string bad in new[] { "", "');alert(1);//", "x/nexus", "skyrim.com" })
                    {
                        bool rejected = false; try { FreeDownloadFlow.Script(bad, 42, 123); } catch (ArgumentException) { rejected = true; }
                        Expect(rejected, "An unsafe game domain was accepted.");
                    }
                    foreach (long bad in new[] { 0L, -1L, 9007199254740992L })
                    {
                        bool rejected = false; try { FreeDownloadFlow.Script("baldursgate3", 42, bad); } catch (ArgumentException) { rejected = true; }
                        Expect(rejected, "An unsafe file id was accepted.");
                    }
                    return Task.FromResult(0);
                });
            }
            catch (Exception error)
            {
                if (!failureRecorded) { failures++; results.Add(new { name = "Test harness", passed = false, error = error.ToString(), stage = phase }); }
            }
            // C# 5 does not permit awaited cleanup in finally.
            try
            {
                finishing = true;
                SetPhase("Disposing fixture browser");
                browser.Dispose();
                // The profile remains owned by WebView2 until its browser exit event.
                if (browserInitialized || browserExited.Task.IsCompleted)
                {
                    await AwaitOperation(browserExited.Task, "Waiting for fixture browser shutdown", 10000);
                    SetPhase("Cleaning fixture profile");
                    string absolute = Path.GetFullPath(root);
                    string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "NexusM-FreeDownloadTests-";
                    if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe free-download fixture cleanup path.");
                    await AwaitOperation(Task.Run(delegate
                    {
                        try { if (Directory.Exists(absolute)) Directory.Delete(absolute, true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }), "Cleaning fixture profile", 5000);
                }
            }
            catch (Exception error) { failures++; results.Add(new { name = "Fixture cleanup", passed = false, error = error.ToString(), stage = phase }); }
            finally
            {
                phase = "Complete";
                WriteReport(false);
                Environment.ExitCode = failures == 0 ? 0 : 1;
                Close();
            }
        }

        private async Task Check(string name, Func<Task> test)
        {
            currentCheck = name;
            phase = "Starting check";
            WriteReport(true);
            var clock = Stopwatch.StartNew();
            try { await test(); results.Add(new { name = name, passed = true, elapsedMilliseconds = clock.ElapsedMilliseconds }); }
            catch (Exception error)
            {
                failures++; failureRecorded = true;
                results.Add(new { name = name, passed = false, error = error.ToString(), stage = phase, captures = captured, captureEvents = captureEvents.ToArray(), elapsedMilliseconds = clock.ElapsedMilliseconds });
                // Preserve the first failure; a delayed handoff must not reach a later fixture.
                throw;
            }
            finally { WriteReport(true); }
        }

        private void RecordBrowserFailure(string reason)
        {
            if (finishing) return;
            lifecycle = reason;
            browserFailure.TrySetResult(reason);
            WriteReport(true);
        }

        private void RecordCapture(string source)
        {
            captured++;
            captureEvents.Add(source);
            handoff.TrySetResult(true);
        }

        private async Task WaitForHandoff()
        {
            await AwaitOperation(handoff.Task, "Waiting for website countdown and nxm handoff", 10000);
            // Evaluate the original page after cancellation to drain the handoff event turn.
            await Eval("window.clicks", "Verifying captured handoff");
            Expect(captured == 1, "Expected one website nxm handoff; captured=" + captured + " via " + String.Join(", ", captureEvents) + ".");
        }

        private void SetPhase(string value)
        {
            if (phase == value) return;
            phase = value;
            WriteReport(true);
        }

        private void WriteReport(bool running)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            // The launcher tolerates partial progress JSON and validates the final report
            // after exit. Shared writes avoid Windows replacement/scanner races.
            using (var stream = new FileStream(report, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(json.Serialize(new { passed = !running && failures == 0, running = running, phase = phase, currentCheck = currentCheck, runtimeVersion = runtimeVersion, browserFailure = lifecycle, elapsedMilliseconds = elapsed.ElapsedMilliseconds, failures = failures, checks = results }));
        }

        private sealed class FixtureInfrastructureException : Exception
        {
            internal FixtureInfrastructureException(string message, Exception cause) : base(message, cause) { }
        }

        private async Task AwaitOperation(Task task, string operation, int milliseconds = 10000)
        {
            SetPhase(operation);
            using (var cancel = new CancellationTokenSource())
            {
                try
                {
                    Task delay = Task.Delay(milliseconds, cancel.Token);
                    Task completed = finishing ? await Task.WhenAny(task, delay) : await Task.WhenAny(task, delay, browserFailure.Task);
                    if (completed == browserFailure.Task) throw new InvalidOperationException(browserFailure.Task.Result);
                    if (completed != task) throw new TimeoutException(operation + " timed out after " + milliseconds + " ms.");
                    await task;
                }
                catch (Exception error) { throw new FixtureInfrastructureException(operation + ": " + error.Message, error); }
                finally { cancel.Cancel(); }
            }
        }

        private async Task<T> AwaitOperation<T>(Task<T> task, string operation, int milliseconds = 10000)
        {
            await AwaitOperation((Task)task, operation, milliseconds);
            return await task;
        }

        private async Task LoadFixture(string url, string body)
        {
            fixture = "<!doctype html><html><head><meta charset='utf-8'><title>Download fixture</title></head><body>" + body + "</body></html>";
            navigation = url + (url.Contains("?") ? "&" : "?") + "fixture=" + Guid.NewGuid().ToString("N");
            captured = 0;
            captureEvents.Clear();
            handoff = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong navigationId = 0;
            EventHandler<CoreWebView2NavigationStartingEventArgs> starting = delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                if (e.Uri == navigation) navigationId = e.NavigationId;
            };
            EventHandler<CoreWebView2NavigationCompletedEventArgs> completed = delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (navigationId == 0 || e.NavigationId != navigationId) return;
                if (e.IsSuccess) done.TrySetResult(true); else done.TrySetException(new Exception("Fixture navigation failed: " + e.WebErrorStatus));
            };
            try
            {
                SetPhase("Navigating to local download fixture");
                browser.CoreWebView2.NavigationStarting += starting;
                browser.CoreWebView2.NavigationCompleted += completed;
                browser.CoreWebView2.Navigate(navigation);
                await AwaitOperation(done.Task, phase, 10000);
            }
            catch (FixtureInfrastructureException) { throw; }
            catch (Exception error) { throw new FixtureInfrastructureException(phase + ": " + error.Message, error); }
            finally
            {
                if (browser.CoreWebView2 != null)
                {
                    browser.CoreWebView2.NavigationStarting -= starting;
                    browser.CoreWebView2.NavigationCompleted -= completed;
                }
            }
        }

        private async Task<Dictionary<string, object>> AdvanceReady() { await Advance(); await Task.Delay(600); return await Advance(); }
        private async Task<Dictionary<string, object>> Advance() { return json.Deserialize<Dictionary<string, object>>(await Eval(FreeDownloadFlow.Script("baldursgate3", 42, 123))); }
        private async Task<string> Eval(string script, string operation = "Executing download fixture JavaScript")
        {
            SetPhase(operation);
            Task<string> task;
            try { task = browser.ExecuteScriptAsync(script); }
            catch (Exception error) { throw new FixtureInfrastructureException(operation + ": " + error.Message, error); }
            return await AwaitOperation(task, operation);
        }
        private static string Get(Dictionary<string, object> state, string key) { object value; return state.TryGetValue(key, out value) && value != null ? value.ToString() : ""; }
        private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
