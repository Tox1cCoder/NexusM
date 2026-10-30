using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace NexusM
{
    internal static class QueueBrowserTests
    {
        private static readonly List<object> checks = new List<object>();
        private static string root;

        [STAThread]
        private static int Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            root = Path.Combine(Path.GetTempPath(), "NexusM-QueueBrowserTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                Check("pending authorization requests deduplicate and cancellation removes only the selected job", Deduplication);
                Check("authorization deadline releases a stalled job while preserving it and the remaining queue", Deadline);
                Check("configured network timeout extends the bounded authorization deadline", ConfiguredDeadline);
                Check("mismatched and malformed Nexus links fail the selected job without starting another download", InvalidLinks);
                Check("late authorization deadlines cannot overwrite completed or cancelled jobs", TerminalStates);
                Check("late Nexus links after a timeout stay consumed without reopening a failed job", LateLinks);
            } finally {
                File.WriteAllText(args[0], Data.Json.Serialize(Data.Obj("passed", checks.All(c => Data.Bool(c, "passed")), "checks", checks)));
                string absolute = Path.GetFullPath(root), prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\NexusM-QueueBrowserTests-";
                if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe browser fixture cleanup path.");
                Directory.Delete(absolute, true);
            }
            return checks.All(c => Data.Bool(c, "passed")) ? 0 : 1;
        }
        private static void Check(string name, Action action) {
            try { action(); checks.Add(Data.Obj("name", name, "passed", true)); }
            catch (Exception ex) { checks.Add(Data.Obj("name", name, "passed", false, "error", ex.ToString())); }
        }
        private static void Expect(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private sealed class Fixture : IDisposable {
            internal readonly Storage Store;
            internal readonly MainForm Form;
            internal readonly Dictionary<string, object> First, Second;
            internal Fixture(string name) {
                Store = new Storage(Path.Combine(root, name));
                First = Job(1); Second = Job(2); Store.Downloads.Add(First); Store.Downloads.Add(Second); Store.Save();
                Form = new MainForm(Store, null, null, "queue-browser-fixture");
            }
            internal BrowserForm Browser() { return new BrowserForm(Form, null, "https://www.nexusmods.com/fixturegame/mods/1?tab=files&file_id=1&nmm=1", false, "", Store, Data.Str(First, "id")); }
            public void Dispose() { Form.Controller.Dispose(); Form.Dispose(); }
        }
        private static Dictionary<string, object> Job(int id) {
            return Data.Obj("id", "fixturegame:1:" + id, "gameDomain", "fixturegame", "modId", 1, "fileId", id, "status", "waiting-authorization", "resumeOnStartup", true);
        }
        private static void Started(BrowserForm browser, DateTime value) { typeof(BrowserForm).GetField("flowStarted", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(browser, value); }
        private static void Deduplication() {
            using (var f = new Fixture("dedup")) {
                f.Form.OpenAuthorization("https://www.nexusmods.com/first", Data.Str(f.First, "id"));
                f.Form.OpenAuthorization("https://www.nexusmods.com/first", Data.Str(f.First, "id"));
                f.Form.OpenAuthorization("https://www.nexusmods.com/second", Data.Str(f.Second, "id"));
                var queue = (Queue<string[]>)typeof(MainForm).GetField("authorizations", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.Form);
                Expect(queue.Count == 2, "Repeated requests duplicated a pending authorization.");
                f.Form.CancelAuthorization(Data.Str(f.First, "id"));
                Expect(queue.Count == 1 && queue.Peek()[1] == Data.Str(f.Second, "id"), "Cancelling one authorization removed another queued job.");
            }
        }
        private static void Deadline() {
            using (var f = new Fixture("deadline")) using (var browser = f.Browser()) {
                DateTime start = DateTime.UtcNow; Started(browser, start);
                typeof(BrowserForm).GetField("polling", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(browser, true);
                browser.CheckAuthorizationTimeout(start.AddSeconds(299));
                Expect(Data.Str(f.First, "status") == "waiting-authorization", "The job timed out before its deadline.");
                browser.CheckAuthorizationTimeout(start.AddSeconds(301));
                Expect(Data.Str(f.First, "status") == "failed" && Data.Str(f.First, "errorCode") == "authorization-timeout", "A stalled authorization did not become a retryable failure.");
                Expect(f.Store.Downloads.Count == 2 && Data.Str(f.Second, "status") == "waiting-authorization", "An authorization failure lost the queued next file.");
                var reopened = new Storage(f.Store.Root);
                Expect(reopened.Downloads.Count == 2 && Data.Str(reopened.Downloads[0], "errorCode") == "authorization-timeout", "The authorization failure was not durable.");
            }
        }
        private static void ConfiguredDeadline() {
            using (var f = new Fixture("configured")) {
                f.Store.Settings["downloadTimeoutSeconds"] = 600;
                using (var browser = f.Browser()) {
                    DateTime start = DateTime.UtcNow; Started(browser, start);
                    browser.CheckAuthorizationTimeout(start.AddSeconds(1199));
                    Expect(Data.Str(f.First, "status") == "waiting-authorization", "The longer configured deadline was ignored.");
                    browser.CheckAuthorizationTimeout(start.AddSeconds(1201));
                    Expect(Data.Str(f.First, "errorCode") == "authorization-timeout", "The configured authorization deadline was unbounded.");
                }
            }
        }
        private static void InvalidLinks() {
            string[] links = { "nxm://fixturegame/mods/1/files/99?key=fixture-secret&expires=2000000000&user_id=1", "nxm://fixturegame/mods/invalid/files/1?key=fixture-secret" };
            for (int i = 0; i < links.Length; i++) using (var f = new Fixture("invalid-" + i)) using (var browser = f.Browser()) {
                var capture = typeof(BrowserForm).GetMethod("CaptureNxm", BindingFlags.Instance | BindingFlags.NonPublic);
                Expect((bool)capture.Invoke(browser, new object[] { links[i] }), "The invalid link was not handled by the selected authorization window.");
                Expect(Data.Str(f.First, "status") == "failed" && f.Store.Downloads.Count == 2, "An invalid link started a different job or lost the selected one.");
                Expect(!Data.Json.Serialize(f.Store.State).Contains("fixture-secret"), "The invalid signed link leaked into saved diagnostics.");
            }
        }
        private static void TerminalStates() {
            foreach (string status in new[] { "completed", "cancelled" }) using (var f = new Fixture("terminal-" + status)) using (var browser = f.Browser()) {
                f.First["status"] = status;
                browser.CheckAuthorizationTimeout(DateTime.UtcNow.AddHours(1));
                Expect(Data.Str(f.First, "status") == status, "A late timeout overwrote a terminal state.");
            }
        }
        private static void LateLinks() {
            using (var f = new Fixture("late-link")) using (var browser = f.Browser()) {
                browser.CheckAuthorizationTimeout(DateTime.UtcNow.AddHours(1));
                var capture = typeof(BrowserForm).GetMethod("CaptureNxm", BindingFlags.Instance | BindingFlags.NonPublic);
                const string uri = "nxm://fixturegame/mods/1/files/1?key=fixture-secret&expires=2000000000&user_id=1";
                Expect((bool)capture.Invoke(browser, new object[] { uri }), "A late link escaped to the operating system's Nexus handler.");
                Expect(!browser.DownloadCaptured && Data.Str(f.First, "status") == "failed" && f.Store.Downloads.Count == 2, "A late link restarted the timed-out job.");
            }
        }
    }
}
