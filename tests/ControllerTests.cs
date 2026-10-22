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
                Check("custom download folders save exactly and survive restart while the default remains Downloads/NexusM", TestDownloadFolderSetting);
                Check("invalid or unavailable download folders retain the saved path and other settings", TestInvalidDownloadFolderSetting);
                Check("clearing the download folder confirms the exact files and retains subfolders and all download history", TestClearDownloadFolder);
                Check("cancelled or empty folder cleanup leaves files and history untouched", TestCancelDownloadFolderCleanup);
                Check("folder cleanup refuses active transfers and pending authorizations before confirmation", TestDownloadFolderActiveGuards);
                Check("folder cleanup blocks reentrant cleanup, folder changes, and new downloads before API access", TestDownloadFolderBusyGuards);
                Check("folder cleanup resets its busy state after confirmation exceptions and partial recycle failures", TestDownloadFolderFailureRecovery);
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
            string[] fixtures = { "not JSON", "[]", "null", "{\"settings\":\"unexpected\"}", "{\"settings\":[1,2],\"games\":false,\"mods\":123,\"downloads\":null}", "{\"settings\":{\"downloadFolder\":\" \"}}", "{\"settings\":{\"downloadFolder\":123}}", "{\"settings\":{\"downloadFolder\":\"relative-folder\"}}", "{\"settings\":{\"downloadFolder\":\"C:\\\\invalid?folder\"}}" };
            for (int i = 0; i < fixtures.Length; i++)
            {
                string folder = Path.Combine(root, "malformed-" + i); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "state.json"), fixtures[i]);
                Storage storage = new Storage(folder);
                object downloadFolder = Data.Get(storage.Settings, "downloadFolder");
                Expect(downloadFolder is string && (string)downloadFolder == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NexusM"), "Malformed settings did not recover the default download folder for fixture " + i + ".");
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

        private static void TestDownloadFolderSetting()
        {
            Storage storage = NewStore("download-folder-setting");
            string defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NexusM");
            Expect(Data.Str(storage.Settings, "downloadFolder") == defaultFolder, "A new library changed the default download folder.");
            string custom = Path.Combine(root, "Custom Downloads");
            WithController(storage, delegate(Controller controller)
            {
                object result = controller.Handle("saveSettings", Data.Obj("downloadFolder", custom)).GetAwaiter().GetResult();
                Expect(Directory.Exists(custom), "Saving a new download folder did not create it.");
                Expect(Data.Str(storage.Settings, "downloadFolder") == custom && Data.Str(Data.Get(result, "settings"), "downloadFolder") == custom, "Saving appended a subfolder or returned a different path.");
                Expect(!Directory.Exists(Path.Combine(custom, "NexusM")), "Saving a custom folder created an unwanted NexusM subfolder.");
                Expect(Data.Str(new Storage(storage.Root).Settings, "downloadFolder") == custom, "A custom folder did not survive restart.");
                // Saving does not inherit the stricter rules for recycling whole folders.
                controller.Handle("saveSettings", Data.Obj("downloadFolder", storage.Root)).GetAwaiter().GetResult();
                Expect(Data.Str(storage.Settings, "downloadFolder") == storage.Root, "A folder protected from cleanup was rejected as a download destination.");
                Expect(controller.Api == null, "Changing a local download folder unexpectedly accessed Nexus.");
            });
            // Validate and reload other-drive and network paths without touching either destination.
            foreach (string configured in new[] { @"D:\Downloads", @"\\fixture-server\shared downloads" })
            {
                Expect(Storage.NormalizeDownloadFolder(configured) == configured, "Absolute folder validation changed a custom destination.");
                storage.Settings["downloadFolder"] = configured; storage.Save();
                Expect(Data.Str(new Storage(storage.Root).Settings, "downloadFolder") == configured, "Reloading a custom folder appended a suffix or restored the default.");
            }
            Expect(Data.Str(NewStore("download-folder-default").Settings, "downloadFolder") == defaultFolder, "A custom destination changed the default for another library.");
        }

        private static void TestInvalidDownloadFolderSetting()
        {
            Storage storage = NewStore("invalid-download-folder");
            string retained = Path.Combine(root, "retained-download-folder");
            WithController(storage, delegate(Controller controller)
            {
                controller.Handle("saveSettings", Data.Obj("downloadFolder", retained)).GetAwaiter().GetResult();
                string before = File.ReadAllText(Path.Combine(storage.Root, "state.json"));
                object[] invalid = { null, "", " ", "relative-folder", @"..\Downloads", @"D:Downloads", @"\Downloads", @"C:\invalid?folder", @"C:\invalid*folder", @"C:\invalid|folder", @"C:\folder:stream", "C:\\invalid\0folder", 123, Data.Obj("path", retained) };
                foreach (object value in invalid)
                {
                    bool rejected = false;
                    try { controller.Handle("saveSettings", Data.Obj("downloadFolder", value, "yourGamesOnly", false)).GetAwaiter().GetResult(); }
                    catch (ArgumentException error) { rejected = error.Message == "Enter a valid absolute download folder path, such as D:\\Downloads."; }
                    Expect(rejected, "An invalid download folder was accepted or lacked a useful error.");
                    Expect(Data.Str(storage.Settings, "downloadFolder") == retained && Data.Bool(storage.Settings, "yourGamesOnly"), "Invalid folder input changed saved settings.");
                    Expect(File.ReadAllText(Path.Combine(storage.Root, "state.json")) == before, "Invalid folder input changed the state file.");
                }
                string occupied = Path.Combine(root, "occupied-folder-path"); File.WriteAllText(occupied, "fixture file");
                bool unavailable = false;
                try { controller.Handle("saveSettings", Data.Obj("downloadFolder", Path.Combine(occupied, "Downloads"))).GetAwaiter().GetResult(); }
                catch (IOException) { unavailable = true; }
                Expect(unavailable && File.ReadAllText(occupied) == "fixture file", "An unavailable destination was accepted or changed its blocking file.");
                Expect(Data.Str(storage.Settings, "downloadFolder") == retained && File.ReadAllText(Path.Combine(storage.Root, "state.json")) == before, "A failed folder creation replaced the saved destination.");
            });
            Expect(Data.Str(new Storage(storage.Root).Settings, "downloadFolder") == retained, "Rejected folder input changed the destination after restart.");
        }

        private static string CleanupFolder(Storage storage, string suffix)
        {
            string folder = Path.Combine(root, Path.GetFileName(storage.Root) + "-" + suffix);
            Directory.CreateDirectory(folder);
            storage.Settings["downloadFolder"] = folder;
            return folder;
        }

        private static void ExpectCleanupRejected(Action action, string reason)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidOperationException error)
            {
                rejected = true;
                Expect(error.Message.IndexOf("API key", StringComparison.OrdinalIgnoreCase) < 0, reason + " reached the API requirement before the cleanup guard.");
            }
            Expect(rejected, reason + " was not rejected.");
        }

        private static void TestClearDownloadFolder()
        {
            Storage storage = NewStore("clear-folder");
            string folder = CleanupFolder(storage, "archives"), recycled = Path.Combine(root, "clear-folder-recycled");
            Directory.CreateDirectory(recycled);
            string archive = Path.Combine(folder, "main.zip"), partial = Path.Combine(folder, "optional.zip.part"), sidecar = Path.Combine(folder, "main.zip.nexusm.json");
            File.WriteAllText(archive, "fixture archive"); File.WriteAllText(partial, "fixture partial"); File.WriteAllText(sidecar, "fixture sidecar");
            string nested = Path.Combine(folder, "keep-subfolder"); Directory.CreateDirectory(nested);
            string nestedFile = Path.Combine(nested, "keep.zip"); File.WriteAllText(nestedFile, "nested fixture");
            storage.Mods.Add(Data.Obj("gameDomain", "baldursgate3", "modId", 5508, "lastDownloadedAt", 1792100000, "reviewedAt", 1792000000, "needsReview", true));
            storage.Downloads.Add(Download(42, "completed", 1792200000, "2.2.3"));
            storage.Downloads.Add(Download(99, "failed", 0, "2.2.1"));
            storage.State["fileDownloads"] = Data.Obj("baldursgate3:5508:42", Download(42, "completed", 1792200000, "2.2.3"));
            string modsBefore = Data.Json.Serialize(storage.Mods), queueBefore = Data.Json.Serialize(storage.Downloads), filesBefore = Data.Json.Serialize(Data.Get(storage.State, "fileDownloads"));
            WithController(storage, delegate(Controller controller)
            {
                int confirmations = 0, moved = 0;
                object result = controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan)
                {
                    confirmations++;
                    Expect(Data.Bool(storage.State, "clearingDownloadFolder"), "Cleanup was not busy during its confirmation callback.");
                    Expect(String.Equals(plan.Folder, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase), "Confirmation received a different download folder.");
                    Expect(plan.Files.Count == 3 && plan.Files.OrderBy(x => x).SequenceEqual(new[] { archive, partial, sidecar }.OrderBy(x => x)), "Confirmation did not contain exactly the direct files.");
                    Expect(plan.Files.IsReadOnly, "The confirmed file list should be read-only.");
                    return true;
                }, delegate(string path)
                {
                    Expect(Data.Bool(storage.State, "clearingDownloadFolder"), "Cleanup stopped being busy before file recycling completed.");
                    File.Move(path, Path.Combine(recycled, Path.GetFileName(path))); moved++;
                });
                Expect(confirmations == 1 && moved == 3, "Confirmed cleanup did not process every direct file exactly once.");
                Expect(Directory.Exists(folder) && Directory.GetFiles(folder).Length == 0, "Cleanup removed the folder or left direct files behind.");
                Expect(File.ReadAllText(nestedFile) == "nested fixture", "Cleanup changed a subfolder's contents.");
                Expect(Directory.GetFiles(recycled).Length == 3, "Fixture recycle destination did not retain the files.");
                ExpectCleanupIdle(result, "Successful cleanup response");
                ExpectCleanupIdle(storage.PublicState(), "Successful cleanup public state");
                ExpectCleanupIdle(Data.Json.DeserializeObject(File.ReadAllText(Path.Combine(storage.Root, "state.json"))), "Successful cleanup saved state");
                Expect(Data.Json.Serialize(storage.Mods) == modsBefore && Data.Json.Serialize(storage.Downloads) == queueBefore && Data.Json.Serialize(Data.Get(storage.State, "fileDownloads")) == filesBefore,
                    "Folder cleanup changed the queue, per-file flags, or mod dates and review state.");
            });
            Storage reopened = new Storage(storage.Root);
            Expect(Data.Json.Serialize(reopened.Mods) == modsBefore && Data.Json.Serialize(reopened.Downloads) == queueBefore && Data.Json.Serialize(Data.Get(reopened.State, "fileDownloads")) == filesBefore,
                "Folder cleanup failed to retain the library and successful file history after restart.");
        }

        private static void TestCancelDownloadFolderCleanup()
        {
            Storage storage = NewStore("cancel-clear-folder");
            string folder = CleanupFolder(storage, "archives"), archive = Path.Combine(folder, "keep.zip");
            File.WriteAllText(archive, "keep fixture");
            WithController(storage, delegate(Controller controller)
            {
                int confirmations = 0, recycles = 0;
                object result = controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { confirmations++; return false; }, delegate(string path) { recycles++; });
                Expect(confirmations == 1 && recycles == 0 && File.ReadAllText(archive) == "keep fixture", "Declining cleanup changed a file.");
                ExpectCleanupIdle(result, "Declined cleanup response");
                ExpectCleanupIdle(storage.PublicState(), "Declined cleanup public state");
                storage.Settings["downloadFolder"] = CleanupFolder(storage, "empty");
                controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { confirmations++; return true; }, delegate(string path) { recycles++; });
                Expect(confirmations == 1 && recycles == 0, "An empty download folder prompted for confirmation or recycled a file.");
                Expect(!Data.Bool(storage.State, "clearingDownloadFolder"), "Empty cleanup retained the busy state.");
            });
        }

        private static void TestDownloadFolderActiveGuards()
        {
            Storage storage = NewStore("guard-clear-folder");
            string folder = CleanupFolder(storage, "archives"), archive = Path.Combine(folder, "keep.zip");
            File.WriteAllText(archive, "keep fixture");
            WithController(storage, delegate(Controller controller)
            {
                int confirmations = 0, recycles = 0;
                Func<DownloadFolderPlan, bool> confirm = delegate(DownloadFolderPlan plan) { confirmations++; return true; };
                Action<string> recycle = delegate(string path) { recycles++; };
                var transfers = (Dictionary<string, CancellationTokenSource>)typeof(Controller).GetField("transfers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                var pending = (HashSet<string>)typeof(Controller).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                using (var active = new CancellationTokenSource())
                {
                    transfers["unlisted-transfer"] = active;
                    ExpectCleanupRejected(delegate { controller.ClearDownloadFolder(confirm, recycle); }, "A transfer missing from the visible queue");
                    Expect(!active.IsCancellationRequested, "Folder cleanup cancelled an active transfer.");
                    transfers.Remove("unlisted-transfer");
                }
                pending.Add("unlisted-authorization");
                ExpectCleanupRejected(delegate { controller.ClearDownloadFolder(confirm, recycle); }, "A pending authorization missing from the visible queue");
                pending.Clear();
                foreach (string status in new[] { "queued", "authorizing", "waiting-authorization", "retrying", "downloading" })
                {
                    storage.Downloads.Add(Download(42, status, 0, "2.2.3"));
                    ExpectCleanupRejected(delegate { controller.ClearDownloadFolder(confirm, recycle); }, "The " + status + " queue entry");
                    Expect(Data.Str(storage.Downloads[0], "status") == status, "Cleanup changed a guarded download status.");
                    storage.Downloads.Clear();
                }
                Expect(confirmations == 0 && recycles == 0 && File.ReadAllText(archive) == "keep fixture", "Guarded cleanup reached confirmation or changed a file.");
                Expect(!Data.Bool(storage.State, "clearingDownloadFolder"), "Rejected cleanup retained the busy state.");
            });
        }

        private static void TestDownloadFolderBusyGuards()
        {
            Storage storage = NewStore("busy-clear-folder");
            string folder = CleanupFolder(storage, "archives"), archive = Path.Combine(folder, "keep.zip"), alternative = Path.Combine(root, "busy-clear-folder-uncreated");
            File.WriteAllText(archive, "keep fixture");
            WithController(storage, delegate(Controller controller)
            {
                int recycled = 0;
                controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan)
                {
                    Expect(Data.Bool(storage.State, "clearingDownloadFolder"), "Cleanup was not busy inside its confirmation callback.");
                    ExpectCleanupRejected(delegate { controller.ClearDownloadFolder(delegate(DownloadFolderPlan nested) { throw new Exception("Nested cleanup reached confirmation."); }); }, "Nested folder cleanup");
                    ExpectCleanupRejected(delegate { controller.Handle("saveSettings", Data.Obj("downloadFolder", alternative)).GetAwaiter().GetResult(); }, "Changing the download folder");
                    ExpectCleanupRejected(delegate { controller.Handle("browseFolder", null).GetAwaiter().GetResult(); }, "Browsing for a new download folder");
                    ExpectCleanupRejected(delegate { controller.Handle("startDownload", Data.Obj("gameDomain", "baldursgate3", "modId", 5508, "fileId", 42)).GetAwaiter().GetResult(); }, "Starting a new download");
                    ExpectCleanupRejected(delegate { controller.Handle("retryDownload", Data.Obj("id", "missing-download")).GetAwaiter().GetResult(); }, "Retrying a download");
                    ExpectCleanupRejected(delegate { controller.ReceiveNxm("nxm://baldursgate3/mods/5508/files/42").GetAwaiter().GetResult(); }, "Receiving a new NXM download");
                    Expect(Data.Str(storage.Settings, "downloadFolder") == folder && !Directory.Exists(alternative), "Busy folder change altered settings or created the new folder.");
                    Expect(storage.Downloads.Count == 0 && controller.Api == null, "A blocked download changed the queue or initialized an API client.");
                    return false;
                }, delegate(string path) { recycled++; });
                Expect(recycled == 0 && File.ReadAllText(archive) == "keep fixture" && !Data.Bool(storage.State, "clearingDownloadFolder"), "Reentrant cleanup checks changed a file or left cleanup busy.");
            });
        }

        private static void TestDownloadFolderFailureRecovery()
        {
            Storage storage = NewStore("failed-clear-folder");
            string folder = CleanupFolder(storage, "archives"), recycled = Path.Combine(root, "failed-clear-folder-recycled");
            Directory.CreateDirectory(recycled);
            string first = Path.Combine(folder, "a-main.zip"), second = Path.Combine(folder, "b-optional.zip");
            File.WriteAllText(first, "main fixture"); File.WriteAllText(second, "optional fixture");
            WithController(storage, delegate(Controller controller)
            {
                bool threw = false;
                try { controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { throw new IOException("Fixture confirmation failure"); }, delegate(string path) { throw new Exception("Recycling must not follow a failed confirmation."); }); }
                catch (IOException) { threw = true; }
                Expect(threw && !Data.Bool(storage.State, "clearingDownloadFolder") && File.Exists(first) && File.Exists(second), "Confirmation failure changed files or left cleanup busy.");
                ExpectCleanupIdle(storage.PublicState(), "Confirmation failure public state");
                ExpectCleanupIdle(Data.Json.DeserializeObject(File.ReadAllText(Path.Combine(storage.Root, "state.json"))), "Confirmation failure saved state");
                int processed = 0;
                object result = controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { return true; }, delegate(string path)
                {
                    processed++;
                    if (path == second) throw new IOException("Fixture locked file");
                    File.Move(path, Path.Combine(recycled, Path.GetFileName(path)));
                });
                Expect(processed == 2 && !File.Exists(first) && File.ReadAllText(second) == "optional fixture", "Partial recycling did not retain the failed file and move the successful file.");
                ExpectCleanupIdle(result, "Partial recycle failure response");
                ExpectCleanupIdle(storage.PublicState(), "Partial recycle failure public state");
                controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { return true; }, delegate(string path) { File.Move(path, Path.Combine(recycled, Path.GetFileName(path))); });
                Expect(!File.Exists(second) && !Data.Bool(storage.State, "clearingDownloadFolder"), "Cleanup could not run again after a partial failure.");
            });
        }

        private static void ExpectCleanupIdle(object state, string description)
        {
            object busy = Data.Get(state, "clearingDownloadFolder");
            Expect(busy is bool && !(bool)busy, description + " must include an explicit false to unlock merged UI state.");
        }

        private static void AssertNoCredential(string text, string credential, string description)
        {
            Expect(text.IndexOf(credential, StringComparison.Ordinal) < 0 && text.IndexOf("\"apiKey\"", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("\"apikey\"", StringComparison.OrdinalIgnoreCase) < 0,
                "Credential content was exposed through " + description + ".");
        }

        private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
