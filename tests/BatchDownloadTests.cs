using System;
using System.Collections.Generic;
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
    internal static class BatchDownloadTests
    {
        private static readonly List<object> Checks = new List<object>();
        private static string root;

        [STAThread]
        private static int Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            root = Path.Combine(Path.GetTempPath(), "NexusM-BatchDownloadTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string report = Path.GetFullPath(args[0]);
            using (var host = new Form { ShowInTaskbar = false, Opacity = 0 }) {
                host.Shown += async delegate {
                    try {
                        await Check("only one active main file without optional choices is eligible", Eligibility);
                        await Check("selected library IDs are validated and deduplicated before network access", SelectedOnly);
                        await Check("independent checkbox checks share a four-request network bound", SharedConcurrency);
                        await Check("batch start rechecks current file IDs and newly added choices", FreshValidation);
                        await Check("a mixed batch queues valid mods without opening full mod details and skips duplicate authorizations", MixedBatch);
                        await Check("Open Nexus reopens a pending single-file authorization without duplicating its download entry", ReopenPendingAuthorization);
                        await Check("overlapping batch starts and folder cleanup are blocked while preparing", BusyGuards);
                        await Check("failed authorization setup leaves a retryable failed queue entry", AuthorizationFailure);
                        await Check("closing the controller cancels file checks without queuing downloads", Cancellation);
                    }
                    catch (Exception error) { Checks.Add(Data.Obj("name", "Harness failure", "passed", false, "error", error.ToString())); }
                    finally { host.Close(); }
                };
                Application.Run(host);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            System.IO.File.WriteAllText(report, Data.Json.Serialize(Data.Obj("passed", Checks.All(check => Data.Bool(check, "passed")), "checks", Checks)), new UTF8Encoding(false));
            string absolute = Path.GetFullPath(root), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("NexusM-BatchDownloadTests-", StringComparison.Ordinal)) {
                try { Directory.Delete(absolute, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return Checks.All(check => Data.Bool(check, "passed")) ? 0 : 1;
        }

        private static async Task Check(string name, Func<Task> action) {
            try { await action(); Checks.Add(Data.Obj("name", name, "passed", true)); }
            catch (Exception error) { Checks.Add(Data.Obj("name", name, "passed", false, "error", error.ToString())); }
        }
        private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static object Mod(long id) { return Data.Obj("gameDomain", "fixturegame", "modId", id); }
        private static object File(long id, string category) { return Data.Obj("file_id", id, "category_name", category, "name", "Archive " + id, "size_in_bytes", 8192); }
        private static object Payload(params long[] ids) { return Data.Obj("mods", ids.Select(Mod).ToArray()); }
        private static object[] Mods(params long[] ids) { return ids.Select(Mod).ToArray(); }
        private static List<object> Candidates(object result) { return Data.List(Data.Get(result, "candidates")); }
        private static object Candidate(object result, long id) { return Candidates(result).Single(item => Data.Num(item, "modId") == id); }

        private sealed class Fixture : IDisposable
        {
            internal readonly Storage Store;
            internal readonly MainForm Form;
            internal readonly FakeNexus Server = new FakeNexus();
            internal Controller Controller { get { return Form.Controller; } }
            internal readonly List<string> Authorizations = new List<string>();
            internal Fixture(string name, int count) {
                Store = new Storage(Path.Combine(root, name));
                Store.Settings["downloadFolder"] = Path.Combine(root, name, "downloads");
                for (int id = 1; id <= count; id++) { Store.Mods.Add(Data.Obj("gameDomain", "fixturegame", "modId", id, "name", "Mod " + id)); Server.Files[id] = new[] { File(id * 10, "MAIN") }; }
                Form = new MainForm(Store, null, null, "batch-fixture");
                Expect(Controller.Api == null, "Fixture unexpectedly loaded an account key.");
                var api = new NexusApi("synthetic-batch-fixture-key");
                var field = typeof(NexusApi).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic);
                ((HttpClient)field.GetValue(api)).Dispose(); field.SetValue(api, new HttpClient(Server) { Timeout = TimeSpan.FromSeconds(5) }); Controller.Api = api;
            }
            internal void Authorize(string url, string id) { Expect(url.StartsWith("https://www.nexusmods.com/fixturegame/mods/", StringComparison.Ordinal) && url.EndsWith("&nmm=1", StringComparison.Ordinal), "The normal Nexus manager authorization flow was bypassed."); Authorizations.Add(id); }
            public void Dispose() { Server.Release(); Controller.Dispose(); Form.Dispose(); }
        }

        private sealed class FakeNexus : HttpMessageHandler
        {
            internal readonly Dictionary<long, object> Files = new Dictionary<long, object>();
            internal readonly HashSet<long> Fail = new HashSet<long>();
            internal readonly List<long> Requests = new List<long>();
            internal TaskCompletionSource<bool> Gate;
            internal int Active, MaxConcurrent;
            internal void Release() { if (Gate != null) Gate.TrySetResult(true); }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
                string[] parts = request.RequestUri.AbsolutePath.Split('/');
                if (request.RequestUri.Host != "api.nexusmods.com" || parts.Length != 7 || parts[6] != "files.json") throw new Exception("Batch processing requested non-file metadata or a real download.");
                long id = long.Parse(parts[5]);
                lock (Requests) { Requests.Add(id); Active++; MaxConcurrent = Math.Max(MaxConcurrent, Active); }
                try {
                    await Task.Delay(15, token);
                    if (Gate != null) { await Task.WhenAny(Gate.Task, Task.Delay(Timeout.Infinite, token)); token.ThrowIfCancellationRequested(); }
                    if (Fail.Contains(id)) return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonTools.Serializer().Serialize(Data.Obj("files", Files[id])), Encoding.UTF8, "application/json") };
                }
                finally { lock (Requests) { Active--; } }
            }
        }

        private static async Task Eligibility() {
            using (var fixture = new Fixture("eligibility", 8)) {
                fixture.Server.Files[2] = new[] { File(20, "MAIN"), File(21, "OPTIONAL") };
                fixture.Server.Files[3] = new[] { File(30, "MAIN"), File(31, "MAIN") };
                fixture.Server.Files[4] = new[] { File(40, "ARCHIVED"), File(41, "DELETED") };
                fixture.Server.Files[5] = new[] { File(50, "main"), File(51, "ARCHIVED"), File(52, "OLD_VERSION") };
                fixture.Server.Files[6] = new[] { Data.Obj("file_id", 60, "category_id", 1, "name", "Numeric category") };
                fixture.Server.Files[7] = "unreadable";
                fixture.Server.Files[8] = new[] { File(0, "MAIN") };
                object result = await fixture.Controller.Handle("getBatchDownloadCandidates", Payload(1, 2, 3, 4, 5, 6, 7, 8));
                Expect(Candidates(result).Count == 8, "Candidate results did not match the requested mods.");
                foreach (long id in new long[] { 1, 5, 6 }) Expect(Data.Bool(Candidate(result, id), "eligible") && Data.Str(Candidate(result, id), "status") == "eligible", "A valid one-main mod was not eligible.");
                foreach (long id in new long[] { 2, 3, 4 }) Expect(!Data.Bool(Candidate(result, id), "eligible") && Data.Str(Candidate(result, id), "status") == "ineligible" && Data.Str(Candidate(result, id), "reason").Length > 0, "A file choice or missing main file was not explained.");
                foreach (long id in new long[] { 7, 8 }) Expect(Data.Str(Candidate(result, id), "status") == "failed", "Malformed file data did not remain retryable.");
                Expect(Candidates(result).All(candidate => Data.Get(candidate, "file") == null), "Raw file descriptions leaked into small candidate responses.");
                Expect(fixture.Store.Downloads.Count == 0 && fixture.Authorizations.Count == 0, "Eligibility checks initiated a download.");
            }
        }

        private static async Task SelectedOnly() {
            using (var fixture = new Fixture("selected", 3)) {
                object result = await fixture.Controller.Handle("getBatchDownloadCandidates", Payload(2, 2));
                Expect(Candidates(result).Count == 1 && fixture.Server.Requests.SequenceEqual(new long[] { 2 }), "Duplicates or unselected library mods were fetched.");
                int before = fixture.Server.Requests.Count;
                foreach (object payload in new[] { Payload(), Payload(999), Payload(-1), Payload(Enumerable.Repeat(1L, 101).ToArray()), Data.Obj("mods", new[] { Data.Obj("gameDomain", "bad/domain", "modId", 1) }) }) {
                    bool rejected = false; try { await fixture.Controller.Handle("getBatchDownloadCandidates", payload); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "Malformed or unbounded candidate input was accepted.");
                }
                Expect(fixture.Server.Requests.Count == before, "Invalid inputs reached Nexus.");
            }
        }

        private static async Task SharedConcurrency() {
            using (var fixture = new Fixture("concurrency", 12)) {
                await Task.WhenAll(Enumerable.Range(1, 12).Select(id => fixture.Controller.Handle("getBatchDownloadCandidates", Payload(id))));
                Expect(fixture.Server.Requests.Count == 12 && fixture.Server.MaxConcurrent <= 4 && fixture.Server.MaxConcurrent > 1, "Independent checkbox calls bypassed shared bounded concurrency.");
            }
        }

        private static async Task FreshValidation() {
            using (var fixture = new Fixture("fresh", 2)) {
                await fixture.Controller.Handle("getBatchDownloadCandidates", Payload(1, 2));
                fixture.Server.Files[1] = new[] { File(111, "MAIN") };
                fixture.Server.Files[2] = new[] { File(20, "MAIN"), File(21, "OPTIONAL") };
                object result = await fixture.Controller.StartBatchDownload(Mods(1, 2), fixture.Authorize);
                object batch = Data.Get(result, "batch");
                Expect(Data.Num(batch, "queued") == 1 && Data.Num(batch, "skipped") == 1, "Changed file choices were not rechecked at download time.");
                Expect(fixture.Authorizations.SequenceEqual(new[] { "fixturegame:1:111" }), "The stale main file was authorized.");
                Expect(fixture.Store.Downloads.Count == 1 && Data.Num(fixture.Store.Downloads[0], "total") == 8192, "The newly verified file was not queued with its metadata.");
            }
        }

        private static async Task MixedBatch() {
            using (var fixture = new Fixture("mixed", 3)) {
                fixture.Server.Files[2] = new[] { File(20, "MAIN"), File(21, "OPTIONAL") }; fixture.Server.Fail.Add(3);
                object result = await fixture.Controller.StartBatchDownload(Mods(1, 2, 3), fixture.Authorize);
                object batch = Data.Get(result, "batch");
                Expect(Data.Num(batch, "queued") == 1 && Data.Num(batch, "skipped") == 1 && Data.Num(batch, "failed") == 1, "A partial batch did not retain success while reporting both kinds of skipped mod.");
                Expect(fixture.Authorizations.Count == 1 && fixture.Store.Downloads.Count == 1 && Data.Str(fixture.Store.Downloads[0], "status") == "waiting-authorization", "A batch bypassed the normal authorization queue.");
                result = await fixture.Controller.StartBatchDownload(Mods(1), fixture.Authorize);
                Expect(Data.Num(Data.Get(result, "batch"), "queued") == 0 && fixture.Authorizations.Count == 1 && fixture.Store.Downloads.Count == 1, "A repeated batch created a duplicate authorization.");
                object check = await fixture.Controller.Handle("getBatchDownloadCandidates", Payload(1));
                Expect(Data.Str(Candidate(check, 1), "status") == "alreadyQueued", "A queued file was still shown as available for selection.");
            }
        }

        private static async Task BusyGuards() {
            using (var fixture = new Fixture("busy", 1)) {
                fixture.Server.Gate = new TaskCompletionSource<bool>();
                Task<object> starting = fixture.Controller.StartBatchDownload(Mods(1), fixture.Authorize);
                Expect(fixture.Controller.HasActiveTransfers, "Batch preparation did not block authorization launch.");
                bool rejected = false; try { await fixture.Controller.StartBatchDownload(Mods(1), fixture.Authorize); } catch (InvalidOperationException) { rejected = true; }
                Expect(rejected && fixture.Server.Requests.Count == 1, "Overlapping starts repeated file checks or downloads.");
                rejected = false; int confirmations = 0;
                try { fixture.Controller.ClearDownloadFolder(delegate { confirmations++; return true; }); } catch (InvalidOperationException) { rejected = true; }
                Expect(rejected && confirmations == 0, "Folder cleanup started while a batch was preparing.");
                fixture.Server.Release(); await starting;
                Expect(!fixture.Controller.HasActiveTransfers && fixture.Authorizations.Count == 1, "Batch preparation retained its busy state.");
            }
        }

        private static async Task ReopenPendingAuthorization() {
            using (var fixture = new Fixture("reopen-pending", 1)) {
                fixture.Store.Settings["maxParallelDownloads"] = 1;
                await fixture.Controller.StartBatchDownload(Mods(1), fixture.Authorize);
                int requests = fixture.Server.Requests.Count;
                var transfers = (Dictionary<string, CancellationTokenSource>)typeof(Controller).GetField("transfers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Controller);
                using (var unrelated = new CancellationTokenSource()) {
                    transfers["unrelated-fixture-transfer"] = unrelated;
                    try {
                        await fixture.Controller.Handle("retryDownload", Data.Obj("id", "fixturegame:1:10"));
                        var authorizationQueue = (Queue<string[]>)typeof(MainForm).GetField("authorizations", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Form);
                        Expect(authorizationQueue.Count == 1 && authorizationQueue.Peek()[1] == "fixturegame:1:10", "Open Nexus did not reach the existing browser authorization flow.");
                        Expect(fixture.Server.Requests.Count == requests && fixture.Store.Downloads.Count == 1, "Reopening an authorization re-fetched metadata or duplicated the download.");
                    }
                    finally { transfers.Remove("unrelated-fixture-transfer"); }
                }
            }
        }

        private static async Task AuthorizationFailure() {
            using (var fixture = new Fixture("authorization-failure", 1)) {
                object result = await fixture.Controller.StartBatchDownload(Mods(1), delegate { throw new InvalidOperationException("Fixture authorization failure."); });
                Expect(Data.Num(Data.Get(result, "batch"), "failed") == 1 && Data.Str(fixture.Store.Downloads.Single(), "status") == "failed", "A failed authorization remained in the pending queue.");
                result = await fixture.Controller.StartBatchDownload(Mods(1), fixture.Authorize);
                Expect(Data.Num(Data.Get(result, "batch"), "queued") == 1 && fixture.Authorizations.Count == 1, "An authorization setup failure could not be retried.");
            }
        }

        private static async Task Cancellation() {
            using (var fixture = new Fixture("cancel", 1)) {
                fixture.Server.Gate = new TaskCompletionSource<bool>();
                Task<object> starting = fixture.Controller.StartBatchDownload(Mods(1), fixture.Authorize);
                fixture.Controller.Dispose(); bool cancelled = false;
                try { await starting; } catch (OperationCanceledException) { cancelled = true; }
                Expect(cancelled && fixture.Authorizations.Count == 0 && fixture.Store.Downloads.Count == 0, "Closing the app queued a download after cancellation.");
                Expect(!fixture.Controller.HasActiveTransfers, "Cancellation retained batch preparation state.");
            }
        }
    }
}
