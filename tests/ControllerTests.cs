using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NexusM
{
    internal static class ControllerTests
    {
        private static readonly List<object> Checks = new List<object>();
        private static string root;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            root = Path.Combine(Path.GetTempPath(), "NexusM-ControllerTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "controller-tests.json");
            try
            {
                Check("backup restores game and mod metadata, dates, review state, and local game ownership", TestRestore);
                Check("successful file history survives restore independently from the queue", TestFileHistory);
                Check("import merges newer download and reviewed dates without rollback or duplicates", TestDateMerge);
                Check("invalid imported domains and IDs are ignored, valid domains are normalized", TestInvalidImports);
                Check("incomplete transfers recover as interrupted without corrupting finished records", TestInterruptedStartup);
                Check("malformed settings and state recover with a safe default download folder", TestMalformedStorage);
                Check("DPAPI credential stays outside public state, disk state, and exported backup", TestCredentialIsolation);
                Check("saved library and per-file download history round trip without a key or network", TestOfflineRoundTrip);
                Check("a valid empty-library backup restores game choices and exclusions", TestEmptyBackup);
                Check("clearing downloads removes inactive and waiting rows while retaining transfers, files, and mod dates", TestClearDownloads);
                Check("clearing downloads preserves the newest successful file history, including legacy queue-only rows", TestClearDownloadHistory);
                Check("clearing an empty download list is safe and idempotent", TestClearEmptyDownloads);
                Check("Your games filter defaults on, persists boolean changes, and rejects malformed payloads safely", TestYourGamesSetting);
            }
            catch (Exception error) { Checks.Add(Data.Obj("name", "Harness failure", "passed", false, "error", error.ToString())); }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(report));
                File.WriteAllText(report, Data.Json.Serialize(Data.Obj("passed", Checks.All(item => Data.Bool(item, "passed")), "checks", Checks)), new UTF8Encoding(false));
                string absolute = Path.GetFullPath(root);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("NexusM-ControllerTests-", StringComparison.Ordinal))
                {
                    try { Directory.Delete(absolute, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            return Checks.All(item => Data.Bool(item, "passed")) ? 0 : 1;
        }

        private static void Check(string name, Action test)
        {
            try { test(); Checks.Add(Data.Obj("name", name, "passed", true)); }
            catch (Exception error) { Checks.Add(Data.Obj("name", name, "passed", false, "error", error.Message)); }
        }

        private static Storage NewStore(string name)
        {
            return new Storage(Path.Combine(root, name));
        }

        private static void WithController(Storage storage, Action<Controller> test)
        {
            // Never Show or run this form: no WebView initialization, IPC, or network.
            using (MainForm form = new MainForm(storage, null, null, "controller-fixture"))
            {
                try { Expect(form.Controller.Api == null, "Isolated fixture unexpectedly has an API client."); test(form.Controller); }
                finally { form.Controller.Dispose(); }
            }
        }

        private static void Import(Controller controller, object value)
        {
            controller.Handle("importData", Data.Obj("text", Data.Json.Serialize(value))).GetAwaiter().GetResult();
        }

        private static object Fixture()
        {
            return Data.Obj("schemaVersion", 1,
                "games", new[] { Data.Obj("id", 3474, "domain", "baldursgate3", "name", "Baldur's Gate 3", "imageUrl", "https://images.nexusmods.com/game.jpg", "localOnly", true) },
                "mods", new[] {
                    Data.Obj("gameDomain", "baldursgate3", "modId", 5508, "name", "Custom Difficulty Tweaks", "version", "2.2.3", "author", "Mod Author", "summary", "Difficulty settings", "pictureUrl", "https://images.nexusmods.com/mod.jpg", "lastDownloadedAt", 1792100000, "reviewedAt", 1792000000, "updatedAt", 1792200000),
                    Data.Obj("gameDomain", "baldursgate3", "modId", 5509, "name", "Optional Translation", "version", "2.2.1", "lastDownloadedAt", 1792100000, "reviewedAt", 1792300000, "updatedAt", 1792200000)
                },
                "downloads", new[] {
                    Download(42, "completed", 1792100000, "2.2.2"),
                    Download(77, "waiting-authorization", 0, "2.2.3")
                },
                "fileDownloads", Data.Obj("baldursgate3:5508:42", Download(42, "completed", 1792200000, "2.2.3"), "baldursgate3:5508:99", Download(99, "completed", 1792150000, "2.2.1")));
        }

        private static Dictionary<string, object> Download(int fileId, string status, long date, string version)
        {
            return Data.Obj("id", "baldursgate3:5508:" + fileId, "gameDomain", "baldursgate3", "modId", 5508, "fileId", fileId,
                "name", "File " + fileId, "filename", "fixture-" + fileId + ".zip", "version", version,
                "status", status, "downloadedAt", date, "downloadedBytes", 1024, "totalBytes", 1024,
                "path", Path.Combine(root, "archives", "fixture-" + fileId + ".zip"));
        }

        private static void TestRestore()
        {
            Storage storage = NewStore("restore");
            WithController(storage, delegate(Controller controller)
            {
                Import(controller, Fixture());
                Expect(storage.Games.Count == 1 && storage.Mods.Count == 2, "The backup's game/mod counts were not restored.");
                object game = storage.Games[0], mod = storage.Mods[0];
                Expect(Data.Str(game, "name") == "Baldur's Gate 3" && Data.Num(game, "id") == 3474, "Game metadata was lost.");
                Expect(Data.Bool(game, "localOnly"), "A manually added game's localOnly flag was lost.");
                Expect(Data.Str(mod, "name") == "Custom Difficulty Tweaks" && Data.Str(mod, "version") == "2.2.3", "Mod name/version was lost.");
                Expect(Data.Str(mod, "author") == "Mod Author" && Data.Str(mod, "pictureUrl") == "https://images.nexusmods.com/mod.jpg", "Mod metadata was lost.");
                Expect(Data.Num(mod, "lastDownloadedAt") == 1792100000 && Data.Num(mod, "reviewedAt") == 1792000000, "Downloaded/review dates were lost.");
                Expect(Data.Bool(mod, "needsReview"), "An update after the downloaded/reviewed date should require review.");
                Expect(!Data.Bool(storage.Mods[1], "needsReview"), "A previously reviewed update should remain reviewed.");
                Expect(controller.Api == null, "A metadata backup import should work offline.");
            });
        }

        private static void TestFileHistory()
        {
            Storage storage = NewStore("file-history");
            WithController(storage, delegate(Controller controller)
            {
                Import(controller, Fixture());
                object files = Data.Get(storage.State, "fileDownloads");
                Expect(Data.Map(files).Count == 2, "Completed file history was not restored.");
                object main = Data.Get(files, "baldursgate3:5508:42"), optional = Data.Get(files, "baldursgate3:5508:99");
                Expect(Data.Num(main, "downloadedAt") == 1792200000 && Data.Str(main, "version") == "2.2.3", "The newest successful file record was not retained.");
                Expect(Data.Num(optional, "fileId") == 99 && Data.Str(optional, "status") == "completed", "An optional file absent from the queue was lost.");
                Expect(Data.Get(files, "baldursgate3:5508:77") == null, "An unfinished download was recorded as successful.");
                storage.Downloads.Clear(); storage.Save();
                Storage reopened = new Storage(storage.Root);
                Expect(Data.Map(Data.Get(reopened.State, "fileDownloads")).Count == 2, "Clearing the queue also cleared successful per-file history.");
            });
        }

        private static void TestDateMerge()
        {
            Storage storage = NewStore("date-merge");
            WithController(storage, delegate(Controller controller)
            {
                Import(controller, Fixture());
                Import(controller, new[] { Data.Obj("gameDomain", "baldursgate3", "modId", 5508, "name", "Custom Difficulty Tweaks", "lastDownloadedUtc", "2026-10-01T00:00:00Z", "reviewedAt", 1791000000) });
                Expect(storage.Mods.Count == 2, "A repeat import duplicated a mod.");
                Expect(Data.Num(storage.Mods[0], "lastDownloadedAt") == 1792100000, "An older history import rolled the download date back.");
                Expect(Data.Num(storage.Mods[0], "reviewedAt") == 1792000000, "An older backup rolled the reviewed date back.");
                Import(controller, new[] { Data.Obj("gameDomain", "baldursgate3", "modId", 5508, "lastDownloadedUtc", "2026-10-20T12:00:00Z", "updatedUtc", "2026-10-19T00:00:00Z") });
                Expect(Data.Num(storage.Mods[0], "lastDownloadedAt") == 1792497600, "ISO history dates were not imported.");
                Expect(!Data.Bool(storage.Mods[0], "needsReview"), "Downloaded update remained flagged for review.");
            });
        }

        private static void TestInvalidImports()
        {
            Storage storage = NewStore("invalid-imports");
            WithController(storage, delegate(Controller controller)
            {
                Import(controller, new[] {
                    Data.Obj("gameDomain", "../outside", "modId", 1),
                    Data.Obj("gameDomain", "https://evil.invalid", "modId", 2),
                    Data.Obj("gameDomain", new string('a', 101), "modId", 3),
                    Data.Obj("gameDomain", "baldursgate3", "modId", 0),
                    Data.Obj("gameDomain", "baldursgate3", "modId", -1),
                    Data.Obj("gameDomain", "baldursgate3", "modId", "999999999999999999999999"),
                    Data.Obj("gameDomain", "BALDURSGATE3", "modId", 123, "name", "Valid import")
                });
                Expect(storage.Mods.Count == 1, "Invalid domain/ID records were accepted.");
                Expect(Data.Str(storage.Mods[0], "gameDomain") == "baldursgate3", "Valid game domain was not normalized.");
                Import(controller, new[] { Data.Obj("gameDomain", "baldursgate3", "modId", 123, "lastDownloadedAt", 1792000000) });
                Expect(storage.Mods.Count == 1, "Case variation duplicated a mod.");
            });
        }

        private static void TestInterruptedStartup()
        {
            string folder = Path.Combine(root, "interrupted"); Directory.CreateDirectory(folder);
            string[] states = { "downloading", "queued", "authorizing", "retrying", "waiting-authorization", "completed", "failed", "cancelled", "interrupted" };
            File.WriteAllText(Path.Combine(folder, "state.json"), Data.Json.Serialize(Data.Obj("downloads", states.Select((status, i) => Data.Obj("id", i.ToString(), "status", status)).ToArray())));
            Storage storage = new Storage(folder);
            for (int i = 0; i < states.Length; i++)
            {
                string expected = i < 5 ? "interrupted" : states[i];
                Expect(Data.Str(storage.Downloads[i], "status") == expected, "Startup status recovery failed for " + states[i] + ".");
                if (i < 5) Expect(Data.Str(storage.Downloads[i], "error").Length > 0, "Interrupted transfer has no recovery explanation.");
            }
            Expect(!Data.Bool(storage.State, "connected"), "Startup should begin disconnected until authentication succeeds.");
        }

        private static void TestMalformedStorage()
        {
            string[] fixtures = { "not JSON", "[]", "null", "{\"settings\":\"unexpected\"}", "{\"settings\":[1,2],\"games\":false,\"mods\":123,\"downloads\":null}", "{\"settings\":{\"downloadFolder\":\" \"}}", "{\"settings\":{\"downloadFolder\":123}}" };
            for (int i = 0; i < fixtures.Length; i++)
            {
                string folder = Path.Combine(root, "malformed-" + i); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "state.json"), fixtures[i]);
                Storage storage = new Storage(folder);
                object downloadFolder = Data.Get(storage.Settings, "downloadFolder");
                Expect(downloadFolder is string && Path.IsPathRooted((string)downloadFolder), "Malformed settings did not recover an absolute download folder for fixture " + i + ".");
                Expect(storage.Games != null && storage.Mods != null && storage.Downloads != null, "Malformed collections were not initialized.");
                storage.Save();
                Expect(File.Exists(Path.Combine(folder, "state.json")), "Recovered state could not be saved.");
            }
        }

        private static void TestCredentialIsolation()
        {
            const string fixtureCredential = "Fixture-only credential for DPAPI storage checks";
            Storage storage = NewStore("credential-isolation");
            WithController(storage, delegate(Controller controller)
            {
                Import(controller, Fixture());
                storage.WriteKey(fixtureCredential);
                Expect(storage.ReadKey() == fixtureCredential, "Windows-encrypted credential did not round trip.");
                Expect(Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(storage.Root, "api-key.bin"))).IndexOf(fixtureCredential, StringComparison.Ordinal) < 0, "Credential was stored as plaintext.");
                string publicJson = Data.Json.Serialize(storage.PublicState());
                Expect(Data.Bool(storage.Settings, "hasApiKey"), "The UI should receive the credential-presence flag.");
                AssertNoCredential(publicJson, fixtureCredential, "public state");
                storage.Save();
                AssertNoCredential(File.ReadAllText(Path.Combine(storage.Root, "state.json")), fixtureCredential, "disk state");
                MethodInfo export = typeof(Controller).GetMethod("ExportData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Expect(export != null, "Expose the actual ExportData projection to verify backup credential isolation headlessly.");
                string backup = Data.Json.Serialize(export.Invoke(controller, null));
                AssertNoCredential(backup, fixtureCredential, "exported backup");
                Expect(Data.List(Data.Get(Data.Json.DeserializeObject(backup), "mods")).Count == 2, "Export projection does not contain the library.");
                storage.WriteKey("");
                Expect(storage.ReadKey() == "" && !File.Exists(Path.Combine(storage.Root, "api-key.bin")), "Credential removal failed.");
            });
        }

        private static void TestOfflineRoundTrip()
        {
            Storage storage = NewStore("offline-roundtrip");
            WithController(storage, delegate(Controller controller) { Import(controller, Fixture()); });
            Storage restored = new Storage(storage.Root);
            WithController(restored, delegate(Controller controller)
            {
                object state = controller.Handle("getState", null).GetAwaiter().GetResult();
                Expect(!Data.Bool(state, "connected") && !Data.Bool(restored.Settings, "hasApiKey"), "Offline library should not appear authenticated.");
                Expect(Data.List(Data.Get(state, "mods")).Count == 2 && Data.List(Data.Get(state, "games")).Count == 1, "Offline library was not retained.");
                Expect(Data.Map(Data.Get(state, "fileDownloads")).Count == 2, "Per-file history was not retained after restart.");
                Expect(controller.Api == null, "Offline state created an unexpected API client.");
            });
        }

        private static void TestEmptyBackup()
        {
            Storage storage = NewStore("empty-backup");
            WithController(storage, delegate(Controller controller)
            {
                Import(controller, Data.Obj("schemaVersion", 1,
                    "games", new[] { Data.Obj("id", 3474, "domain", "baldursgate3", "name", "Baldur's Gate 3", "localOnly", true) },
                    "mods", new object[0], "downloads", new object[0], "fileDownloads", Data.Obj(),
                    "excludedGames", new[] { "CYBERPUNK2077", "cyberpunk2077", "../invalid" }));
                Expect(storage.Games.Count == 1 && storage.Mods.Count == 0, "An empty-library backup was not restored.");
                List<object> excluded = Data.List(Data.Get(storage.State, "excludedGames"));
                Expect(excluded.Count == 1 && Convert.ToString(excluded[0]) == "cyberpunk2077", "Game exclusions were not validated and normalized.");
                object export = typeof(Controller).GetMethod("ExportData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(controller, null);
                Expect(Data.List(Data.Get(export, "excludedGames")).Count == 1, "Game exclusions were lost from export.");
            });
        }

        private static void TestClearDownloads()
        {
            Storage storage = NewStore("clear-downloads");
            string files = Path.Combine(storage.Root, "archives"); Directory.CreateDirectory(files);
            string archive = Path.Combine(files, "finished.zip"), partial = Path.Combine(files, "unfinished.zip.part");
            byte[] archiveBytes = { 80, 75, 3, 4, 17 }, partialBytes = { 80, 75, 3 };
            File.WriteAllBytes(archive, archiveBytes); File.WriteAllBytes(partial, partialBytes);
            storage.Mods.Add(Data.Obj("gameDomain", "baldursgate3", "modId", 5508, "lastDownloadedAt", 1792100000, "reviewedAt", 1792000000, "updatedAt", 1792200000, "needsReview", true));
            string modBefore = Data.Json.Serialize(storage.Mods);
            string[] states = { "completed", "failed", "cancelled", "interrupted", "waiting-authorization", "queued", "authorizing", "retrying", "downloading", "failed" };
            for (int i = 0; i < states.Length; i++)
            {
                var entry = Download(100 + i, states[i], i == 0 ? 1792100000 : 0, "1.0");
                entry["path"] = i == 0 ? archive : partial;
                storage.Downloads.Add(entry);
            }
            using (var active = new CancellationTokenSource())
            {
                WithController(storage, delegate(Controller controller)
                {
                    var transfers = (Dictionary<string, CancellationTokenSource>)typeof(Controller).GetField("transfers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                    transfers["baldursgate3:5508:109"] = active;
                    var pending = (HashSet<string>)typeof(Controller).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                    pending.Add("baldursgate3:5508:104");
                    object result = controller.Handle("clearDownloads", null).GetAwaiter().GetResult();
                    string[] expected = { "baldursgate3:5508:105", "baldursgate3:5508:106", "baldursgate3:5508:107", "baldursgate3:5508:108", "baldursgate3:5508:109" };
                    Expect(storage.Downloads.Select(x => Data.Str(x, "id")).OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)), "Clearing did not retain exactly the active and queued transfers.");
                    Expect(Data.List(Data.Get(result, "downloads")).Count == 5, "Clear action did not return the updated queue state.");
                    Expect(!active.IsCancellationRequested && transfers.ContainsKey("baldursgate3:5508:109"), "Clearing cancelled an active transfer.");
                    Expect(!pending.Contains("baldursgate3:5508:104"), "Cleared waiting authorization remained pending.");
                    Expect(Data.Json.Serialize(storage.Mods) == modBefore, "Clearing changed the mod's download date or review state.");
                    Expect(File.ReadAllBytes(archive).SequenceEqual(archiveBytes), "Clearing removed or changed a downloaded archive.");
                    Expect(File.ReadAllBytes(partial).SequenceEqual(partialBytes), "Clearing removed or changed a partial archive.");
                    object saved = Data.Json.DeserializeObject(File.ReadAllText(Path.Combine(storage.Root, "state.json")));
                    Expect(Data.List(Data.Get(saved, "downloads")).Count == 5, "The cleared queue was not persisted.");
                    Expect(Data.Get(Data.Get(storage.State, "fileDownloads"), "baldursgate3:5508:100") != null, "Clearing lost the successful completed file record.");
                    Expect(Data.Get(Data.Get(storage.State, "fileDownloads"), "baldursgate3:5508:101") == null, "Clearing recorded a failed transfer as a successful download.");
                });
            }
        }

        private static void TestClearDownloadHistory()
        {
            Storage storage = NewStore("clear-download-history");
            // The queue-only row represents an older app state before fileDownloads existed.
            storage.Downloads.Add(Download(77, "completed", 1792250000, "2.2.4"));
            storage.Downloads.Add(Download(42, "completed", 1792100000, "2.2.2"));
            storage.Downloads.Add(Download(99, "completed", 1792200000, "2.2.3"));
            storage.State["fileDownloads"] = Data.Obj("baldursgate3:5508:42", Download(42, "completed", 1792300000, "2.2.5"),
                "baldursgate3:5508:99", Download(99, "completed", 1792150000, "2.2.1"));
            WithController(storage, delegate(Controller controller)
            {
                controller.Handle("clearDownloads", null).GetAwaiter().GetResult();
                Expect(storage.Downloads.Count == 0, "Completed download rows were not cleared.");
                object history = Data.Get(storage.State, "fileDownloads");
                Expect(Data.Map(history).Count == 3, "Clearing did not retain all successful per-file records.");
                object legacy = Data.Get(history, "baldursgate3:5508:77"), newerHistory = Data.Get(history, "baldursgate3:5508:42"), newerQueue = Data.Get(history, "baldursgate3:5508:99");
                Expect(Data.Num(legacy, "downloadedAt") == 1792250000 && Data.Str(legacy, "version") == "2.2.4", "Queue-only completed history was lost.");
                Expect(Data.Num(newerHistory, "downloadedAt") == 1792300000 && Data.Str(newerHistory, "version") == "2.2.5", "An older queue row rolled successful file history back.");
                Expect(Data.Num(newerQueue, "downloadedAt") == 1792200000 && Data.Str(newerQueue, "version") == "2.2.3", "The newer completed queue record was not retained.");
            });
            Storage reopened = new Storage(storage.Root);
            Expect(reopened.Downloads.Count == 0 && Data.Map(Data.Get(reopened.State, "fileDownloads")).Count == 3, "Cleared queue and successful history did not survive restart.");
        }

        private static void TestClearEmptyDownloads()
        {
            Storage storage = NewStore("clear-empty-downloads");
            storage.State["fileDownloads"] = Data.Obj("baldursgate3:5508:42", Download(42, "completed", 1792200000, "2.2.3"));
            string history = Data.Json.Serialize(Data.Get(storage.State, "fileDownloads"));
            WithController(storage, delegate(Controller controller)
            {
                controller.Handle("clearDownloads", null).GetAwaiter().GetResult();
                controller.Handle("clearDownloads", Data.Obj()).GetAwaiter().GetResult();
                Expect(storage.Downloads.Count == 0, "Clearing an empty queue added a download.");
                Expect(Data.Json.Serialize(Data.Get(storage.State, "fileDownloads")) == history, "Clearing an empty queue changed successful history.");
            });
        }

        private static void TestYourGamesSetting()
        {
            Storage storage = NewStore("your-games-setting");
            Expect(Data.Bool(storage.Settings, "yourGamesOnly"), "Your games filtering should default on for a new library.");
            WithController(storage, delegate(Controller controller)
            {
                object result = controller.Handle("saveSettings", Data.Obj("yourGamesOnly", false)).GetAwaiter().GetResult();
                Expect(Data.Get(storage.Settings, "yourGamesOnly") is bool && !Data.Bool(storage.Settings, "yourGamesOnly"), "A boolean false was not saved.");
                Expect(!Data.Bool(Data.Get(result, "settings"), "yourGamesOnly"), "The returned settings did not reflect the changed filter.");
                Expect(!Data.Bool(new Storage(storage.Root).Settings, "yourGamesOnly"), "False filter setting did not survive restart.");
                controller.Handle("saveSettings", Data.Obj("yourGamesOnly", true)).GetAwaiter().GetResult();
                Expect(Data.Bool(new Storage(storage.Root).Settings, "yourGamesOnly"), "True filter setting did not survive restart.");
                object[] invalid = { null, "false", "invalid", "", 0, 1, Data.Obj("enabled", false), new object[0] };
                foreach (var value in invalid)
                {
                    bool rejected = false;
                    try { controller.Handle("saveSettings", Data.Obj("yourGamesOnly", value)).GetAwaiter().GetResult(); }
                    catch (ArgumentException) { rejected = true; }
                    Expect(rejected, "A malformed filter setting was not rejected.");
                    Expect(Data.Get(storage.Settings, "yourGamesOnly") is bool && Data.Bool(storage.Settings, "yourGamesOnly"), "A malformed filter setting disabled the filter.");
                }
                controller.Handle("saveSettings", null).GetAwaiter().GetResult();
                Expect(Data.Bool(storage.Settings, "yourGamesOnly"), "An unrelated settings save disabled the filter.");
                Expect(controller.Api == null, "Changing the filter unexpectedly initialized an API client.");
            });
            string malformedFolder = Path.Combine(root, "your-games-setting-malformed"); Directory.CreateDirectory(malformedFolder);
            File.WriteAllText(Path.Combine(malformedFolder, "state.json"), "{\"settings\":{\"yourGamesOnly\":\"false\"}}");
            Storage recovered = new Storage(malformedFolder);
            Expect(Data.Get(recovered.Settings, "yourGamesOnly") is bool && Data.Bool(recovered.Settings, "yourGamesOnly"), "A malformed disk setting did not recover the default filter.");
        }

        private static void AssertNoCredential(string text, string credential, string description)
        {
            Expect(text.IndexOf(credential, StringComparison.Ordinal) < 0 && text.IndexOf("\"apiKey\"", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("\"apikey\"", StringComparison.OrdinalIgnoreCase) < 0,
                "Credential content was exposed through " + description + ".");
        }

        private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
