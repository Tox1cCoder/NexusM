using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
                Check("all unfinished downloads survive queues larger than the old 500-entry limit", TestLargeQueuePersistence);
                Check("download configuration validates atomically and persists across restarts", TestDownloadConfiguration);
                Check("corrupt state recovers its backup and preserves unreadable originals", TestQueueStateBackup);
                Check("saved unfinished downloads recover FIFO once with their original folder", TestQueueRecovery);
                Check("disabled recovery, unavailable accounts and explicit cancellation retain queue intent safely", TestQueueRecoveryGuards);
                Check("authorization failures retain redacted bounded per-download logs", TestQueueFailureLogs);
                Check("failed queue admission rolls back pending state and can be retried after disk recovery", TestQueueAdmissionFailure);
                Check("queue progress and shutdown still release transfers when checkpoint writes fail", TestQueueCheckpointFailure);
                Check("unfinished backups preserve folders, FIFO order, retry deadlines and sanitized logs", TestQueueBackupMetadata);
                Check("unacknowledged completed archives reconcile history offline across restart", TestQueueCompletionReceipt);
                Check("server retry deadlines survive restart and block premature manual and automatic retry", TestQueueRetryDeadline);
                Check("a later manual refresh reconnects and restores an offline saved queue", TestQueueOfflineReconnect);
                Check("new links arriving during settings changes are saved for fresh authorization", TestQueueSettingsGate);
                Check("temporary recovery checkpoint failures keep every job resumable and recover on refresh", TestQueueRecoverySaveFailure);
                Check("all queue admission paths honor a saved server retry deadline before network or mutation", TestQueueAdmissionRetryDeadline);
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
                Check("clearing the download folder confirms only NexusM files and retains unrelated files, subfolders, and history", TestClearDownloadFolder);
                Check("folder cleanup works from persistent file history after the visible queue is cleared", TestClearDownloadFolderAfterClearingList);
                Check("a folder containing only unrelated files never prompts or deletes anything", TestClearDownloadFolderWithoutOwnedFiles);
                Check("a file replaced since its recorded download remains untouched before confirmation", TestClearDownloadFolderReplacedFile);
                Check("backup restore preserves ownership fingerprints and still protects replacement files", TestDownloadOwnershipBackup);
                Check("cancelled or empty folder cleanup leaves files and history untouched", TestCancelDownloadFolderCleanup);
                Check("folder cleanup refuses active transfers and pending authorizations before confirmation", TestDownloadFolderActiveGuards);
                Check("folder cleanup blocks reentrant cleanup, folder changes, and new downloads before API access", TestDownloadFolderBusyGuards);
                Check("folder cleanup resets its busy state after confirmation exceptions and partial delete failures", TestDownloadFolderFailureRecovery);
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

        private static Dictionary<string, object> QueueRow(int id, string status, string folder, long order)
        {
            return Data.Obj("id", "fixturegame:1:" + id, "gameDomain", "fixturegame", "modId", 1, "fileId", id,
                "name", "Queue archive " + id, "status", status, "folder", folder, "queuedAt", 1000L + order, "queueOrder", order,
                "downloadedBytes", 1234L, "bytes", 1234L, "totalBytes", 5000L, "total", 5000L, "speed", 9999L,
                "retryAt", 2000L, "resumeOnStartup", status == "interrupted");
        }

        private static void TestLargeQueuePersistence()
        {
            Storage storage = NewStore("large-queue"); string folder = Path.Combine(root, "large-archives");
            string[] statuses = { "queued", "downloading", "waiting-authorization", "retrying", "authorizing", "failed", "cancelled", "completed" };
            for (int i = 1; i <= 1200; i++) storage.Downloads.Add(QueueRow(i, statuses[(i - 1) % statuses.Length], folder, i));
            storage.Save(); Storage reopened = new Storage(storage.Root);
            Expect(storage.Downloads.Count == 1200 && reopened.Downloads.Count == 1200, "Saving silently discarded old unfinished downloads.");
            for (int i = 0; i < reopened.Downloads.Count; i++) {
                object row = reopened.Downloads[i]; string original = statuses[i % statuses.Length];
                Expect(Data.Num(row, "downloadedBytes") == 1234L && Data.Num(row, "speed") == 0L && Data.Num(row, "retryAt") == 0L, "Recovery lost partial progress or retained transient speed/backoff.");
                Expect(Data.Str(row, "folder") == folder, "Recovery redirected a saved job's folder.");
                bool unfinished = new[] { "queued", "downloading", "waiting-authorization", "retrying", "authorizing" }.Contains(original);
                Expect(Data.Str(row, "status") == (unfinished ? "interrupted" : original), "Recovery changed the wrong terminal state.");
                Expect(Data.Bool(row, "resumeOnStartup") == unfinished, "Recovery lost resume intent or reactivated a terminal row.");
            }
        }

        private static void TestDownloadConfiguration()
        {
            Storage storage = NewStore("download-config");
            Expect(Data.Num(storage.Settings, "maxParallelDownloads") == 2 && Data.Num(storage.Settings, "maxDownloadRetries") == 3 && Data.Bool(storage.Settings, "resumeDownloadsOnStartup"), "Download defaults are missing.");
            WithController(storage, delegate(Controller controller) {
                controller.Handle("saveSettings", Data.Obj("maxParallelDownloads", 4, "maxDownloadRetries", 0, "downloadTimeoutSeconds", 600, "retryDelaySeconds", 60, "resumeDownloadsOnStartup", false)).GetAwaiter().GetResult();
                var reopened = new Storage(storage.Root);
                Expect(Data.Num(reopened.Settings, "maxParallelDownloads") == 4 && Data.Num(reopened.Settings, "maxDownloadRetries") == 0 && !Data.Bool(reopened.Settings, "resumeDownloadsOnStartup"), "Download options did not persist.");
                object[] bad = { Data.Obj("maxParallelDownloads", 0), Data.Obj("maxParallelDownloads", 5), Data.Obj("maxParallelDownloads", "2"), Data.Obj("maxDownloadRetries", 11), Data.Obj("downloadTimeoutSeconds", 14), Data.Obj("downloadTimeoutSeconds", 601), Data.Obj("retryDelaySeconds", 0), Data.Obj("resumeDownloadsOnStartup", "yes") };
                foreach (object payload in bad) {
                    bool rejected = false; try { controller.Handle("saveSettings", payload).GetAwaiter().GetResult(); } catch (ArgumentException) { rejected = true; }
                    Expect(rejected && Data.Num(storage.Settings, "maxParallelDownloads") == 4 && !Data.Bool(storage.Settings, "resumeDownloadsOnStartup"), "An invalid setting partly changed the saved configuration.");
                }
                var transfers = (Dictionary<string, CancellationTokenSource>)typeof(Controller).GetField("transfers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                using (var active = new CancellationTokenSource()) {
                    transfers["active-config-fixture"] = active;
                    try {
                        bool rejected = false; try { controller.Handle("saveSettings", Data.Obj("maxParallelDownloads", 1, "resumeDownloadsOnStartup", true)).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
                        Expect(rejected && Data.Num(storage.Settings, "maxParallelDownloads") == 4 && !Data.Bool(storage.Settings, "resumeDownloadsOnStartup"), "Changing active transfer options was not rejected atomically.");
                    } finally { transfers.Remove("active-config-fixture"); }
                }
            });
            File.WriteAllText(Path.Combine(storage.Root, "state.json"), "{\"settings\":{\"maxParallelDownloads\":99,\"maxDownloadRetries\":-1,\"downloadTimeoutSeconds\":\"90\",\"retryDelaySeconds\":false,\"resumeDownloadsOnStartup\":\"yes\"}}");
            var malformed = new Storage(storage.Root);
            Expect(Data.Num(malformed.Settings, "maxParallelDownloads") == 2 && Data.Num(malformed.Settings, "maxDownloadRetries") == 3 && Data.Num(malformed.Settings, "downloadTimeoutSeconds") == 90 && Data.Num(malformed.Settings, "retryDelaySeconds") == 2 && Data.Bool(malformed.Settings, "resumeDownloadsOnStartup"), "Malformed persisted options were not normalized.");
        }

        private static void TestQueueStateBackup()
        {
            Storage storage = NewStore("queue-backup"); string path = Path.Combine(storage.Root, "state.json");
            storage.Downloads.Add(QueueRow(1, "queued", Path.Combine(root, "backup-archives"), 1)); storage.Save();
            storage.Downloads.Add(QueueRow(2, "queued", Path.Combine(root, "backup-archives"), 2)); storage.Save();
            File.WriteAllText(path, "unreadable primary fixture");
            var restored = new Storage(storage.Root);
            Expect(restored.Downloads.Count == 1 && Data.Str(restored.Downloads[0], "id") == "fixturegame:1:1", "The previous valid queue snapshot was not recovered.");
            Expect(Data.Str(restored.State, "notice").Contains("Recovered") && Directory.GetFiles(storage.Root, "state-unreadable-*.json").Length == 1, "Backup recovery did not report or preserve the unreadable file.");
            restored.Save(); var again = new Storage(storage.Root);
            Expect(again.Downloads.Count == 1 && Data.List(Data.Get(Data.Json.DeserializeObject(File.ReadAllText(path + ".bak")), "downloads")).Count == 1, "Saving restored state destroyed the known-good backup.");
            File.WriteAllText(path, "bad primary again"); File.WriteAllText(path + ".bak", "bad backup fixture");
            var lost = new Storage(storage.Root);
            Expect(Data.Str(lost.State, "notice").Contains("could not be read") && Directory.GetFiles(storage.Root, "state-unreadable-*.json").Length == 3, "Unrecoverable files were silently discarded.");
        }

        private static void TestQueueRecovery()
        {
            Storage storage = NewStore("queue-recovery"); string folder = Path.Combine(root, "original-queue-folder");
            storage.Settings["downloadFolder"] = Path.Combine(root, "changed-default-folder");
            storage.Downloads.Add(QueueRow(2, "interrupted", folder, 2)); storage.Downloads.Add(QueueRow(1, "interrupted", folder, 1));
            storage.Downloads.Add(QueueRow(3, "failed", folder, 3)); storage.Downloads.Add(QueueRow(4, "cancelled", folder, 4)); storage.Downloads.Add(QueueRow(5, "completed", folder, 5));
            WithController(storage, delegate(Controller controller) {
                controller.Api = new NexusApi("fixture-only-key"); storage.State["connected"] = true;
                var ids = new List<string>();
                controller.ResumePendingDownloads(delegate(string url, string id) {
                    var disk = Data.Json.DeserializeObject(File.ReadAllText(Path.Combine(storage.Root, "state.json")));
                    object saved = Data.List(Data.Get(disk, "downloads")).Single(row => Data.Str(row, "id") == id);
                    Expect(Data.Str(saved, "status") == "waiting-authorization" && Data.Str(saved, "folder") == folder, "Authorization began before the pinned job was persisted.");
                    Expect(!url.Contains("fixture-only-key") && url.EndsWith("&nmm=1", StringComparison.Ordinal), "Recovery persisted or used an account credential as download authorization.");
                    ids.Add(id);
                });
                controller.ResumePendingDownloads(delegate(string url, string id) { ids.Add(id); });
                Expect(ids.SequenceEqual(new[] { "fixturegame:1:1", "fixturegame:1:2" }), "Recovery reordered, duplicated, or reactivated terminal downloads.");
                Expect(controller.CanStartAuthorization, "Available parallel slots did not allow authorization.");
                var transfers = (Dictionary<string, CancellationTokenSource>)typeof(Controller).GetField("transfers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                using (var one = new CancellationTokenSource()) using (var two = new CancellationTokenSource()) {
                    transfers["one"] = one; transfers["two"] = two;
                    Expect(!controller.CanStartAuthorization, "The browser scheduler ignored the configured parallel limit.");
                    transfers.Clear();
                }
            });
            Storage reopened = new Storage(storage.Root);
            Expect(reopened.Downloads.Count == 5 && reopened.Downloads.Take(2).All(row => Data.Str(row, "status") == "interrupted" && Data.Bool(row, "resumeOnStartup")), "Closing the app lost queued recovery intent.");
        }

        private static void TestQueueRecoveryGuards()
        {
            Storage storage = NewStore("queue-recovery-guards"); string folder = Path.Combine(root, "guard-archives");
            storage.Downloads.Add(QueueRow(1, "interrupted", folder, 1));
            WithController(storage, delegate(Controller controller) {
                int callbacks = 0; Action<string, string> authorize = delegate { callbacks++; };
                controller.ResumePendingDownloads(authorize); Expect(callbacks == 0 && Data.Bool(storage.Downloads[0], "resumeOnStartup"), "A missing account discarded recovery intent.");
                controller.Api = new NexusApi("fixture-only-key"); storage.State["connected"] = true; storage.Settings["resumeDownloadsOnStartup"] = false;
                controller.ResumePendingDownloads(authorize); Expect(callbacks == 0 && Data.Str(storage.Downloads[0], "status") == "interrupted", "Disabled automatic recovery started a download.");
                storage.Settings["resumeDownloadsOnStartup"] = true; controller.ResumePendingDownloads(authorize);
                controller.Handle("cancelDownload", Data.Obj("id", "fixturegame:1:1")).GetAwaiter().GetResult();
                Expect(Data.Str(storage.Downloads[0], "status") == "cancelled" && !Data.Bool(storage.Downloads[0], "resumeOnStartup"), "Explicit cancellation was not saved before shutdown.");
            });
            var reopened = new Storage(storage.Root);
            Expect(Data.Str(reopened.Downloads.Single(), "status") == "cancelled" && !Data.Bool(reopened.Downloads.Single(), "resumeOnStartup"), "An explicitly cancelled job was reactivated on restart.");
        }

        private static void TestQueueFailureLogs()
        {
            Storage storage = NewStore("queue-failure-logs"); var row = QueueRow(1, "waiting-authorization", Path.Combine(root, "log-archives"), 1); storage.Downloads.Add(row);
            WithController(storage, delegate(Controller controller) {
                controller.AuthorizationFailed("fixturegame:1:1", "Nexus timed out https://cdn.nexusmods.com/archive?key=synthetic-secret token=another-secret", "authorization_timeout");
                string saved = File.ReadAllText(Path.Combine(storage.Root, "state.json"));
                Expect(Data.Str(row, "status") == "failed" && Data.Str(row, "errorCode") == "authorization_timeout" && !Data.Bool(row, "resumeOnStartup"), "An authorization timeout lost its queue entry or left it blocking.");
                Expect(!saved.Contains("synthetic-secret") && !saved.Contains("another-secret"), "Queue errors or logs leaked signed download credentials.");
                MethodInfo append = typeof(Controller).GetMethod("QueueEvent", BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < 60; i++) append.Invoke(null, new object[] { row, "fixture", "Log entry " + i });
                var events = Data.List(Data.Get(row, "events"));
                Expect(events.Count == 40 && Data.Str(events.First(), "message") == "Log entry 20" && Data.Num(events.Last(), "time") > 0, "Per-job logs were unbounded or lost event timestamps.");
                controller.AuthorizationFailed("fixturegame:1:1", "Late failure", "late");
                Expect(Data.Str(row, "errorCode") == "authorization_timeout", "A late browser callback overwrote a terminal download.");
            });
        }

        private static bool EnqueueFixture(Controller controller, int fileId, Action<string, string> authorize)
        {
            return (bool)typeof(Controller).GetMethod("QueueDownloadAuthorization", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller,
                new object[] { "fixturegame", 1L, (long)fileId, Data.Obj("name", "Enqueue fixture", "size", 4096L), authorize });
        }

        private static void TestQueueAdmissionFailure()
        {
            Storage storage = NewStore("queue-admission-failure");
            WithController(storage, delegate(Controller controller) {
                int authorizations = 0;
                using (var locked = new FileStream(Path.Combine(storage.Root, "state.json.tmp"), FileMode.Create, FileAccess.Write, FileShare.None)) {
                    bool rejected = false; try { EnqueueFixture(controller, 1, delegate { authorizations++; }); } catch (TargetInvocationException error) { rejected = error.InnerException is IOException; }
                    var pending = (HashSet<string>)typeof(Controller).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                    Expect(rejected && authorizations == 0 && storage.Downloads.Count == 0 && pending.Count == 0, "Failed persistence left an undurable phantom job or started authorization.");
                }
                Expect(EnqueueFixture(controller, 1, delegate { authorizations++; }) && authorizations == 1, "A failed admission could not be retried after disk access recovered.");
                controller.Api = new NexusApi("fixture-only-key");
                using (var locked = new FileStream(Path.Combine(storage.Root, "state.json.tmp"), FileMode.Create, FileAccess.Write, FileShare.None)) {
                    bool rejected = false; try { controller.ReceiveNxm("nxm://fixturegame/mods/1/files/1?key=synthetic-fresh-link&expires=1900000000&user_id=7").GetAwaiter().GetResult(); } catch (IOException) { rejected = true; }
                    var pending = (HashSet<string>)typeof(Controller).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                    Expect(rejected && !pending.Contains("fixturegame:1:1") && Data.Str(storage.Downloads.Single(), "status") == "interrupted", "An unsaved fresh link stranded its job in the pending authorization queue.");
                }
                Expect(EnqueueFixture(controller, 1, delegate { authorizations++; }) && authorizations == 2, "Retry stayed blocked after a fresh-link checkpoint failed.");
                var row = Data.Map(storage.Downloads.Single());
                controller.Handle("cancelDownload", Data.Obj("id", Data.Str(row, "id"))).GetAwaiter().GetResult();
                string original = Path.Combine(root, "admission-original"), changed = Path.Combine(root, "admission-changed"); row["folder"] = original; storage.Settings["downloadFolder"] = changed;
                Expect(EnqueueFixture(controller, 1, delegate { }) && Data.Str(storage.Downloads.Single(), "folder") == original, "A resumable retry abandoned the original partial folder.");
                controller.Handle("cancelDownload", Data.Obj("id", Data.Str(row, "id"))).GetAwaiter().GetResult();
                Data.Map(storage.Downloads.Single())["status"] = "completed";
                Expect(EnqueueFixture(controller, 1, delegate { }) && Data.Str(storage.Downloads.Single(), "folder") == changed, "A new download of a completed file ignored the new configured folder.");
            });
        }

        private static void TestQueueCheckpointFailure()
        {
            Storage storage = NewStore("queue-checkpoint-failure"); var row = QueueRow(1, "downloading", Path.Combine(root, "checkpoint-archives"), 1); row["resumeOnStartup"] = true; storage.Downloads.Add(row); storage.Save();
            MainForm form = new MainForm(storage, null, null, "checkpoint-fixture");
            using (form) using (var transfer = new CancellationTokenSource()) {
                Controller controller = form.Controller;
                var transfers = (Dictionary<string, CancellationTokenSource>)typeof(Controller).GetField("transfers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller); transfers["fixturegame:1:1"] = transfer;
                using (var locked = new FileStream(Path.Combine(storage.Root, "state.json.tmp"), FileMode.Create, FileAccess.Write, FileShare.None)) {
                    typeof(Controller).GetMethod("DownloadProgress", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller,
                        new object[] { Data.Obj("id", "fixturegame:1:1", "status", "retrying", "downloadedBytes", 2345L, "totalBytes", 5000L, "error", "A network timeout occurred.") });
                    Expect(Data.Bool(storage.State, "queuePersistenceError") && Data.Num(row, "bytes") == 2345L, "A failed progress checkpoint crashed or discarded current in-memory progress.");
                    controller.Dispose(); Expect(transfer.IsCancellationRequested, "Failed shutdown checkpoint prevented cancellation and resource release.");
                }
                var reopened = new Storage(storage.Root);
                Expect(reopened.Downloads.Count == 1 && Data.Str(reopened.Downloads[0], "status") == "interrupted" && Data.Bool(reopened.Downloads[0], "resumeOnStartup"), "Failed shutdown persistence lost the previously durable recovery intent.");
            }
        }

        private static void TestQueueBackupMetadata()
        {
            Storage storage = NewStore("queue-backup-metadata"); string original = Path.Combine(root, "backup-original-folder"); long retryAt = Data.Now() + 600;
            var row = QueueRow(7, "interrupted", original, 17); row["attempt"] = 2L; row["maxAttempts"] = 4L; row["retryAt"] = retryAt; row["errorCode"] = "rate-limited";
            row["events"] = Enumerable.Range(0, 60).Select(index => Data.Obj("time", 1000L + index, "code", "fixture", "message", index == 59 ? "https://cdn.nexusmods.com/x?key=never-export-this" : "Event " + index)).Cast<object>().ToList();
            WithController(storage, delegate(Controller controller) {
                Import(controller, Data.Obj("items", new object[0], "schemaVersion", 1, "games", new object[0], "mods", new object[0], "downloads", new[] { row }));
                object restored = storage.Downloads.Single(); var events = Data.List(Data.Get(restored, "events"));
                Expect(Data.Str(restored, "folder") == original && Data.Num(restored, "queueOrder") == 17 && Data.Num(restored, "queuedAt") == 1017L && Data.Bool(restored, "resumeOnStartup"), "Backup restore lost durable queue location, order, or intent.");
                Expect(Data.Num(restored, "attempt") == 2 && Data.Num(restored, "maxAttempts") == 4 && Data.Num(restored, "retryAt") == retryAt, "Backup restore discarded attempt or server backoff state.");
                Expect(events.Count == 40 && Data.Str(events[0], "message") == "Event 20" && !Data.Json.Serialize(restored).Contains("never-export-this"), "Restored logs were not bounded to the newest safe events.");
            });
        }

        private static void TestQueueCompletionReceipt()
        {
            foreach (string status in new[] { "interrupted", "completed" }) {
                Storage storage = NewStore("queue-receipt-" + status); string folder = Path.Combine(root, "receipt-archives-" + status); Directory.CreateDirectory(folder);
                string target = Path.Combine(folder, "final.zip"), marker = Path.Combine(folder, ".nexusm-fixturegame-1-9.part.json"); File.WriteAllText(target, "complete fixture archive"); var file = new FileInfo(target);
                var row = QueueRow(9, status, folder, 1); row["path"] = target; storage.Downloads.Add(row); storage.Mods.Add(Data.Obj("gameDomain", "fixturegame", "modId", 1, "lastDownloadedAt", 500L, "updatedAt", 700L)); storage.Save();
                File.WriteAllText(marker, Data.Json.Serialize(Data.Obj("id", "fixturegame:1:9", "gameDomain", "fixturegame", "modId", 1, "fileId", 9, "phase", "finalizing", "path", target,
                    "name", "Final archive", "filename", "final.zip", "version", "2.0", "downloadedAt", 1000L, "downloadedBytes", file.Length, "totalBytes", file.Length,
                    "fileLength", file.Length, "fileCreatedUtcTicks", file.CreationTimeUtc.Ticks, "fileModifiedUtcTicks", file.LastWriteTimeUtc.Ticks)));
                Storage reopened = new Storage(storage.Root);
                WithController(reopened, delegate(Controller controller) {
                    Expect(Data.Str(reopened.Downloads.Single(), "status") == "completed" && !Data.Bool(reopened.Downloads.Single(), "resumeOnStartup"), "Finalized archive was queued for duplicate download.");
                    Expect(Data.Num(Data.Get(Data.Get(reopened.State, "fileDownloads"), "fixturegame:1:9"), "downloadedAt") == 1000L && Data.Num(reopened.Mods.Single(), "lastDownloadedAt") == 1000L, "Offline finalization recovery missed per-file or mod download history.");
                    Expect(!File.Exists(marker) && File.ReadAllText(target) == "complete fixture archive" && controller.Api == null, "Completion recovery used network, changed the archive, or failed to acknowledge its saved receipt.");
                });
            }
        }

        private static void TestQueueRetryDeadline()
        {
            Storage storage = NewStore("queue-retry-deadline"); var row = QueueRow(1, "retrying", Path.Combine(root, "deadline-archives"), 1); long retryAt = Data.Now() + 600;
            row["errorCode"] = "rate-limited"; row["retryAt"] = retryAt; storage.Downloads.Add(row); storage.Save();
            Storage reopened = new Storage(storage.Root);
            Expect(Data.Num(reopened.Downloads.Single(), "retryAt") == retryAt && Data.Str(reopened.Downloads.Single(), "errorCode") == "rate-limited", "Restart discarded the server's retry deadline.");
            WithController(reopened, delegate(Controller controller) {
                controller.Api = new NexusApi("fixture-only-key"); reopened.State["connected"] = true; int authorizations = 0;
                controller.ResumePendingDownloads(delegate { authorizations++; });
                bool rejected = false; try { controller.Handle("retryDownload", Data.Obj("id", "fixturegame:1:1")).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
                Expect(rejected && authorizations == 0 && Data.Str(reopened.Downloads.Single(), "status") == "interrupted", "Manual or automatic retry ignored the server's minimum wait.");
            });
        }

        private static void TestQueueOfflineReconnect()
        {
            Storage storage = NewStore("queue-offline-reconnect"); storage.Downloads.Add(QueueRow(1, "interrupted", Path.Combine(root, "offline-archives"), 1));
            WithController(storage, delegate(Controller controller) {
                controller.Api = new NexusApi("fixture-only-key");
                typeof(NexusApi).GetField("validated", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller.Api, Data.Obj("user_id", 7L, "name", "Fixture account", "is_premium", false));
                storage.State["connected"] = false;
                var transfers = (Dictionary<string, CancellationTokenSource>)typeof(Controller).GetField("transfers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                using (var one = new CancellationTokenSource()) using (var two = new CancellationTokenSource()) {
                    transfers["one"] = one; transfers["two"] = two;
                    try {
                        controller.Handle("refresh", Data.Obj()).GetAwaiter().GetResult();
                        Expect(Data.Bool(storage.State, "connected") && Data.Str(storage.Downloads.Single(), "status") == "waiting-authorization", "Refreshing after offline startup never reconnected or restored the saved queue.");
                    } finally { transfers.Clear(); }
                }
            });
        }

        private static void TestQueueSettingsGate()
        {
            Storage storage = NewStore("queue-settings-gate");
            WithController(storage, delegate(Controller controller) {
                controller.Api = new NexusApi("fixture-only-key"); storage.State["connected"] = true;
                FieldInfo gate = typeof(Controller).GetField("changingSettings", BindingFlags.Instance | BindingFlags.NonPublic); gate.SetValue(controller, true);
                controller.ReceiveNxm("nxm://fixturegame/mods/1/files/9?key=synthetic-gated-link&expires=1900000000&user_id=7").GetAwaiter().GetResult();
                Expect(!controller.CanStartAuthorization && storage.Downloads.Count == 1 && Data.Str(storage.Downloads.Single(), "status") == "interrupted" && Data.Bool(storage.Downloads.Single(), "resumeOnStartup"), "A link arriving during an account/settings change started on the old downloader or lost its durable intent.");
                Expect(!File.ReadAllText(Path.Combine(storage.Root, "state.json")).Contains("synthetic-gated-link"), "A settings-delayed job persisted its signed URI.");
                gate.SetValue(controller, false); int authorizations = 0;
                controller.ResumePendingDownloads(delegate { authorizations++; });
                Expect(authorizations == 1 && Data.Str(storage.Downloads.Single(), "status") == "waiting-authorization", "A settings-delayed job did not request fresh authorization afterward.");
            });
        }

        private static void TestQueueRecoverySaveFailure()
        {
            Storage storage = NewStore("queue-recovery-save-failure"); string folder = Path.Combine(root, "recovery-save-archives");
            storage.Downloads.Add(QueueRow(1, "interrupted", folder, 1)); storage.Downloads.Add(QueueRow(2, "interrupted", folder, 2)); storage.Save();
            WithController(storage, delegate(Controller controller) {
                controller.Api = new NexusApi("fixture-only-key"); storage.State["connected"] = true; int authorizations = 0;
                using (var locked = new FileStream(Path.Combine(storage.Root, "state.json.tmp"), FileMode.Create, FileAccess.Write, FileShare.None)) {
                    controller.ResumePendingDownloads(delegate { authorizations++; });
                    var pending = (HashSet<string>)typeof(Controller).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                    Expect(authorizations == 0 && pending.Count == 0 && Data.Bool(storage.State, "queuePersistenceError"), "Unsaved recovery started authorization, left pending state, or hid its checkpoint failure.");
                    Expect(storage.Downloads.Count == 2 && storage.Downloads.All(row => Data.Str(row, "status") == "interrupted" && Data.Bool(row, "resumeOnStartup")), "A transient recovery checkpoint failure removed a job's resumable intent.");
                }
                var transfers = (Dictionary<string, CancellationTokenSource>)typeof(Controller).GetField("transfers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                using (var one = new CancellationTokenSource()) using (var two = new CancellationTokenSource()) {
                    transfers["one"] = one; transfers["two"] = two;
                    try {
                        controller.Handle("refresh", Data.Obj()).GetAwaiter().GetResult();
                        Expect(storage.Downloads.All(row => Data.Str(row, "status") == "waiting-authorization") && !Data.Bool(storage.State, "queuePersistenceError"), "A connected manual refresh did not resume every retained job after checkpoint access recovered.");
                    } finally { transfers.Clear(); }
                }
            });
        }

        private sealed class AdmissionTraffic : HttpMessageHandler
        {
            internal int Requests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests++; throw new InvalidOperationException("A server-wait admission reached the network.");
            }
        }

        private static void TestQueueAdmissionRetryDeadline()
        {
            foreach (string code in new[] { "rate-limited", "server-error" }) {
                Storage storage = NewStore("queue-admission-deadline-" + code); var row = QueueRow(1, "failed", Path.Combine(root, "admission-deadline-archives"), 1);
                row["errorCode"] = code; row["retryAt"] = Data.Now() + 600; row["events"] = new[] { Data.Obj("time", Data.Now(), "code", code, "message", "Waiting for the server.") }; storage.Downloads.Add(row); storage.Save();
                WithController(storage, delegate(Controller controller) {
                    controller.Api = new NexusApi("fixture-only-key"); var traffic = new AdmissionTraffic();
                    var client = typeof(NexusApi).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic); ((HttpClient)client.GetValue(controller.Api)).Dispose(); client.SetValue(controller.Api, new HttpClient(traffic));
                    string before = Data.Json.Serialize(storage.Downloads); int authorizations = 0;
                    bool startRejected = false; try { controller.Handle("startDownload", Data.Obj("gameDomain", "fixturegame", "modId", 1, "fileId", 1)).GetAwaiter().GetResult(); } catch (InvalidOperationException) { startRejected = true; }
                    bool batchRejected = false; try { EnqueueFixture(controller, 1, delegate { authorizations++; }); } catch (TargetInvocationException error) { batchRejected = error.InnerException is InvalidOperationException; }
                    bool incomingRejected = false; try { controller.ReceiveNxm("nxm://fixturegame/mods/1/files/1?key=synthetic-deadline-link&expires=1900000000&user_id=7").GetAwaiter().GetResult(); } catch (InvalidOperationException) { incomingRejected = true; }
                    Expect(startRejected && batchRejected && incomingRejected && authorizations == 0 && traffic.Requests == 0, "A download control or incoming Nexus link bypassed the server's retry deadline.");
                    Expect(Data.Json.Serialize(storage.Downloads) == before && storage.Downloads.Count == 1, "Rejecting early retry reset its deadline, changed its log, or duplicated the saved job.");
                });
            }
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

        private static Dictionary<string, object> OwnedFile(string path, int fileId)
        {
            var record = Download(fileId, "completed", Data.Now(), "2.2.3");
            record["path"] = Path.GetFullPath(path); record["filename"] = Path.GetFileName(path);
            record["downloadedBytes"] = new FileInfo(path).Length; record["totalBytes"] = new FileInfo(path).Length;
            return record;
        }

        private static void RecordOwnedFile(Storage storage, string path, int fileId)
        {
            var history = Data.Map(Data.Get(storage.State, "fileDownloads"));
            history["baldursgate3:5508:" + fileId] = OwnedFile(path, fileId);
            storage.State["fileDownloads"] = history;
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
            string folder = CleanupFolder(storage, "archives");
            string archive = Path.Combine(folder, "main.zip"), partial = Path.Combine(folder, ".nexusm-baldursgate3-5508-99.part"), sidecar = partial + ".json";
            File.WriteAllText(archive, "fixture archive"); File.WriteAllText(partial, "fixture partial");
            File.WriteAllText(sidecar, Data.Json.Serialize(Data.Obj("id", "baldursgate3:5508:99", "total", 1000, "etag", "\"fixture\"", "lastModified", "")));
            string unrelatedText = Path.Combine(folder, "personal-notes.txt"), unrelatedZip = Path.Combine(folder, "manual-download.zip");
            string unrelatedSidecar = Path.Combine(folder, "main.zip.nexusm.json"), unrelatedPartial = Path.Combine(folder, ".nexusm-baldursgate3-5508-77.part");
            File.WriteAllText(unrelatedText, "personal fixture"); File.WriteAllText(unrelatedZip, "manual fixture");
            File.WriteAllText(unrelatedSidecar, "unrelated sidecar fixture"); File.WriteAllText(unrelatedPartial, "unproven partial fixture");
            string nested = Path.Combine(folder, "keep-subfolder"); Directory.CreateDirectory(nested);
            string nestedFile = Path.Combine(nested, "keep.zip"); File.WriteAllText(nestedFile, "nested fixture");
            storage.Mods.Add(Data.Obj("gameDomain", "baldursgate3", "modId", 5508, "lastDownloadedAt", 1792100000, "reviewedAt", 1792000000, "needsReview", true));
            storage.Downloads.Add(OwnedFile(archive, 42));
            storage.Downloads.Add(Download(99, "failed", 0, "2.2.1"));
            RecordOwnedFile(storage, archive, 42);
            RecordOwnedFile(storage, nestedFile, 88);
            string elsewhere = Path.Combine(root, "clear-folder-other-location"); Directory.CreateDirectory(elsewhere);
            string outside = Path.Combine(elsewhere, "outside-only.zip"), sameNameUnrelated = Path.Combine(folder, "outside-only.zip");
            File.WriteAllText(outside, "old folder fixture"); File.WriteAllText(sameNameUnrelated, "current unrelated fixture"); RecordOwnedFile(storage, outside, 77);
            var filenameOnly = Download(66, "completed", Data.Now(), "2.2.3"); filenameOnly["path"] = ""; filenameOnly["filename"] = "manual-download.zip";
            Data.Map(storage.State["fileDownloads"])["baldursgate3:5508:66"] = filenameOnly;
            string modsBefore = Data.Json.Serialize(storage.Mods), queueBefore = Data.Json.Serialize(storage.Downloads), filesBefore = Data.Json.Serialize(Data.Get(storage.State, "fileDownloads"));
            WithController(storage, delegate(Controller controller)
            {
                int confirmations = 0, deleted = 0;
                object result = controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan)
                {
                    confirmations++;
                    Expect(Data.Bool(storage.State, "clearingDownloadFolder"), "Cleanup was not busy during its confirmation callback.");
                    Expect(String.Equals(plan.Folder, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase), "Confirmation received a different download folder.");
                    Expect(plan.Files.Count == 3 && plan.Files.OrderBy(x => x).SequenceEqual(new[] { archive, partial, sidecar }.OrderBy(x => x)), "Confirmation did not contain exactly the proven NexusM files.");
                    Expect(plan.Files.IsReadOnly, "The confirmed file list should be read-only.");
                    return true;
                }, delegate(string path)
                {
                    Expect(Data.Bool(storage.State, "clearingDownloadFolder"), "Cleanup stopped being busy before file deletion completed.");
                    File.Delete(path); deleted++;
                });
                Expect(confirmations == 1 && deleted == 3, "Confirmed cleanup did not process every proven NexusM file exactly once.");
                Expect(Directory.Exists(folder) && !File.Exists(archive) && !File.Exists(partial) && !File.Exists(sidecar), "Cleanup removed the folder or retained an owned file.");
                Expect(File.ReadAllText(unrelatedText) == "personal fixture" && File.ReadAllText(unrelatedZip) == "manual fixture", "Cleanup changed unrelated text or archive files.");
                Expect(File.ReadAllText(unrelatedSidecar) == "unrelated sidecar fixture" && File.ReadAllText(unrelatedPartial) == "unproven partial fixture", "Cleanup guessed ownership from a sidecar or partial filename.");
                Expect(File.ReadAllText(outside) == "old folder fixture" && File.ReadAllText(sameNameUnrelated) == "current unrelated fixture", "Cleanup used a filename from another folder instead of its exact recorded path.");
                Expect(File.ReadAllText(nestedFile) == "nested fixture", "Cleanup changed a subfolder's contents.");
                Expect(Data.Str(result, "notice").Contains("Permanently deleted 3 files."), "Cleanup should report the permanent deletion count.");
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

        private static void TestClearDownloadFolderAfterClearingList()
        {
            Storage storage = NewStore("clear-folder-after-list");
            string folder = CleanupFolder(storage, "archives"), archive = Path.Combine(folder, "recorded.zip"), unrelated = Path.Combine(folder, "keep.zip");
            File.WriteAllText(archive, "recorded archive fixture"); File.WriteAllText(unrelated, "unrelated archive fixture");
            storage.Downloads.Add(OwnedFile(archive, 42));
            WithController(storage, delegate(Controller controller)
            {
                controller.Handle("clearDownloads", null).GetAwaiter().GetResult();
                Expect(storage.Downloads.Count == 0, "The visible queue was not cleared before folder cleanup.");
                int confirmed = 0;
                controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { confirmed++; Expect(plan.Files.SequenceEqual(new[] { archive }), "Persistent history did not identify exactly the recorded archive."); return true; }, File.Delete);
                Expect(confirmed == 1 && !File.Exists(archive) && File.ReadAllText(unrelated) == "unrelated archive fixture", "Clearing the visible list lost ownership evidence or allowed unrelated deletion.");
                Expect(Data.Get(Data.Get(storage.State, "fileDownloads"), "baldursgate3:5508:42") != null && storage.Downloads.Count == 0, "Folder cleanup removed the persistent downloaded flag or rebuilt the queue.");
            });
        }

        private static void TestClearDownloadFolderWithoutOwnedFiles()
        {
            Storage storage = NewStore("clear-folder-unrelated-only");
            string folder = CleanupFolder(storage, "archives"), archive = Path.Combine(folder, "manual.zip"), text = Path.Combine(folder, "notes.txt");
            File.WriteAllText(archive, "manual fixture"); File.WriteAllText(text, "personal fixture");
            WithController(storage, delegate(Controller controller)
            {
                int confirmed = 0, deleted = 0;
                object result = controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { confirmed++; return true; }, delegate { deleted++; });
                Expect(confirmed == 0 && deleted == 0, "Unrelated files reached the confirmation or deletion callback.");
                Expect(File.ReadAllText(archive) == "manual fixture" && File.ReadAllText(text) == "personal fixture", "Unrelated-only cleanup changed files.");
                Expect(!Data.Bool(result, "clearingDownloadFolder"), "Unrelated-only cleanup left a busy state.");
            });
        }

        private static void TestClearDownloadFolderReplacedFile()
        {
            Storage storage = NewStore("clear-folder-replaced-file");
            string folder = CleanupFolder(storage, "archives"), archive = Path.Combine(folder, "recorded.zip");
            File.WriteAllText(archive, "original fixture");
            var record = OwnedFile(archive, 42); var file = new FileInfo(archive);
            record["fileLength"] = file.Length; record["fileCreatedUtcTicks"] = file.CreationTimeUtc.Ticks; record["fileModifiedUtcTicks"] = file.LastWriteTimeUtc.Ticks;
            storage.State["fileDownloads"] = Data.Obj("baldursgate3:5508:42", record);
            File.WriteAllText(archive, "replaced fixture");
            File.SetLastWriteTimeUtc(archive, file.LastWriteTimeUtc.AddMinutes(5));
            WithController(storage, delegate(Controller controller)
            {
                int confirmed = 0, deleted = 0;
                controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { confirmed++; return true; }, delegate { deleted++; });
                Expect(confirmed == 0 && deleted == 0 && File.ReadAllText(archive) == "replaced fixture", "A stale recorded path was mistaken for the original app download.");
                Expect(!Data.Bool(storage.PublicState(), "clearingDownloadFolder"), "Replaced-file cleanup left a busy state.");
            });
        }

        private static void TestDownloadOwnershipBackup()
        {
            Storage original = NewStore("ownership-backup-original");
            string folder = CleanupFolder(original, "archives"), archive = Path.Combine(folder, "recorded.zip");
            File.WriteAllText(archive, "AAA fixture");
            var record = OwnedFile(archive, 42); var file = new FileInfo(archive);
            record["fileLength"] = file.Length; record["fileCreatedUtcTicks"] = file.CreationTimeUtc.Ticks; record["fileModifiedUtcTicks"] = file.LastWriteTimeUtc.Ticks;
            original.State["fileDownloads"] = Data.Obj("baldursgate3:5508:42", record);
            object backup = null;
            WithController(original, delegate(Controller controller) { backup = controller.ExportData(); });
            Storage restored = NewStore("ownership-backup-restored"); restored.Settings["downloadFolder"] = folder;
            WithController(restored, delegate(Controller controller)
            {
                Import(controller, backup);
                object saved = Data.Get(Data.Get(restored.State, "fileDownloads"), "baldursgate3:5508:42");
                foreach (string field in new[] { "fileLength", "fileCreatedUtcTicks", "fileModifiedUtcTicks" })
                    Expect(Data.Num(saved, field) == Data.Num(record, field), "Backup restore lost the " + field + " ownership field.");
                int confirmations = 0, deletions = 0;
                Func<DownloadFolderPlan, bool> confirm = delegate(DownloadFolderPlan plan)
                {
                    confirmations++; Expect(plan.Files.SequenceEqual(new[] { archive }), "The restored ownership record did not identify the original archive."); return false;
                };
                Action<string> delete = delegate { deletions++; };
                controller.ClearDownloadFolder(confirm, delete);
                Expect(confirmations == 1 && deletions == 0, "The intact restored archive was not eligible for confirmed cleanup.");
                File.WriteAllText(archive, "BBB fixture");
                File.SetLastWriteTimeUtc(archive, new DateTime(Data.Num(record, "fileModifiedUtcTicks"), DateTimeKind.Utc).AddMinutes(5));
                controller.ClearDownloadFolder(confirm, delete);
                Expect(confirmations == 1 && deletions == 0 && File.ReadAllText(archive) == "BBB fixture", "Restoring a backup downgraded fingerprint validation to a size-only match.");
            });
        }

        private static void TestCancelDownloadFolderCleanup()
        {
            Storage storage = NewStore("cancel-clear-folder");
            string folder = CleanupFolder(storage, "archives"), archive = Path.Combine(folder, "keep.zip");
            File.WriteAllText(archive, "keep fixture");
            RecordOwnedFile(storage, archive, 42);
            WithController(storage, delegate(Controller controller)
            {
                int confirmations = 0, deletes = 0;
                object result = controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { confirmations++; return false; }, delegate(string path) { deletes++; });
                Expect(confirmations == 1 && deletes == 0 && File.ReadAllText(archive) == "keep fixture", "Declining cleanup changed a file.");
                ExpectCleanupIdle(result, "Declined cleanup response");
                ExpectCleanupIdle(storage.PublicState(), "Declined cleanup public state");
                storage.Settings["downloadFolder"] = CleanupFolder(storage, "empty");
                controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { confirmations++; return true; }, delegate(string path) { deletes++; });
                Expect(confirmations == 1 && deletes == 0, "An empty download folder prompted for confirmation or deleted a file.");
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
                int confirmations = 0, deletes = 0;
                Func<DownloadFolderPlan, bool> confirm = delegate(DownloadFolderPlan plan) { confirmations++; return true; };
                Action<string> delete = delegate(string path) { deletes++; };
                var transfers = (Dictionary<string, CancellationTokenSource>)typeof(Controller).GetField("transfers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                var pending = (HashSet<string>)typeof(Controller).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                using (var active = new CancellationTokenSource())
                {
                    transfers["unlisted-transfer"] = active;
                    ExpectCleanupRejected(delegate { controller.ClearDownloadFolder(confirm, delete); }, "A transfer missing from the visible queue");
                    Expect(!active.IsCancellationRequested, "Folder cleanup cancelled an active transfer.");
                    transfers.Remove("unlisted-transfer");
                }
                pending.Add("unlisted-authorization");
                ExpectCleanupRejected(delegate { controller.ClearDownloadFolder(confirm, delete); }, "A pending authorization missing from the visible queue");
                pending.Clear();
                foreach (string status in new[] { "queued", "authorizing", "waiting-authorization", "retrying", "downloading" })
                {
                    storage.Downloads.Add(Download(42, status, 0, "2.2.3"));
                    ExpectCleanupRejected(delegate { controller.ClearDownloadFolder(confirm, delete); }, "The " + status + " queue entry");
                    Expect(Data.Str(storage.Downloads[0], "status") == status, "Cleanup changed a guarded download status.");
                    storage.Downloads.Clear();
                }
                Expect(confirmations == 0 && deletes == 0 && File.ReadAllText(archive) == "keep fixture", "Guarded cleanup reached confirmation or changed a file.");
                Expect(!Data.Bool(storage.State, "clearingDownloadFolder"), "Rejected cleanup retained the busy state.");
            });
        }

        private static void TestDownloadFolderBusyGuards()
        {
            Storage storage = NewStore("busy-clear-folder");
            string folder = CleanupFolder(storage, "archives"), archive = Path.Combine(folder, "keep.zip"), alternative = Path.Combine(root, "busy-clear-folder-uncreated");
            File.WriteAllText(archive, "keep fixture");
            RecordOwnedFile(storage, archive, 42);
            WithController(storage, delegate(Controller controller)
            {
                int deleted = 0;
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
                }, delegate(string path) { deleted++; });
                Expect(deleted == 0 && File.ReadAllText(archive) == "keep fixture" && !Data.Bool(storage.State, "clearingDownloadFolder"), "Reentrant cleanup checks changed a file or left cleanup busy.");
            });
        }

        private static void TestDownloadFolderFailureRecovery()
        {
            Storage storage = NewStore("failed-clear-folder");
            string folder = CleanupFolder(storage, "archives");
            string first = Path.Combine(folder, "a-main.zip"), second = Path.Combine(folder, "b-optional.zip");
            File.WriteAllText(first, "main fixture"); File.WriteAllText(second, "optional fixture");
            RecordOwnedFile(storage, first, 42); RecordOwnedFile(storage, second, 99);
            WithController(storage, delegate(Controller controller)
            {
                bool threw = false;
                try { controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { throw new IOException("Fixture confirmation failure"); }, delegate(string path) { throw new Exception("Deletion must not follow a failed confirmation."); }); }
                catch (IOException) { threw = true; }
                Expect(threw && !Data.Bool(storage.State, "clearingDownloadFolder") && File.Exists(first) && File.Exists(second), "Confirmation failure changed files or left cleanup busy.");
                ExpectCleanupIdle(storage.PublicState(), "Confirmation failure public state");
                ExpectCleanupIdle(Data.Json.DeserializeObject(File.ReadAllText(Path.Combine(storage.Root, "state.json"))), "Confirmation failure saved state");
                int processed = 0;
                object result = controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { return true; }, delegate(string path)
                {
                    processed++;
                    if (path == second) throw new IOException("Fixture locked file");
                    File.Delete(path);
                });
                Expect(Data.Str(result, "notice").Contains("Permanently deleted 1 file.") && Data.Str(result, "notice").Contains("1 file could not be deleted."), "Partial deletion should report both successful and failed counts.");
                Expect(processed == 2 && !File.Exists(first) && File.ReadAllText(second) == "optional fixture", "Partial deletion did not retain the failed file and delete the successful file.");
                ExpectCleanupIdle(result, "Partial delete failure response");
                ExpectCleanupIdle(storage.PublicState(), "Partial delete failure public state");
                controller.ClearDownloadFolder(delegate(DownloadFolderPlan plan) { return true; }, delegate(string path) { File.Delete(path); });
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
