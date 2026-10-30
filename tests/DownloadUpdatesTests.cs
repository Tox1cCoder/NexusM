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
    internal static class DownloadUpdatesTests
    {
        private static readonly List<object> Checks = new List<object>();
        private static string root;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            root = Path.Combine(Path.GetTempPath(), "NexusM-DownloadUpdatesTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            using (var host = new Form { Opacity = 0, ShowInTaskbar = false }) {
                host.Shown += async delegate {
                    try {
                        await Check("explicit file relationships follow renamed archives and skipped versions", ResolveChains);
                        await Check("branches, cycles, unavailable targets and unrelated optional files are not guessed", AmbiguousRelations);
                        await Check("only completed retained archives with unchanged fingerprints are checked", RetainedOnly);
                        await Check("missing relationships offer review without choosing a replacement", ReviewFallback);
                        await Check("dismissal persists for one update and a later target notifies again", Dismissal);
                        await Check("transient failures retain known updates and overlapping checks join bounded workers", FailureAndConcurrency);
                        await Check("ordinary downloads notify about the matching retained old archive before authorization", OrdinaryDownloadNotification);
                        await Check("batch downloads reuse their fresh file list to notify about old archives", BatchDownloadNotification);
                        await Check("direct Nexus links notify about old archives before safely failed authorization", DirectNxmNotification);
                        await Check("cancelling a direct Nexus link releases its transfer slot during an old-archive check", CancelDirectNxmNotification);
                        await Check("download notifications match exact targets and preserve dismissal and archive ownership", DownloadNotificationMatching);
                        await Check("replacement keeps old archive until a verified new download completes", Replacement);
                        await Check("normal popup replacement dismisses the refreshed notification while authorization is queued", NormalReplacementDismissal);
                        await Check("replacement joins an already queued newer file without duplicating authorization", JoinPendingReplacement);
                        await Check("replacement reuses a verified completed newer archive without downloading it again", ReuseCompletedReplacement);
                        await Check("an externally changed completed target cannot justify deleting the old archive", ChangedCompletedReplacement);
                        await Check("cancelled replacement cannot delete an archive on a later unrelated download", CancelReplacement);
                        await Check("Keep old file overrides an already queued replacement", KeepOverridesReplacement);
                        await Check("authorization failure rolls back destructive replacement intent", AuthorizationFailure);
                        await Check("targeted deletion keeps history and unrelated archives and partial files", TargetedDeletion);
                        await Check("stale targets and externally changed old archives are kept", StaleAndChanged);
                        await Check("re-downloading an old file cannot redirect existing popup consent", RepeatedOldFile);
                        await Check("unverifiable completed replacements consume their delete intent", UnverifiedCompletion);
                        await Check("old archive cleanup failure keeps the successful new download and reports it", CleanupFailure);
                        await Check("restart, cancel and list clearing remove pending destructive decisions", LifecycleGuards);
                        await Check("folder cleanup prunes notifications and late file changes are skipped", CleanupAndLateChanges);
                    }
                    catch (Exception error) { Checks.Add(Data.Obj("name", "Harness failure", "passed", false, "error", error.ToString())); }
                    finally { host.Close(); }
                };
                Application.Run(host);
            }
            string report = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(report));
            System.IO.File.WriteAllText(report, Data.Json.Serialize(Data.Obj("passed", Checks.All(check => Data.Bool(check, "passed")), "checks", Checks)), new UTF8Encoding(false));
            string absolute = Path.GetFullPath(root), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("NexusM-DownloadUpdatesTests-", StringComparison.Ordinal)) {
                try { Directory.Delete(absolute, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return Checks.All(check => Data.Bool(check, "passed")) ? 0 : 1;
        }

        private static async Task Check(string name, Func<Task> test)
        {
            try { await test(); Checks.Add(Data.Obj("name", name, "passed", true)); }
            catch (Exception error) { Checks.Add(Data.Obj("name", name, "passed", false, "error", error.ToString())); }
        }
        private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static async Task Reject(Func<Task> test) { bool rejected = false; try { await test(); } catch (InvalidOperationException) { rejected = true; } catch (IOException) { rejected = true; } Expect(rejected, "An unsafe operation was accepted."); }
        private static object File(long id, string version = "1.1", string category = "MAIN") { return Data.Obj("file_id", id, "name", "Completely renamed release " + id, "file_name", "new-archive-" + id + ".7z", "version", version, "category_name", category); }
        private static object Edge(long oldId, long newId) { return Data.Obj("old_file_id", oldId, "new_file_id", newId); }
        private static object Response(object[] files, params object[] edges) { return Data.Obj("files", files, "file_updates", edges); }
        private static List<object> Updates(Fixture f) { return Data.List(Data.Get(f.Store.State, "downloadUpdates")); }
        private static Dictionary<string, object> Payload(object update) { return Data.Obj("id", Data.Str(update, "id"), "fileId", Data.Num(update, "fileId"), "updateKey", Data.Str(update, "updateKey"), "archiveKey", Data.Str(update, "archiveKey")); }

        private sealed class Fixture : IDisposable
        {
            internal readonly Storage Store;
            internal readonly MainForm Form;
            internal readonly FakeNexus Server = new FakeNexus();
            internal readonly string Folder;
            internal readonly List<string> Authorizations = new List<string>();
            internal Controller Controller { get { return Form.Controller; } }
            internal Fixture(string name, int count = 1)
            {
                Store = new Storage(Path.Combine(root, "data", name)); Folder = Path.Combine(root, "archives", name); Directory.CreateDirectory(Folder); Store.Settings["downloadFolder"] = Folder;
                for (int modId = 1; modId <= count; modId++) {
                    Store.Mods.Add(Data.Obj("gameDomain", "fixturegame", "modId", modId, "name", "Mod " + modId, "version", "1.1", "updatedAt", 2000));
                    Server.Responses[modId] = Response(new[] { File(modId * 10 + 1) }, Edge(modId * 10, modId * 10 + 1));
                }
                Form = new MainForm(Store, null, null, "update-fixture"); Expect(Controller.Api == null, "Fixture loaded a real API key.");
                var api = new NexusApi("synthetic-update-fixture-key"); var field = typeof(NexusApi).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic);
                ((HttpClient)field.GetValue(api)).Dispose(); field.SetValue(api, new HttpClient(Server) { Timeout = TimeSpan.FromSeconds(5) }); Controller.Api = api;
            }
            internal object Archive(long fileId = 10, long modId = 1, string filename = null)
            {
                string path = Path.Combine(Folder, filename ?? "old-name-" + fileId + ".zip"); System.IO.File.WriteAllText(path, "synthetic archive " + fileId);
                var info = new FileInfo(path);
                var row = Data.Obj("id", "fixturegame:" + modId + ":" + fileId, "gameDomain", "fixturegame", "modId", modId, "fileId", fileId, "name", "Old archive", "filename", Path.GetFileName(path), "version", "1.0", "path", path, "status", "completed", "downloadedAt", 1000,
                    "downloadedBytes", info.Length, "fileLength", info.Length, "fileCreatedUtcTicks", info.CreationTimeUtc.Ticks, "fileModifiedUtcTicks", info.LastWriteTimeUtc.Ticks);
                Store.Downloads.RemoveAll(item => Data.Str(item, "id") == Data.Str(row, "id")); Store.Downloads.Add(row);
                var history = Data.Map(Data.Get(Store.State, "fileDownloads")); history[Data.Str(row, "id")] = new Dictionary<string, object>(row); Store.State["fileDownloads"] = history;
                return row;
            }
            internal void Authorize(string url, string id) { Expect(url.EndsWith("&nmm=1", StringComparison.Ordinal), "Replacement bypassed normal Nexus authorization."); Authorizations.Add(id); }
            internal void CacheDetails(long fileId = 11, long modId = 1)
            {
                var details = (Dictionary<string, object>)typeof(Controller).GetField("details", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Controller);
                details["fixturegame:" + modId] = Data.Obj("files", new[] { Data.Obj("fileId", fileId, "name", "New archive", "category", "MAIN", "version", "1.1") });
            }
            public void Dispose() { Server.Release(); Controller.Dispose(); Form.Dispose(); }
        }

        private sealed class FakeNexus : HttpMessageHandler
        {
            internal readonly Dictionary<long, object> Responses = new Dictionary<long, object>();
            internal readonly HashSet<long> Fail = new HashSet<long>();
            internal readonly List<long> Requests = new List<long>();
            internal TaskCompletionSource<bool> Gate;
            internal int Active, MaxConcurrent, RejectedRequests;
            internal void Release() { if (Gate != null) Gate.TrySetResult(true); }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                string[] parts = request.RequestUri.AbsolutePath.Split('/');
                if (request.RequestUri.Host != "api.nexusmods.com") throw new Exception("Fixture attempted an unexpected network request.");
                if (parts.Length == 6 && parts[4] == "mods" && parts[5].EndsWith(".json", StringComparison.Ordinal))
                    return JsonResponse(Data.Obj("name", "Mod fixture", "version", "1.1", "updated_timestamp", 2000, "game_id", 1));
                if (parts.Length == 7 && parts[6] == "changelogs.json") return JsonResponse(Data.Obj());
                if (request.RequestUri.AbsolutePath == "/v2/graphql") return JsonResponse(Data.Obj("data", Data.Obj("mod", Data.Obj("modRequirements", Data.Obj()))));
                if (parts.Length != 7 || parts[6] != "files.json") { RejectedRequests++; throw new Exception("Fixture rejected an unexpected network request before sending it."); }
                long modId = Int64.Parse(parts[5]);
                lock (Requests) { Requests.Add(modId); Active++; MaxConcurrent = Math.Max(MaxConcurrent, Active); }
                try {
                    await Task.Delay(15, token);
                    if (Gate != null) { await Task.WhenAny(Gate.Task, Task.Delay(Timeout.Infinite, token)); token.ThrowIfCancellationRequested(); }
                    if (Fail.Contains(modId)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"message\":\"Temporary failure\"}") };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Data.Json.Serialize(Responses[modId]), Encoding.UTF8, "application/json") };
                }
                finally { lock (Requests) { Active--; } }
            }
            private static HttpResponseMessage JsonResponse(object value) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Data.Json.Serialize(value), Encoding.UTF8, "application/json") }; }
        }

        private static Task ResolveChains()
        {
            object response = Response(new[] { File(12, "1.2"), File(999, "9.9", "OPTIONAL") }, Edge(11, 12), Edge(10, 11));
            Expect(Data.Num(DownloadUpdateResolver.Resolve(10, response), "file_id") == 12, "Renamed or skipped versions were not linked by Nexus identity.");
            Expect(DownloadUpdateResolver.Resolve(999, response) == null, "An optional file without an update link chose another file.");
            return Task.FromResult(0);
        }
        private static Task AmbiguousRelations()
        {
            Expect(DownloadUpdateResolver.Resolve(10, Response(new[] { File(11), File(12) }, Edge(10, 11), Edge(10, 12))) == null, "A branch chose an arbitrary variant.");
            Expect(DownloadUpdateResolver.Resolve(10, Response(new[] { File(11) }, Edge(10, 11), Edge(11, 10))) == null, "A cyclic graph was accepted.");
            foreach (string category in new[] { "OLD_VERSION", "ARCHIVED", "DELETED" }) Expect(DownloadUpdateResolver.Resolve(10, Response(new[] { File(11, "1.1", category) }, Edge(10, 11))) == null, "An unavailable target was accepted.");
            Expect(Data.Num(DownloadUpdateResolver.Resolve(10, Response(new[] { File(11) }, Edge(10, 11), Edge(10, 11))), "file_id") == 11, "Duplicate equivalent edges looked ambiguous.");
            return Task.FromResult(0);
        }
        private static async Task RetainedOnly()
        {
            using (var f = new Fixture("retained", 5)) {
                f.Archive(10); object changed = f.Archive(20, 2); System.IO.File.AppendAllText(Data.Str(changed, "path"), "externally changed");
                object missing = f.Archive(30, 3); System.IO.File.Delete(Data.Str(missing, "path"));
                object historyOnly = f.Archive(40, 4); f.Store.Downloads.Remove(historyOnly);
                object legacy = f.Archive(50, 5); Data.Map(legacy).Remove("fileModifiedUtcTicks");
                await f.Controller.CheckDownloadedUpdates();
                Expect(Updates(f).Count == 1 && Data.Str(Updates(f)[0], "id") == "fixturegame:1:10", "Nonretained or changed archives were offered for replacement.");
                Expect(f.Server.Requests.SequenceEqual(new long[] { 1 }), "Unowned archives triggered file checks.");
            }
        }
        private static async Task ReviewFallback()
        {
            using (var f = new Fixture("fallback")) {
                f.Archive(); f.Server.Responses[1] = Response(new[] { File(11), File(12, "1.1", "OPTIONAL") });
                await f.Controller.CheckDownloadedUpdates(); object update = Updates(f).Single();
                Expect(Data.Bool(update, "reviewOnly") && Data.Num(update, "fileId") == 0, "Unlinked variants were assigned a replacement.");
                await Reject(async delegate { await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(update), f.Authorize); });
                Expect(f.Authorizations.Count == 0, "Review-only update downloaded an arbitrary variant.");
                Data.Map(f.Store.Mods[0])["version"] = "1.0";
                await f.Controller.CheckDownloadedUpdates(); Expect(Updates(f).Count == 0, "A description-only update notified as a new version.");
            }
        }
        private static async Task Dismissal()
        {
            using (var f = new Fixture("dismissal")) {
                f.Archive(); await f.Controller.CheckDownloadedUpdates();
                await f.Controller.HandleDownloadedUpdate("dismissDownloadUpdate", Payload(Updates(f).Single()));
                await f.Controller.CheckDownloadedUpdates(); Expect(Data.Bool(Updates(f).Single(), "dismissed"), "The same target notified again after dismissal.");
                var restored = new Storage(f.Store.Root); Expect(Data.Str(Data.Get(restored.State, "dismissedDownloadUpdates"), "fixturegame:1:10") == "file:11", "Dismissal did not persist.");
                f.Server.Responses[1] = Response(new[] { File(12, "1.2") }, Edge(10, 11), Edge(11, 12));
                await f.Controller.CheckDownloadedUpdates(); Expect(!Data.Bool(Updates(f).Single(), "dismissed"), "A newly released target stayed dismissed.");
            }
        }
        private static async Task FailureAndConcurrency()
        {
            using (var f = new Fixture("workers", 9)) {
                for (int id = 1; id <= 9; id++) f.Archive(id * 10, id);
                await f.Controller.CheckDownloadedUpdates(); Expect(Updates(f).Count == 9, "Initial update scan missed files.");
                f.Server.Fail.Add(1); f.Server.Gate = new TaskCompletionSource<bool>();
                Task first = f.Controller.CheckDownloadedUpdates(), second = f.Controller.CheckDownloadedUpdates();
                Expect(Object.ReferenceEquals(first, second), "Overlapping checks did not join.");
                await Task.Delay(50); Expect(f.Server.Active <= 4 && f.Server.MaxConcurrent == 4, "File checks were not bounded at four."); f.Server.Release();
                await first; Expect(Updates(f).Count == 9, "A transient error discarded a known update.");
                Expect(f.Controller.DownloadUpdateCheckFailures == 1, "Incomplete archive checks were reported as fully successful.");
            }
        }
        private static async Task Replacement()
        {
            using (var f = new Fixture("replacement")) {
                object old = f.Archive(); string oldPath = Data.Str(old, "path"); await f.Controller.CheckDownloadedUpdates();
                await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), f.Authorize);
                Expect(System.IO.File.Exists(oldPath), "Replacement deleted the old archive before download success.");
                Expect(f.Authorizations.SequenceEqual(new[] { "fixturegame:1:11" }), "The wrong replacement was queued.");
                f.Controller.CompleteDownloadReplacement(Data.Obj("id", "fixturegame:1:11", "status", "failed")); Expect(System.IO.File.Exists(oldPath), "Failed replacement removed the old archive.");
                object final = f.Archive(11, 1, "changed-filename.7z"); f.Controller.CompleteDownloadReplacement(final);
                Expect(!System.IO.File.Exists(oldPath) && System.IO.File.Exists(Data.Str(final, "path")), "Successful replacement did not delete only the old archive.");
                Expect(!f.Store.Downloads.Any(item => Data.Str(item, "id") == "fixturegame:1:10"), "Deleted old archive stayed in Downloads.");
                Expect(Data.Get(Data.Get(f.Store.State, "fileDownloads"), "fixturegame:1:10") != null, "Replacement erased exact-file download history.");
            }
        }
        private static async Task OrdinaryDownloadNotification()
        {
            using (var f = new Fixture("ordinary-download")) {
                object old = f.Archive(); f.CacheDetails();
                await f.Controller.AuthorizeDownload("fixturegame", 1, 11, delegate(string url, string id) {
                    Expect(Updates(f).Count == 1 && Data.Num(Updates(f)[0], "fileId") == 11, "The normal download reached authorization without notifying about its older archive.");
                    f.Authorize(url, id);
                });
                Expect(f.Authorizations.SequenceEqual(new[] { "fixturegame:1:11" }), "The normal download was queued incorrectly.");
                Expect(System.IO.File.Exists(Data.Str(old, "path")) && Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0, "Starting a newer download deleted or authorized deletion of the old archive without consent.");
                var restored = new Storage(f.Store.Root);
                Expect(restored.Downloads.Any(row => Data.Str(row, "id") == "fixturegame:1:11"), "The newer file was not saved in the queue.");
            }
        }
        private static async Task NormalReplacementDismissal()
        {
            using (var f = new Fixture("normal-popup-replacement")) {
                object old = f.Archive(); await f.Controller.CheckDownloadedUpdates();
                // Hold authorization in its saved queue so the fixture never opens a Nexus browser.
                typeof(Controller).GetField("changingSettings", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Controller, true);
                await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()));
                Expect(Updates(f).Count == 1 && Data.Bool(Updates(f)[0], "dismissed"), "Refreshing the notification during authorization left the replacement popup open.");
                Expect(Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 1 && System.IO.File.Exists(Data.Str(old, "path")), "Normal authorization lost its replacement intent or deleted the old archive early.");
                Expect(Data.Str(f.Store.Downloads.Single(row => Data.Str(row, "id") == "fixturegame:1:11"), "status") == "waiting-authorization", "The normal replacement was not saved for authorization.");
            }
        }
        private static async Task BatchDownloadNotification()
        {
            using (var f = new Fixture("batch-download")) {
                object old = f.Archive();
                object result = await f.Controller.StartBatchDownload(new[] { Data.Obj("gameDomain", "fixturegame", "modId", 1) }, f.Authorize);
                Expect(Data.Num(Data.Get(result, "batch"), "queued") == 1 && Updates(f).Count == 1 && Data.Num(Updates(f)[0], "fileId") == 11, "Batch admission did not notify about the older archive.");
                Expect(f.Server.Requests.SequenceEqual(new long[] { 1 }), "Batch notification fetched the file list again instead of reusing the fresh check.");
                Expect(System.IO.File.Exists(Data.Str(old, "path")) && Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0, "Batch admission deleted the old archive or assumed replacement consent.");
            }
        }
        private static async Task DownloadNotificationMatching()
        {
            using (var f = new Fixture("download-matching", 2)) {
                object old = f.Archive(); f.Archive(20, 2);
                object response = Response(new[] { File(11), File(99, "9.9", "OPTIONAL") }, Edge(10, 11));
                await f.Controller.NotifyDownloadingUpdate("fixturegame", 1, 99, response);
                Expect(Updates(f).Count == 0, "An unrelated optional file was offered as a replacement.");
                await f.Controller.NotifyDownloadingUpdate("differentgame", 1, 11, response);
                Expect(Updates(f).Count == 0, "A different game's file identifiers matched the old archive.");
                await f.Controller.NotifyDownloadingUpdate("fixturegame", 1, 10, response);
                Expect(Updates(f).Count == 0, "Re-downloading the old file prompted to replace it with another target.");
                await f.Controller.NotifyDownloadingUpdate("fixturegame", 1, 11, response);
                Expect(Updates(f).Count == 1 && Data.Str(Updates(f)[0], "id") == "fixturegame:1:10", "The selected target did not identify its exact older archive.");
                await f.Controller.HandleDownloadedUpdate("dismissDownloadUpdate", Payload(Updates(f).Single()));
                await f.Controller.NotifyDownloadingUpdate("fixturegame", 1, 11, response);
                Expect(Updates(f).Count == 1 && Data.Bool(Updates(f)[0], "dismissed"), "Starting the same newer download discarded Keep old file.");
                System.IO.File.AppendAllText(Data.Str(old, "path"), "changed externally");
                f.Store.State.Remove("downloadUpdates");
                await f.Controller.NotifyDownloadingUpdate("fixturegame", 1, 11, response);
                Expect(Updates(f).Count == 0, "The requested newer file enabled deletion of an externally changed old archive.");
            }
            using (var f = new Fixture("download-check-failure")) {
                object old = f.Archive(); f.Server.Responses[1] = Data.Obj("files", "unreadable");
                await f.Controller.NotifyDownloadingUpdate("fixturegame", 1, 11);
                Expect(Updates(f).Count == 0 && System.IO.File.Exists(Data.Str(old, "path")), "An unreadable update check invented a replacement or removed the old archive.");
            }
        }
        private static async Task DirectNxmNotification()
        {
            using (var f = new Fixture("direct-nxm-download")) {
                object old = f.Archive();
                var downloader = new DownloadService(f.Controller.Api, null);
                typeof(Controller).GetField("downloader", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Controller, downloader);
                await f.Controller.ReceiveNxm("nxm://fixturegame/mods/1/files/11");
                Expect(Updates(f).Count == 1 && Data.Num(Updates(f)[0], "fileId") == 11, "The direct Nexus link did not create a replacement notification.");
                Expect(System.IO.File.Exists(Data.Str(old, "path")) && Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0, "A failed direct Nexus link deleted or scheduled deletion of the old archive.");
                Expect(Data.Str(f.Store.Downloads.Single(row => Data.Str(row, "id") == "fixturegame:1:11"), "status") == "failed", "The synthetic authorization failure was not retained in the queue.");
                Expect(f.Server.Requests.SequenceEqual(new long[] { 1 }) && f.Server.RejectedRequests == 1, "The direct link did not stop at the fixture's rejected account validation request.");
            }
        }
        private static async Task CancelDirectNxmNotification()
        {
            using (var f = new Fixture("cancel-direct-nxm-download")) {
                object old = f.Archive(); f.Server.Gate = new TaskCompletionSource<bool>();
                var downloader = new DownloadService(f.Controller.Api, null);
                typeof(Controller).GetField("downloader", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Controller, downloader);
                Task receiving = f.Controller.ReceiveNxm("nxm://fixturegame/mods/1/files/11");
                await Task.Delay(30);
                Expect(f.Server.Requests.SequenceEqual(new long[] { 1 }), "Direct Nexus admission skipped its old-archive check.");
                await f.Controller.Handle("cancelDownload", Data.Obj("id", "fixturegame:1:11"));
                Expect(await Task.WhenAny(receiving, Task.Delay(2000)) == receiving, "Cancellation waited for the old-archive API check instead of releasing its transfer slot.");
                await receiving;
                Expect(!f.Server.Gate.Task.IsCompleted && !f.Controller.HasActiveTransfers && f.Server.Active == 0, "The cancelled link still occupied an API request or transfer slot.");
                Expect(Updates(f).Count == 0, "A cancelled old-archive check emitted a notification after cancellation.");
                Expect(System.IO.File.Exists(Data.Str(old, "path")) && Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0, "A cancelled direct Nexus link deleted or scheduled deletion of the old archive.");
                Expect(Data.Str(f.Store.Downloads.Single(row => Data.Str(row, "id") == "fixturegame:1:11"), "status") == "cancelled", "The cancelled direct link lost its terminal queue state.");
                Expect(f.Server.Requests.Count == 1 && f.Server.RejectedRequests == 0, "Cancellation allowed an archive authorization or download request.");
            }
        }
        private static async Task JoinPendingReplacement()
        {
            using (var f = new Fixture("join-pending")) {
                object old = f.Archive(); f.CacheDetails();
                await f.Controller.AuthorizeDownload("fixturegame", 1, 11, f.Authorize);
                await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), f.Authorize);
                Expect(f.Authorizations.SequenceEqual(new[] { "fixturegame:1:11" }), "Replacement duplicated an existing newer download's authorization.");
                Expect(f.Store.Downloads.Count(row => Data.Str(row, "id") == "fixturegame:1:11") == 1 && System.IO.File.Exists(Data.Str(old, "path")), "Joining the pending target duplicated its queue row or deleted the old archive early.");
                object final = f.Archive(11); f.Controller.CompleteDownloadReplacement(final);
                Expect(!System.IO.File.Exists(Data.Str(old, "path")) && System.IO.File.Exists(Data.Str(final, "path")), "The joined newer download did not replace the old archive on verified completion.");
            }
        }
        private static async Task ReuseCompletedReplacement()
        {
            using (var f = new Fixture("reuse-completed")) {
                object old = f.Archive(), final = f.Archive(11); Data.Map(final)["version"] = "1.1";
                await f.Controller.CheckDownloadedUpdates();
                await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), f.Authorize);
                Expect(f.Authorizations.Count == 0, "A verified completed replacement was downloaded again.");
                Expect(!System.IO.File.Exists(Data.Str(old, "path")) && System.IO.File.Exists(Data.Str(final, "path")), "Reusing the completed target failed to delete only the old archive.");
                Expect(Data.Str(final, "status") == "completed" && Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0, "Completed reuse reset the target or left a future destructive intent.");
                Expect(Data.Str(f.Store.State, "notice").Contains("old version was deleted"), "Completed reuse did not report the actual cleanup outcome.");
            }
        }
        private static async Task ChangedCompletedReplacement()
        {
            using (var f = new Fixture("changed-completed-target")) {
                object old = f.Archive(), final = f.Archive(11); System.IO.File.AppendAllText(Data.Str(final, "path"), "changed externally");
                await f.Controller.CheckDownloadedUpdates();
                await Reject(async delegate { await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), f.Authorize); });
                Expect(System.IO.File.Exists(Data.Str(old, "path")) && f.Authorizations.Count == 0 && Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0, "An unverified completed target triggered deletion, another download, or a pending delete decision.");
            }
        }
        private static async Task CancelReplacement()
        {
            using (var f = new Fixture("cancel")) {
                object old = f.Archive(); await f.Controller.CheckDownloadedUpdates();
                await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), f.Authorize);
                f.Controller.CancelDownloadReplacement("fixturegame:1:11");
                object later = f.Archive(11); f.Controller.CompleteDownloadReplacement(later);
                Expect(System.IO.File.Exists(Data.Str(old, "path")), "A cancelled intent deleted the old file on a later download.");
            }
        }
        private static async Task AuthorizationFailure()
        {
            using (var f = new Fixture("auth-failure")) {
                object old = f.Archive(); await f.Controller.CheckDownloadedUpdates();
                await Reject(async delegate { await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), delegate { throw new IOException("Authorization failed."); }); });
                Expect(Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0 && System.IO.File.Exists(Data.Str(old, "path")), "Authorization failure retained destructive intent or deleted old data.");
            }
        }
        private static async Task KeepOverridesReplacement()
        {
            using (var f = new Fixture("keep-overrides")) {
                object old = f.Archive(); await f.Controller.CheckDownloadedUpdates(); var p = Payload(Updates(f).Single());
                await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", p, f.Authorize);
                await f.Controller.HandleDownloadedUpdate("dismissDownloadUpdate", p);
                Expect(Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0, "Keep did not revoke the pending delete decision.");
                object final = f.Archive(11); f.Controller.CompleteDownloadReplacement(final);
                Expect(System.IO.File.Exists(Data.Str(old, "path")) && System.IO.File.Exists(Data.Str(final, "path")), "Keep was overridden when the new download completed.");
            }
        }
        private static async Task TargetedDeletion()
        {
            using (var f = new Fixture("delete")) {
                object old = f.Archive(); string unrelated = Path.Combine(f.Folder, "unrelated.zip"), partial = Path.Combine(f.Folder, ".nexusm-fixturegame-1-99.part");
                // A retained completed queue entry can predate the separate history map.
                f.Store.State.Remove("fileDownloads");
                System.IO.File.WriteAllText(unrelated, "unrelated"); System.IO.File.WriteAllText(partial, "partial");
                System.IO.File.WriteAllText(partial + ".json", Data.Json.Serialize(Data.Obj("id", "fixturegame:1:99", "etag", "fixture", "lastModified", "", "total", 100)));
                await f.Controller.CheckDownloadedUpdates(); await f.Controller.HandleDownloadedUpdate("deleteDownloadUpdate", Payload(Updates(f).Single()));
                Expect(!System.IO.File.Exists(Data.Str(old, "path")) && System.IO.File.Exists(unrelated) && System.IO.File.Exists(partial) && System.IO.File.Exists(partial + ".json"), "Individual delete touched unrelated files or partials.");
                Expect(Data.Get(Data.Get(f.Store.State, "fileDownloads"), "fixturegame:1:10") != null, "Deleting an archive erased its historical flag.");
                Expect(Updates(f).Count == 0 && f.Store.Downloads.Count == 0, "Deleted archive update was not removed.");
            }
        }
        private static async Task StaleAndChanged()
        {
            using (var f = new Fixture("stale")) {
                object old = f.Archive(); await f.Controller.CheckDownloadedUpdates(); var p = Payload(Updates(f).Single());
                f.Server.Responses[1] = Response(new[] { File(12) }, Edge(10, 12));
                await Reject(async delegate { await f.Controller.HandleDownloadedUpdate("deleteDownloadUpdate", p); }); Expect(System.IO.File.Exists(Data.Str(old, "path")), "Stale consent deleted the old archive.");
                await f.Controller.CheckDownloadedUpdates(); p = Payload(Updates(f).Single());
                System.IO.File.SetLastWriteTimeUtc(Data.Str(old, "path"), DateTime.UtcNow.AddDays(-1));
                await Reject(async delegate { await f.Controller.HandleDownloadedUpdate("deleteDownloadUpdate", p); });
                Expect(System.IO.File.Exists(Data.Str(old, "path")), "An externally changed old archive was deleted.");
            }
        }
        private static async Task CleanupFailure()
        {
            using (var f = new Fixture("cleanup-failure")) {
                object old = f.Archive(); await f.Controller.CheckDownloadedUpdates(); await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), f.Authorize);
                object final = f.Archive(11); f.Controller.CompleteDownloadReplacement(final, delegate { throw new IOException("File is locked."); });
                Expect(Data.Str(final, "status") == "completed" && System.IO.File.Exists(Data.Str(final, "path")) && System.IO.File.Exists(Data.Str(old, "path")), "Cleanup failure invalidated the successful new download.");
                Expect(Data.Str(f.Store.State, "notice").Contains("updated archive was downloaded"), "Cleanup failure was not reported without changing success.");
            }
        }
        private static async Task RepeatedOldFile()
        {
            using (var f = new Fixture("repeated-old-file")) {
                object original = f.Archive(); await f.Controller.CheckDownloadedUpdates(); var oldPopup = Payload(Updates(f).Single());
                object latest = f.Archive(10, 1, "redownloaded-old.zip");
                await Reject(async delegate { await f.Controller.HandleDownloadedUpdate("deleteDownloadUpdate", oldPopup); });
                await f.Controller.CheckDownloadedUpdates();
                await Reject(async delegate { await f.Controller.HandleDownloadedUpdate("deleteDownloadUpdate", oldPopup); });
                Expect(System.IO.File.Exists(Data.Str(original, "path")) && System.IO.File.Exists(Data.Str(latest, "path")), "Old consent deleted a different archive downloaded later with the same file ID.");
            }
        }
        private static async Task UnverifiedCompletion()
        {
            using (var f = new Fixture("unverified-final")) {
                object old = f.Archive(); await f.Controller.CheckDownloadedUpdates(); await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), f.Authorize);
                object final = f.Archive(11); System.IO.File.AppendAllText(Data.Str(final, "path"), "externally changed");
                f.Controller.CompleteDownloadReplacement(final);
                Expect(Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0, "Unverifiable final retained destructive intent.");
                object later = f.Archive(11, 1, "later-download.7z"); f.Controller.CompleteDownloadReplacement(later);
                Expect(System.IO.File.Exists(Data.Str(old, "path")), "Later unrelated download activated an unverified completion's old intent.");
            }
        }
        private static async Task LifecycleGuards()
        {
            using (var f = new Fixture("restart-guard")) {
                object old = f.Archive(); await f.Controller.CheckDownloadedUpdates(); await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), f.Authorize);
                var restored = new Storage(f.Store.Root);
                Expect(Data.Map(Data.Get(restored.State, "downloadReplacements")).Count == 0 && Data.List(Data.Get(restored.State, "downloadUpdates")).Count == 0, "Restart restored a stale destructive decision or notification.");
                Expect(Data.Str(restored.Downloads.Single(item => Data.Str(item, "id") == "fixturegame:1:11"), "status") == "interrupted", "Restart did not stop the pending authorization.");
                Expect(System.IO.File.Exists(Data.Str(old, "path")), "Restart removed the old archive.");
            }
            using (var f = new Fixture("cancel-hook")) {
                object old = f.Archive(); await f.Controller.CheckDownloadedUpdates(); await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), f.Authorize);
                await f.Controller.Handle("cancelDownload", Data.Obj("id", "fixturegame:1:11"));
                Expect(Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0 && System.IO.File.Exists(Data.Str(old, "path")), "Cancelling a queued replacement left its delete decision active.");
            }
            using (var f = new Fixture("clear-hook")) {
                object old = f.Archive(); await f.Controller.CheckDownloadedUpdates(); await f.Controller.HandleDownloadedUpdate("replaceDownloadUpdate", Payload(Updates(f).Single()), f.Authorize);
                await f.Controller.Handle("clearDownloads", null);
                Expect(Data.Map(Data.Get(f.Store.State, "downloadReplacements")).Count == 0 && Updates(f).Count == 0 && System.IO.File.Exists(Data.Str(old, "path")), "Clear list retained update decisions or deleted the old archive.");
            }
        }
        private static async Task CleanupAndLateChanges()
        {
            using (var f = new Fixture("late-change")) {
                object old = f.Archive();
                var plan = DownloadFolderCleanup.PrepareRecordedFile(f.Folder, new[] { f.Store.Root }, old);
                System.IO.File.SetLastWriteTimeUtc(Data.Str(old, "path"), DateTime.UtcNow.AddDays(-1));
                var result = DownloadFolderCleanup.Delete(plan);
                Expect(result.Deleted == 0 && result.Skipped == 1 && System.IO.File.Exists(Data.Str(old, "path")), "A file changed after preparation was deleted.");
            }
            using (var f = new Fixture("folder-clear")) {
                f.Archive(); await f.Controller.CheckDownloadedUpdates();
                f.Controller.ClearDownloadFolder(delegate { return true; });
                Expect(Updates(f).Count == 0, "Folder cleanup retained a notification for a missing archive.");
            }
        }
    }
}
