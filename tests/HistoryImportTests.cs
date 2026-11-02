using System;
using System.Collections;
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
    // Browser-level fixture checks, kept outside the shipped app. The virtual host maps
    // test pages locally; a fresh profile prevents use of any user's Nexus session.
    internal sealed class HistoryImportTests : Form
    {
        private readonly WebView2 browser = new WebView2();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private readonly List<object> results = new List<object>();
        private readonly string root;
        private readonly string report;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private string phase = "Starting fixture", currentCheck = "", runtimeVersion = "";
        private bool infrastructureFailureRecorded;
        private int failures;

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "history-import-tests.json");
            Application.Run(new HistoryImportTests(report));
        }

        private HistoryImportTests(string output)
        {
            report = output;
            root = Path.Combine(Path.GetTempPath(), "NexusM-HistoryTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Width = 800; Height = 600; ShowInTaskbar = false; Opacity = 0;
            Controls.Add(browser);
            browser.Dock = DockStyle.Fill;
            Shown += async delegate { await Run(); };
        }

        private async Task Run()
        {
            try
            {
                WriteReport(true);
                await Check("stalled browser operations fail within their deadline and retain diagnostics", async delegate
                {
                    bool timedOut = false;
                    try { await AwaitOperation(new TaskCompletionSource<bool>().Task, "Verifying stalled-operation deadline", 50); }
                    catch (FixtureInfrastructureException error) { timedOut = error.InnerException is TimeoutException && error.Message.Contains("Verifying stalled-operation deadline"); }
                    Expect(timedOut, "A stalled browser operation did not produce a named timeout.");
                    var failed = new TaskCompletionSource<bool>();
                    failed.SetException(new InvalidOperationException("Synthetic browser failure"));
                    bool retained = false;
                    try { await AwaitOperation(failed.Task, "Verifying failed-operation diagnostics"); }
                    catch (FixtureInfrastructureException error) { retained = error.InnerException != null && error.InnerException.Message == "Synthetic browser failure"; }
                    Expect(retained, "A browser failure lost its original diagnostic.");
                });
                currentCheck = "";
                SetPhase("Creating WebView2 environment");
                // Hidden CI windows must run the real page timers without background clamping.
                // This development-only flag never reaches the shipped application.
                var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-background-timer-throttling" };
                CoreWebView2Environment environment = await AwaitOperation(CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "profile"), options), phase, 30000);
                runtimeVersion = environment.BrowserVersionString;
                SetPhase("Initializing WebView2 control");
                await AwaitOperation(browser.EnsureCoreWebView2Async(environment), phase, 30000);
                browser.CoreWebView2.SetVirtualHostNameToFolderMapping("www.nexusmods.com", root, CoreWebView2HostResourceAccessKind.DenyCors);
                browser.CoreWebView2.SetVirtualHostNameToFolderMapping("untrusted.example", root, CoreWebView2HostResourceAccessKind.DenyCors);
                // Fail closed if a fixture accidentally initiates a real network request.
                browser.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                browser.CoreWebView2.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
                {
                    Uri uri;
                    if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) ||
                        (uri.Host != "www.nexusmods.com" && uri.Host != "untrusted.example"))
                        e.Response = environment.CreateWebResourceResponse(new MemoryStream(new byte[0]), 403, "Fixture network blocked", "Content-Type: text/plain");
                };

                await Check("all 261 client DataTable rows, date columns, and deduplication", async delegate
                {
                    string script = RowsScript(261) + @"
rows.push(rows[0].slice()); rows[rows.length-1][2] = 1792497600;
const table=document.querySelector('table');
const api={settings:()=>[{oFeatures:{bServerSide:false}}],rows:()=>({indexes:()=>({toArray:()=>rows.map((_,i)=>i)})}),
row:i=>({data:()=>rows[i]}),cell:(i,c)=>({render:type=>c===1?rows[i][2]:c===4?rows[i][0]:''})};
window.jQuery=()=>({DataTable:()=>api});
window.jQuery.fn={dataTable:{tables:()=>[table]}};
window.fetch=()=>{throw new Error('DataTable extraction should not fetch');};";
                    await LoadFixture("datatable", BaseTable() + "<script>" + script + "</script>", false, true);
                    Dictionary<string, object> state = await Import("");
                    Expect(Get(state, "status") == "complete", "Import failed: " + Get(state, "message"));
                    object[] items = Items(state);
                    Expect(items.Length == 261, "Expected 261 unique rows beyond the visible page.");
                    Dictionary<string, object> first = (Dictionary<string, object>)items[0];
                    Expect(Get(first, "lastDownloadedUtc") == "2026-10-20T12:00:00.000Z", "Deduplication did not retain newest download.");
                    Expect(Get(first, "updatedUtc") == "2026-10-19T00:00:00.000Z", "Updated date mapping is wrong.");
                    Expect(!Convert.ToBoolean(state["partial"]), "DataTable import should cover all pages.");
                });

                await Check("signed-in website endpoint without jQuery, full history and selected game filter", async delegate
                {
                    string script = RowsScript(261) + @"
rows.push([1792368000,'Different game',1792281600,'Author','Gameplay','',0,1,5,'skyrimspecialedition']);
window.fetch=async(url,options)=>{
if(url!=='/Core/Libs/Common/Managers/Mods?GetDownloadHistory'||options.method!=='GET'||options.credentials!=='same-origin') throw new Error('Unexpected request');
return {ok:true,headers:{get:()=> 'application/json'},json:async()=>({data:rows})};};";
                    await LoadFixture("endpoint", "<script>" + script + "</script>", false, true);
                    Dictionary<string, object> state = await Import("baldursgate3");
                    Expect(Get(state, "status") == "complete", "Endpoint import failed.");
                    Expect(Items(state).Length == 261, "The selected game should contain exactly 261 rows.");
                    Expect(Convert.ToInt32(state["processed"]) == 262, "Did not read all server rows.");
                    Dictionary<string, object> first = (Dictionary<string, object>)Items(state)[0];
                    Expect(Get(first, "lastDownloadedUtc") == "2026-10-18T00:00:00.000Z", "Raw download timestamp was not parsed.");
                });

                await Check("legacy HTML fallback preserves dates and explicitly reports one visible page", async delegate
                {
                    string rows = "";
                    for (int i = 1; i <= 20; i++) rows += HtmlRow("/baldursgate3/mods/" + i, "Legacy " + i);
                    await LoadFixture("legacy", BaseTable(rows) + "<script>window.fetch=async()=>{throw new Error('No website endpoint')};</script>", false, true);
                    Dictionary<string, object> state = await Import("");
                    Expect(Get(state, "status") == "complete", "Visible HTML fallback failed.");
                    Expect(Items(state).Length == 20, "HTML fallback row count is wrong.");
                    Expect(Convert.ToBoolean(state["partial"]), "Visible HTML must be marked partial.");
                    Dictionary<string, object> first = (Dictionary<string, object>)Items(state)[0];
                    Expect(Get(first, "lastDownloadedUtc") == "2026-10-18T14:44:00.000Z", "HTML datetime was lost.");
                    Expect(Get(first, "updatedUtc") == "2026-10-19T01:02:00.000Z", "HTML update datetime was lost.");
                });

                await Check("modern game URLs, encoded names, and unknown dates", async delegate
                {
                    string row = HtmlRow("/games/baldursgate3/mods/987", "A &amp; B").Replace("2026-10-18T14:44:00Z", "unknown");
                    await LoadFixture("modern", BaseTable(row) + "<script>window.fetch=async()=>({ok:false,status:401});</script>", false, true);
                    Dictionary<string, object> state = await Import("");
                    Expect(Get(state, "status") == "complete", "Modern URL fallback failed.");
                    Dictionary<string, object> item = (Dictionary<string, object>)Items(state)[0];
                    Expect(Get(item, "gameDomain") == "baldursgate3" && Convert.ToInt32(item["modId"]) == 987, "Modern URL parsed incorrectly.");
                    Expect(Get(item, "name") == "A & B", "HTML name was not decoded.");
                    Expect(item["lastDownloadedUtc"] == null, "Unknown dates must remain unknown.");
                });

                await Check("raw array history names stay inert", async delegate
                {
                    string script = @"
const payload='<img src=/missing-history-array onerror=window.__historyPayloadRan=true>';
const rows=[[1792368000,payload,1792281600,'Author','Gameplay','',0,3474,987,'baldursgate3']];
window.fetch=async()=>({ok:true,headers:{get:()=> 'application/json'},json:async()=>({data:rows})});";
                    await LoadFixture("security-array", "<script>" + script + "</script>", false, true);
                    Dictionary<string, object> state = await Import("");
                    Expect(Get(state, "status") == "complete", "Synthetic history import failed.");
                    await Task.Delay(300);
                    Expect(await Eval("window.__historyPayloadRan===true") == "false", "Raw array name executed an image error handler in the signed-in origin.");
                });

                await Check("raw object history names stay inert", async delegate
                {
                    string script = @"
const payload='<img src=/missing-history-object onerror=window.__historyPayloadRan=true>';
window.fetch=async()=>({ok:true,headers:{get:()=> 'application/json'},json:async()=>({data:[{game_domain_name:'baldursgate3',mod_id:987,name:payload}]})});";
                    await LoadFixture("security-object", "<script>" + script + "</script>", false, true);
                    Dictionary<string, object> state = await Import("");
                    Expect(Get(state, "status") == "complete", "Synthetic history import failed.");
                    await Task.Delay(300);
                    Expect(await Eval("window.__historyPayloadRan===true") == "false", "Raw object name executed an image error handler in the signed-in origin.");
                });

                await Check("escaped link text is not reinterpreted as active HTML", async delegate
                {
                    string row = HtmlRow("/games/baldursgate3/mods/987", "&lt;img src=/missing-history-encoded onerror=window.__historyPayloadRan=true&gt;");
                    await LoadFixture("security-escaped", BaseTable(row) + "<script>window.fetch=async()=>({ok:false,status:401});</script>", false, true);
                    Expect(await Eval("window.__historyPayloadRan===true") == "false", "Payload ran before import.");
                    Dictionary<string, object> state = await Import("");
                    Expect(Get(state, "status") == "complete", "Synthetic history import failed.");
                    await Task.Delay(300);
                    Expect(await Eval("window.__historyPayloadRan===true") == "false", "Escaped link text was decoded then executed during import.");
                    Expect(Get((Dictionary<string, object>)Items(state)[0], "name") == "<img src=/missing-history-encoded onerror=window.__historyPayloadRan=true>", "Literal link text should remain readable without being reparsed as HTML.");
                });

                await Check("endpoint HTML cells stay inert through row and image extraction", async delegate
                {
                    string script = @"
const html='<a href=/games/baldursgate3/mods/987>Safe name</a><img src=/missing-history-html onerror=window.__historyPayloadRan=(window.__historyPayloadRan||0)+1>';
const rows=[[html,'2026-10-18T14:44:00Z','Author','Gameplay','2026-10-19T01:02:00Z','']];
window.fetch=async()=>({ok:true,headers:{get:()=> 'application/json'},json:async()=>({data:rows})});";
                    await LoadFixture("security-html-cells", "<script>" + script + "</script>", false, true);
                    Expect(await Eval("!!window.__historyPayloadRan") == "false", "Payload ran before import.");
                    Dictionary<string, object> state = await Import("");
                    Expect(Get(state, "status") == "complete" && Items(state).Length == 1, "Synthetic HTML history import failed.");
                    await Task.Delay(300);
                    string activations = await Eval("window.__historyPayloadRan||0");
                    Expect(activations == "0", "Endpoint HTML executed image error handlers " + activations + " times during row/image extraction.");
                });

                await Check("untrusted page is rejected before any fetch", async delegate
                {
                    await LoadFixture("untrusted", "<script>window.fetch=()=>{document.title='NETWORK WAS USED';throw new Error('Unexpected fetch')};</script>", true, true);
                    Dictionary<string, object> state = await Import("");
                    Expect(Get(state, "status") == "error", "Untrusted host was accepted.");
                    Expect(await Eval("document.title") != "\"NETWORK WAS USED\"", "Untrusted host issued a request.");
                });

                await Check("non-history Nexus page is rejected before any fetch", async delegate
                {
                    await LoadFixture("wrongpage", "<script>window.fetch=()=>{document.title='NETWORK WAS USED';throw new Error('Unexpected fetch')};</script>", false, false);
                    Dictionary<string, object> state = await Import("");
                    Expect(Get(state, "status") == "error", "A non-history page was accepted.");
                    Expect(await Eval("document.title") != "\"NETWORK WAS USED\"", "Non-history page issued a request.");
                });

                await Check("game domain validation and safe history URLs", delegate
                {
                    Expect(BrowserImport.HistoryUrl("BaldursGate3") == "https://www.nexusmods.com/baldursgate3/users/myaccount?tab=download+history", "Game history URL is wrong.");
                    Expect(BrowserImport.HistoryUrl("") == "https://www.nexusmods.com/users/myaccount?tab=download+history", "Global history URL is wrong.");
                    bool rejected = false;
                    try { BrowserImport.StartHistoryScript("');window.evil=true;//"); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "Script injection through game domain was accepted.");
                    return Task.FromResult(0);
                });
            }
            catch (Exception error)
            {
                if (!infrastructureFailureRecorded)
                {
                    failures++;
                    results.Add(new { name = "Test harness", passed = false, error = error.ToString(), stage = phase, elapsedMilliseconds = elapsed.ElapsedMilliseconds });
                }
            }
            // Keep asynchronous cleanup outside finally for the app's C# 5 compiler.
            try
            {
                SetPhase("Disposing fixture browser");
                browser.Dispose();
                SetPhase("Cleaning fixture profile");
                string absolute = Path.GetFullPath(root);
                string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "NexusM-HistoryTests-";
                if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe history fixture cleanup path.");
                await AwaitOperation(Task.Run(delegate
                {
                    try { if (Directory.Exists(absolute)) Directory.Delete(absolute, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }), "Cleaning fixture profile", 5000);
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
                failures++;
                results.Add(new { name = name, passed = false, error = error.ToString(), stage = phase, elapsedMilliseconds = clock.ElapsedMilliseconds });
                // A broken renderer cannot safely run later fixtures; retain the first failure.
                if (error is FixtureInfrastructureException) { infrastructureFailureRecorded = true; throw; }
            }
            finally { WriteReport(true); }
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
            // The launcher tolerates a partial snapshot while running and validates the
            // final report after exit. Avoid replacing files held by Windows scanners.
            using (var stream = new FileStream(report, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(json.Serialize(new { passed = !running && failures == 0, running = running, phase = phase, currentCheck = currentCheck, elapsedMilliseconds = elapsed.ElapsedMilliseconds, runtimeVersion = runtimeVersion, failures = failures, checks = results }));
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
                    if (await Task.WhenAny(task, Task.Delay(milliseconds, cancel.Token)) != task)
                        throw new TimeoutException(operation + " timed out after " + milliseconds + " ms.");
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

        private async Task<string> Eval(string script, string operation = "Executing fixture JavaScript", int milliseconds = 10000)
        {
            SetPhase(operation);
            Task<string> task;
            try { task = browser.ExecuteScriptAsync(script); }
            catch (Exception error) { throw new FixtureInfrastructureException(operation + ": " + error.Message, error); }
            return await AwaitOperation(task, operation, milliseconds);
        }

        private async Task LoadFixture(string name, string body, bool untrusted, bool history)
        {
            File.WriteAllText(Path.Combine(root, name + ".html"), "<!doctype html><html><head><meta charset='utf-8'><title>History fixture</title></head><body>" + body + "</body></html>", new UTF8Encoding(false));
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>();
            string target = "https://" + (untrusted ? "untrusted.example" : "www.nexusmods.com") + "/" + name + ".html" + (history ? "?tab=download+history" : "");
            ulong navigationId = 0;
            EventHandler<CoreWebView2NavigationStartingEventArgs> starting = delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                if (e.Uri == target) navigationId = e.NavigationId;
            };
            EventHandler<CoreWebView2NavigationCompletedEventArgs> completed = delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (navigationId == 0 || e.NavigationId != navigationId) return;
                if (e.IsSuccess) done.TrySetResult(true); else done.TrySetException(new Exception("Fixture navigation failed: " + e.WebErrorStatus));
            };
            browser.CoreWebView2.NavigationStarting += starting;
            browser.CoreWebView2.NavigationCompleted += completed;
            try
            {
                SetPhase("Navigating to " + name);
                try { browser.CoreWebView2.Navigate(target); }
                catch (Exception error) { throw new FixtureInfrastructureException(phase + ": " + error.Message, error); }
                await AwaitOperation(done.Task, phase, 15000);
            }
            finally
            {
                browser.CoreWebView2.NavigationStarting -= starting;
                browser.CoreWebView2.NavigationCompleted -= completed;
            }
        }

        private async Task<Dictionary<string, object>> Import(string game)
        {
            await Eval(BrowserImport.StartHistoryScript(game), "Starting history import");
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 10000)
            {
                int remaining = Math.Max(1, 10000 - (int)clock.ElapsedMilliseconds);
                Dictionary<string, object> state = json.Deserialize<Dictionary<string, object>>(await Eval(BrowserImport.PollHistoryScript, "Polling history import", remaining));
                if (Get(state, "status") != "running") return state;
                await Task.Delay(100);
            }
            throw new FixtureInfrastructureException("History import did not complete within 10000 ms.", new TimeoutException("History import polling deadline exceeded."));
        }

        private static string RowsScript(int count)
        {
            return "const rows=[];for(let i=1;i<=" + count + ";i++)rows.push([1792368000,'Mod '+i,1792281600,'Author','Gameplay','',0,3474,i,'baldursgate3']);";
        }

        private static string BaseTable(string rows = "")
        {
            return "<table><thead><tr><th>Mod name</th><th>Last DL</th><th>Uploader</th><th>Category</th><th>Updated</th><th>Endorsement</th></tr></thead><tbody>" + rows + "</tbody></table>";
        }

        private static string HtmlRow(string url, string name)
        {
            return "<tr><td><a href='" + url + "'>" + name + "</a></td><td><time datetime='2026-10-18T14:44:00Z'>18 October 2026, 2:44 pm</time></td><td>Author</td><td>Gameplay</td><td><time datetime='2026-10-19T01:02:00Z'>19 October 2026, 1:02 am</time></td><td></td></tr>";
        }

        private static string Get(Dictionary<string, object> value, string key)
        {
            object result;
            return value.TryGetValue(key, out result) && result != null ? result.ToString() : "";
        }

        private static object[] Items(Dictionary<string, object> state)
        {
            object value = state["items"];
            ArrayList list = value as ArrayList;
            if (list != null) return list.ToArray();
            return (object[])value;
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
