using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
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
    // The controller runs on a real UI synchronization context. MainForm is never
    // shown, so these fixtures do not start WebView, pipes, or a real HTTP request.
    internal static class RefreshTests
    {
        private static readonly List<object> Checks = new List<object>();
        private static string root;
        private static string report;
        private static object liveResult;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            root = Path.Combine(Path.GetTempPath(), "NexusM-RefreshTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "refresh-tests.json");
            using (var host = new Form { Opacity = 0, ShowInTaskbar = false, Size = new Size(1, 1), WindowState = FormWindowState.Minimized })
            {
                host.Shown += async delegate
                {
                    try
                    {
                        await Check("cached library is immediate and does not start API work", CachedState);
                        await Check("startup checks once while cached state stays available and overlapping checks join", StartupCheck);
                        await Check("205 library entries need three compact requests instead of per-mod requests", BatchCount);
                        await Check("unordered UID responses retain reviewed state and the newest download dates", DatesAndMapping);
                        await Check("missing UIDs use saved game IDs and one lookup per unknown domain", MissingUids);
                        await Check("failed game resolution retains cached rows and does not submit stale UIDs", FailedUidResolution);
                        await Check("missing mods and failed chunks retain cached metadata while other chunks update", PartialFailure);
                        await Check("checks respect Your games only and an explicit selected game", FilterScope);
                        await Check("empty refresh does not claim a completed check", EmptyScope);
                        await Check("offline startup retains the library and resets progress without repeated initialization", OfflineStartup);
                        await Check("imports refresh automatically after initialization and remain offline before it", ImportRefresh);
                        await Check("large libraries keep network concurrency bounded at four requests", BoundedConcurrency);
                        if (args.Any(x => x == "--live")) await Check("read-only live refresh of an isolated saved-library copy", LiveRefresh);
                    }
                    catch (Exception error) { Checks.Add(Data.Obj("name", "Harness failure", "passed", false, "error", error.ToString())); }
                    finally { host.Close(); }
                };
                Application.Run(host);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            var result = Data.Obj("passed", Checks.All(x => Data.Bool(x, "passed")), "checks", Checks);
            if (liveResult != null) result["live"] = liveResult;
            File.WriteAllText(report, Data.Json.Serialize(result), new UTF8Encoding(false));
            string absolute = Path.GetFullPath(root);
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("NexusM-RefreshTests-", StringComparison.Ordinal))
            {
                try { Directory.Delete(absolute, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return Checks.All(x => Data.Bool(x, "passed")) ? 0 : 1;
        }

        private static async Task Check(string name, Func<Task> test)
        {
            try { await test(); Checks.Add(Data.Obj("name", name, "passed", true)); }
            catch (Exception error) { Checks.Add(Data.Obj("name", name, "passed", false, "error", error.ToString())); }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly Storage Store;
            internal readonly MainForm Form;
            internal readonly FakeNexus Server;
            internal Controller Controller { get { return Form.Controller; } }

            internal Fixture(string name, int count)
            {
                Store = new Storage(Path.Combine(root, name));
                Store.Games.Add(Game("fixturegame", 17));
                for (int id = 1; id <= count; id++) Store.Mods.Add(Mod("fixturegame", 17, id));
                Form = new MainForm(Store, null, null, "refresh-fixture-" + Guid.NewGuid().ToString("N"));
                Expect(Controller.Api == null, "Fixture unexpectedly loaded an account key.");
                Server = new FakeNexus();
                var api = new NexusApi("synthetic-refresh-fixture-key");
                FieldInfo field = typeof(NexusApi).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic);
                Expect(field != null, "Could not isolate the API client's network handler.");
                ((HttpClient)field.GetValue(api)).Dispose();
                field.SetValue(api, new HttpClient(Server) { Timeout = TimeSpan.FromSeconds(5) });
                Controller.Api = api;
            }

            public void Dispose() { Server.Release(); Controller.Dispose(); Form.Dispose(); }
        }

        private sealed class FakeNexus : HttpMessageHandler
        {
            private readonly object sync = new object();
            internal readonly List<long[]> Batches = new List<long[]>();
            internal readonly List<string> GameLookups = new List<string>();
            internal readonly List<string> Requests = new List<string>();
            internal readonly Dictionary<string, long> GameIds = new Dictionary<string, long> { { "fixturegame", 17 }, { "othergame", 23 }, { "unknowngame", 31 } };
            internal readonly HashSet<long> Omitted = new HashSet<long>();
            internal readonly Dictionary<long, object> Downloaded = new Dictionary<long, object>();
            internal long FailUid;
            internal bool FailValidation;
            internal int MaxConcurrent, Active, ValidateCount, FavouriteCount;
            internal TaskCompletionSource<bool> ValidationGate;

            internal void Release() { if (ValidationGate != null) ValidationGate.TrySetResult(true); }
            internal int BatchCount { get { lock (sync) return Batches.Count; } }
            internal int RequestCount { get { lock (sync) return Requests.Count; } }
            internal long[] RequestedUids { get { lock (sync) return Batches.SelectMany(x => x).ToArray(); } }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                string path = request.RequestUri.AbsolutePath;
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
                lock (sync) { Requests.Add(path); Active++; MaxConcurrent = Math.Max(MaxConcurrent, Active); }
                try
                {
                    await Task.Delay(20, token);
                    if (path.EndsWith("users/validate.json", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref ValidateCount);
                        if (ValidationGate != null) await ValidationGate.Task;
                        return FailValidation ? Response(HttpStatusCode.Unauthorized, Data.Obj("message", "fixture offline")) :
                            Response(HttpStatusCode.OK, Data.Obj("user_id", 7, "name", "Fixture user", "is_premium", false));
                    }
                    if (path == "/v2/graphql")
                    {
                        var payload = JsonTools.Dict(JsonTools.Serializer().DeserializeObject(body));
                        string query = Data.Str(payload, "query");
                        if (query.Contains("favouriteGames"))
                        {
                            Interlocked.Increment(ref FavouriteCount);
                            return Response(HttpStatusCode.OK, Data.Obj("data", Data.Obj("favouriteGames", new[] { Data.Obj("id", 17, "domainName", "fixturegame", "name", "Fixture Game", "artworkSchema", "V1") })));
                        }
                        if (query.Contains("modsByUid"))
                        {
                            long[] uids = Data.List(Data.Get(Data.Get(payload, "variables"), "uids")).Select(x => Convert.ToInt64(x)).ToArray();
                            lock (sync) Batches.Add(uids);
                            if (uids.Contains(FailUid)) return Response(HttpStatusCode.NotFound, Data.Obj("message", "fixture inaccessible batch"));
                            long[] available = uids.Where(x => !Omitted.Contains(x)).Reverse().ToArray();
                            int offset = (int)Data.Num(Data.Get(payload, "variables"), "offset");
                            var nodes = available.Skip(offset).Take(80).Select(Node).ToArray();
                            return Response(HttpStatusCode.OK, Data.Obj("data", Data.Obj("modsByUid", Data.Obj("nodes", nodes, "totalCount", available.Length))));
                        }
                    }
                    if (path.StartsWith("/v1/games/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal) && !path.Contains("/mods/"))
                    {
                        string domain = path.Substring("/v1/games/".Length).Replace(".json", "");
                        lock (sync) GameLookups.Add(domain);
                        long id;
                        if (!GameIds.TryGetValue(domain, out id)) return Response(HttpStatusCode.NotFound, Data.Obj("message", "unknown fixture domain"));
                        return Response(HttpStatusCode.OK, Data.Obj("id", id, "domain_name", domain, "name", domain));
                    }
                    throw new InvalidOperationException("Unexpected fixture request: " + path);
                }
                finally { lock (sync) Active--; }
            }

            private object Node(long uid)
            {
                long gameId = uid >> 32, modId = uid & UInt32.MaxValue;
                object downloaded;
                Downloaded.TryGetValue(uid, out downloaded);
                return Data.Obj("uid", uid.ToString(), "modId", modId, "gameId", gameId,
                    "name", "Updated " + modId, "summary", "Summary " + modId, "version", "2." + modId,
                    "author", "Fixture Author", "pictureUrl", "https://images.nexusmods.com/fixture-" + modId + ".jpg",
                    "status", "published", "updatedAt", "2026-10-19T00:00:00Z", "viewerDownloaded", downloaded, "viewerUpdateAvailable", downloaded != null);
            }

            private static HttpResponseMessage Response(HttpStatusCode code, object value)
            {
                return new HttpResponseMessage(code) { Content = new StringContent(JsonTools.Serializer().Serialize(value), Encoding.UTF8, "application/json") };
            }
        }

        private static Dictionary<string, object> Game(string domain, long id) { return Data.Obj("id", id, "domain", domain, "name", domain); }
        private static long Uid(long game, long mod) { return (game << 32) | mod; }
        private static Dictionary<string, object> Mod(string domain, long game, long id)
        {
            return Data.Obj("gameDomain", domain, "modId", id, "uid", Uid(game, id), "name", "Cached " + id,
                "version", "1.0", "summary", "Cached description", "updatedAt", 1000, "lastDownloadedAt", 900, "reviewedAt", 950, "error", "Stale fixture error");
        }
        private static object Progress(Fixture fixture) { return Data.Get(fixture.Store.State, "refreshProgress"); }
        private static async Task Refresh(Fixture fixture, string domain = "") { await fixture.Controller.Handle("refresh", Data.Obj("gameDomain", domain)); }
        private static void Expect(bool value, string message) { if (!value) throw new Exception(message); }

        private static async Task CachedState()
        {
            using (var fixture = new Fixture("cached", 2))
            {
                fixture.Store.State["lastCheckedAt"] = 1234;
                Task<object> get = fixture.Controller.Handle("getState", null);
                Expect(get.IsCompleted, "Cached getState waited for the network.");
                object state = await get;
                Expect(fixture.Server.RequestCount == 0, "Reading cached state made a network request.");
                Expect(Data.List(Data.Get(state, "mods")).Count == 2, "Cached mods were not immediately available.");
                Expect(Data.Num(state, "lastCheckedAt") == 1234, "Reading cached state changed the last check date.");
            }
        }

        private static async Task StartupCheck()
        {
            using (var fixture = new Fixture("startup", 3))
            {
                fixture.Server.ValidationGate = new TaskCompletionSource<bool>();
                Task<object> first = fixture.Controller.Handle("initialize", null);
                await Until(delegate { return fixture.Server.ValidateCount == 1; });
                Expect(!first.IsCompleted, "Initialization unexpectedly completed with validation blocked.");
                Expect(fixture.Controller.Handle("getState", null).IsCompleted, "Startup blocked cached library rendering.");
                Expect(Data.Bool(Progress(fixture), "running"), "Startup did not publish running progress.");
                Task<object> second = fixture.Controller.Handle("initialize", null);
                Task<object> manual = fixture.Controller.Handle("refresh", null);
                fixture.Server.Release();
                await Task.WhenAll(first, second, manual);
                Expect(fixture.Server.ValidateCount == 1 && fixture.Server.FavouriteCount == 1, "Overlapping startup repeated account/game requests.");
                Expect(fixture.Server.BatchCount == 1, "Overlapping startup and manual checks duplicated the library request.");
                Expect(fixture.Store.Mods.All(x => Data.Str(x, "name").StartsWith("Updated ", StringComparison.Ordinal)), "Startup did not update cached mods.");
                Expect(!Data.Bool(Progress(fixture), "running"), "Completed startup retained a busy progress state.");
                int requests = fixture.Server.RequestCount;
                await fixture.Controller.Handle("initialize", null);
                Expect(fixture.Server.RequestCount == requests, "Repeated initialize started another update check.");
            }
        }

        private static async Task BatchCount()
        {
            using (var fixture = new Fixture("batch-count", 205))
            {
                await Refresh(fixture);
                Expect(fixture.Server.BatchCount == 3, "205 entries used " + fixture.Server.BatchCount + " metadata requests rather than 3.");
                Expect(fixture.Server.RequestCount == 3, "A list refresh still made per-mod or extra download-date requests.");
                Expect(fixture.Server.Batches.All(x => x.Length > 0 && x.Length <= 80), "A batch exceeded the server's 80 UID page size.");
                Expect(fixture.Server.RequestedUids.Distinct().Count() == 205, "Not every mod UID was checked exactly once.");
                Expect(fixture.Store.Mods.All(x => Data.Str(x, "name") == "Updated " + Data.Num(x, "modId")), "An unordered response updated the wrong rows.");
                Expect(Data.Num(Progress(fixture), "done") == 205 && Data.Num(Progress(fixture), "total") == 205, "Final progress did not account for every selected mod.");
                Expect(Data.Num(fixture.Store.State, "lastCheckedAt") > 1234, "A completed nonempty check has no date.");
            }
        }

        private static async Task DatesAndMapping()
        {
            using (var fixture = new Fixture("date-mapping", 3))
            {
                var first = Data.Map(fixture.Store.Mods[0]);
                first["lastDownloadedAt"] = Data.Timestamp("2026-10-20T00:00:00Z");
                first["reviewedAt"] = Data.Timestamp("2026-10-20T01:00:00Z");
                fixture.Server.Downloaded[Uid(17, 1)] = "2026-10-18T00:00:00Z";
                fixture.Server.Downloaded[Uid(17, 2)] = "2026-10-18T00:00:00Z";
                await Refresh(fixture);
                Expect(Data.Num(first, "lastDownloadedAt") == Data.Timestamp("2026-10-20T00:00:00Z"), "An older account download date rolled back a local download.");
                Expect(Data.Num(first, "reviewedAt") == Data.Timestamp("2026-10-20T01:00:00Z") && !Data.Bool(first, "needsReview"), "A reviewed update became unreviewed.");
                var second = Data.Map(fixture.Store.Mods[1]);
                Expect(Data.Num(second, "lastDownloadedAt") == Data.Timestamp("2026-10-18T00:00:00Z"), "The newer account download date was not merged.");
                Expect(Data.Bool(second, "needsReview"), "A newer mod update was not marked for review.");
                Expect(Data.Num(fixture.Store.Mods[2], "lastDownloadedAt") == 900, "A missing account download date erased saved history.");
                Expect(Data.Str(second, "version") == "2.2" && Data.Str(second, "summary") == "Summary 2", "Compact metadata fields were not updated.");
                Expect(Data.Str(second, "error") == "", "A successful row retained an obsolete request error.");
            }
        }

        private static async Task MissingUids()
        {
            using (var fixture = new Fixture("missing-uids", 2))
            {
                fixture.Store.Settings["yourGamesOnly"] = false;
                foreach (var mod in fixture.Store.Mods) Data.Map(mod).Remove("uid");
                var other1 = Mod("unknowngame", 31, 77); other1.Remove("uid"); fixture.Store.Mods.Add(other1);
                var other2 = Mod("unknowngame", 31, 78); other2.Remove("uid"); fixture.Store.Mods.Add(other2);
                await Refresh(fixture);
                Expect(fixture.Server.GameLookups.SequenceEqual(new[] { "unknowngame" }), "Missing UIDs caused repeated or unnecessary game lookups.");
                Expect(fixture.Server.BatchCount == 1 && fixture.Server.RequestedUids.Length == 4, "Resolved IDs were not checked in the same compact batch.");
                Expect(Data.Num(fixture.Store.Mods[0], "uid") == Uid(17, 1) && Data.Num(other2, "uid") == Uid(31, 78), "A UID was derived from the wrong game/mod ID.");
                Expect(Data.Str(other1, "name") == "Updated 77", "Resolved unknown-domain metadata was not fetched.");
            }
        }

        private static async Task PartialFailure()
        {
            using (var fixture = new Fixture("partial-failure", 205))
            {
                fixture.Server.Omitted.Add(Uid(17, 1));
                fixture.Server.FailUid = Uid(17, 101);
                await Refresh(fixture);
                var missing = fixture.Store.Mods[0];
                var failed = fixture.Store.Mods[100];
                Expect(Data.Str(missing, "name") == "Cached 1" && Data.Num(missing, "lastDownloadedAt") == 900, "A missing API row erased its cache or dates.");
                Expect(Data.Str(missing, "error").Length > 0, "A missing mod was not identified as unavailable to check.");
                Expect(Data.Str(failed, "name") == "Cached 101" && Data.Str(failed, "version") == "1.0" && Data.Num(failed, "reviewedAt") == 950, "A failed chunk overwrote prior metadata or review state.");
                Expect(Data.Str(failed, "error").Length > 0, "A failed chunk was not identified to the user.");
                Expect(Data.Str(fixture.Store.Mods[1], "name") == "Updated 2" && Data.Str(fixture.Store.Mods[204], "name") == "Updated 205", "A failed chunk prevented independent chunks from updating.");
                Expect(!Data.Bool(Progress(fixture), "running") && Data.Num(Progress(fixture), "done") == 205, "Partial failure left the check busy or its progress incomplete.");
                Expect(Data.Str(fixture.Store.State, "notice").Contains("kept"), "Partial failure did not explain that previous metadata was retained.");
            }
        }

        private static async Task FailedUidResolution()
        {
            using (var fixture = new Fixture("failed-game-id", 0))
            {
                fixture.Store.Settings["yourGamesOnly"] = false;
                var mod = Mod("brokenworld", 31, 77);
                mod["uid"] = 11; // Does not identify this mod, so its game must be resolved again.
                fixture.Store.Mods.Add(mod);
                await Refresh(fixture);
                Expect(fixture.Server.GameLookups.SequenceEqual(new[] { "brokenworld" }), "The unresolved game's lookup was repeated.");
                Expect(fixture.Server.BatchCount == 0, "A failed game lookup still submitted a stale UID to the mod API.");
                Expect(Data.Str(mod, "name") == "Cached 77" && Data.Num(mod, "lastDownloadedAt") == 900 && Data.Num(mod, "reviewedAt") == 950, "Game lookup failure changed saved metadata or dates.");
                Expect(Data.Str(mod, "error").Length > 0 && !Data.Bool(Progress(fixture), "running"), "Game lookup failure did not leave an actionable, idle state.");
                Expect(Data.Num(fixture.Store.State, "lastCheckedAt") > 0, "An attempted nonempty refresh did not record its check date.");
            }
        }

        private static async Task FilterScope()
        {
            using (var fixture = new Fixture("scope", 1))
            {
                fixture.Store.Mods.Add(Mod("othergame", 23, 8));
                await Refresh(fixture);
                Expect(fixture.Server.RequestedUids.SequenceEqual(new[] { Uid(17, 1) }), "Your games only checked a removed game's mod.");
                Expect(Data.Str(fixture.Store.Mods[1], "name") == "Cached 8", "An out-of-scope row was changed.");
                fixture.Store.Settings["yourGamesOnly"] = false;
                await Refresh(fixture);
                Expect(fixture.Server.Batches[1].Length == 2, "All-games refresh excluded an imported game's mod.");
                fixture.Store.Settings["yourGamesOnly"] = true;
                await Refresh(fixture, "othergame");
                Expect(fixture.Server.Batches[2].SequenceEqual(new[] { Uid(23, 8) }), "An explicit game refresh did not limit its scope to that game.");
            }
        }

        private static async Task EmptyScope()
        {
            using (var fixture = new Fixture("empty", 0))
            {
                fixture.Store.State["lastCheckedAt"] = 1234;
                await Refresh(fixture);
                Expect(fixture.Server.RequestCount == 0, "An empty library made a metadata request.");
                Expect(Data.Num(fixture.Store.State, "lastCheckedAt") == 1234, "An empty refresh claimed to have checked the library.");
                Expect(!Data.Bool(Progress(fixture), "running"), "An empty refresh left a busy progress state.");
            }
        }

        private static async Task OfflineStartup()
        {
            using (var fixture = new Fixture("offline", 2))
            {
                fixture.Server.FailValidation = true;
                await fixture.Controller.Handle("initialize", null);
                Expect(fixture.Store.Mods.Count == 2 && Data.Str(fixture.Store.Mods[0], "name") == "Cached 1", "Offline startup lost the cached library.");
                Expect(!Data.Bool(Progress(fixture), "running"), "Failed startup left the app busy.");
                Expect(Data.Str(fixture.Store.State, "notice").IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0, "Offline startup did not explain the saved library was retained.");
                int requests = fixture.Server.RequestCount;
                await fixture.Controller.Handle("initialize", null);
                Expect(fixture.Server.RequestCount == requests, "Offline initialization repeated automatically on another initialize call.");
                Expect(fixture.Controller.Handle("getState", null).IsCompleted, "Offline cached state was not immediately available.");
            }
        }

        private static async Task ImportRefresh()
        {
            using (var fixture = new Fixture("import", 0))
            {
                await fixture.Controller.ImportHistory(new[] { Data.Obj("gameDomain", "fixturegame", "modId", 1, "name", "Imported before startup", "lastDownloadedAt", 1111) });
                Expect(fixture.Server.RequestCount == 0, "An import before initialization unexpectedly made API requests.");
                await fixture.Controller.Handle("initialize", null);
                Expect(fixture.Server.BatchCount == 1, "Startup did not refresh the imported library.");
                await fixture.Controller.ImportHistory(new[] { Data.Obj("gameDomain", "fixturegame", "modId", 2, "name", "Imported after startup", "lastDownloadedAt", 2222) });
                await Until(delegate { return fixture.Server.BatchCount == 2 && !Data.Bool(Progress(fixture), "running"); });
                var imported = fixture.Store.Mods.First(x => Data.Num(x, "modId") == 2);
                Expect(Data.Str(imported, "name") == "Updated 2", "Import after initialization required a manual check to load current metadata.");
                Expect(Data.Num(imported, "lastDownloadedAt") == 2222, "Automatic import refresh erased the imported download date.");
            }
        }

        private static async Task BoundedConcurrency()
        {
            using (var fixture = new Fixture("concurrency", 905))
            {
                await Refresh(fixture);
                Expect(fixture.Server.BatchCount == 12, "905 mods were not split into twelve requests.");
                Expect(fixture.Server.MaxConcurrent > 0 && fixture.Server.MaxConcurrent <= 4, "Network concurrency exceeded four requests: " + fixture.Server.MaxConcurrent + ".");
                Expect(fixture.Store.Mods.All(x => Data.Str(x, "name").StartsWith("Updated ", StringComparison.Ordinal)), "A queued chunk was lost under concurrency.");
            }
        }

        private static async Task Until(Func<bool> complete)
        {
            for (int attempt = 0; attempt < 200; attempt++) { if (complete()) return; await Task.Delay(10); }
            throw new TimeoutException("The fixture did not reach the expected asynchronous state.");
        }

        private static async Task LiveRefresh()
        {
            // Only explicit --live reads the account's real state. It is never saved
            // or passed to the controller; all refresh writes go to this clone.
            var source = new Storage();
            string folder = Path.Combine(root, "live-library-clone");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "state.json"), Data.Json.Serialize(source.State), new UTF8Encoding(false));
            var clone = new Storage(folder);
            string key = source.ReadKey();
            Expect(!String.IsNullOrWhiteSpace(key), "The optional live check needs the app's locally stored API key.");
            clone.WriteKey(key);
            clone.Settings["yourGamesOnly"] = true;
            var domains = new HashSet<string>(clone.Games.Select(x => Data.Str(x, "domain")));
            var selected = clone.Mods.Where(x => domains.Contains(Data.Str(x, "gameDomain"))).ToArray();
            foreach (object mod in selected) Data.Map(mod)["lastCheckedAt"] = 0;
            Expect(selected.Length > 0, "The optional live check needs a saved mod library for Your games.");
            using (var form = new MainForm(clone, null, null, "live-refresh-clone-" + Guid.NewGuid().ToString("N")))
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    await form.Controller.Handle("refresh", null);
                    watch.Stop();
                    int successful = selected.Count(x => Data.Num(x, "lastCheckedAt") > 0 && Data.Str(x, "error") == "");
                    int missing = selected.Count(x => Data.Str(x, "error").StartsWith("Nexus did not return this mod.", StringComparison.Ordinal));
                    liveResult = Data.Obj("elapsedMilliseconds", watch.ElapsedMilliseconds, "selectedMods", selected.Length,
                        "successfulMods", successful, "failedMods", selected.Length - successful, "unavailableMods", missing, "yourGames", clone.Games.Count,
                        "originalStateModified", false);
                    Expect(successful > 0 && successful + missing == selected.Length, "The live check refreshed " + successful + " of " + selected.Length + " selected mods, with " + missing + " unavailable and the rest failing their requests.");
                    Expect(!Data.Bool(Data.Get(clone.State, "refreshProgress"), "running"), "The live check left progress running.");
                }
                finally { form.Controller.Dispose(); }
            }
        }
    }
}
