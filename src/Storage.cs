using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace NexusM
{
    internal static class Data
    {
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 80 };
        internal static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        internal static object Get(object value, string key) { object result; return Map(value).TryGetValue(key, out result) ? result : null; }
        internal static string Str(object value, string key) { return Convert.ToString(Get(value, key), System.Globalization.CultureInfo.InvariantCulture) ?? ""; }
        internal static long Num(object value, string key) { long result; return long.TryParse(Str(value, key), out result) ? result : 0; }
        internal static bool Bool(object value, string key) { bool result; return bool.TryParse(Str(value, key), out result) && result; }
        internal static List<object> List(object value) { var enumerable = value as IEnumerable; return enumerable == null || value is string || value is IDictionary ? new List<object>() : enumerable.Cast<object>().ToList(); }
        internal static Dictionary<string, object> Obj(params object[] values) { var result = new Dictionary<string, object>(); for (int i = 0; i + 1 < values.Length; i += 2) result[(string)values[i]] = values[i + 1]; return result; }
        internal static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }
        internal static long Timestamp(object value) {
            if (value == null) return 0;
            long number; if (long.TryParse(Convert.ToString(value), out number)) return number > 100000000000 ? number / 1000 : number;
            DateTimeOffset date; return DateTimeOffset.TryParse(Convert.ToString(value), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out date) ? (long)(date.UtcDateTime - new DateTime(1970, 1, 1)).TotalSeconds : 0;
        }
    }

    internal sealed class Storage
    {
        internal readonly string Root;
        internal Dictionary<string, object> State;
        private string StateFile { get { return Path.Combine(Root, "state.json"); } }
        private string BackupFile { get { return StateFile + ".bak"; } }
        private bool restoredBackup, stateWriteBlocked;
        internal Storage(string root = null) {
            Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusM");
            Directory.CreateDirectory(Root);
            State = ReadState(StateFile);
            if (State == null) {
                State = ReadState(BackupFile);
                restoredBackup = State != null;
                if (File.Exists(StateFile)) PreserveUnreadable(StateFile);
                if (State == null && File.Exists(BackupFile)) PreserveUnreadable(BackupFile);
                State = State ?? new Dictionary<string, object>();
                if (restoredBackup) State["notice"] = "Recovered your library and download queue from the previous saved state. The unreadable state file was preserved.";
                else if (File.Exists(StateFile) || File.Exists(BackupFile)) State["notice"] = "The saved state and its backup could not be read. The unreadable files were preserved in the app data folder; restore a library backup before queuing downloads.";
            }
            if (!State.ContainsKey("games")) State["games"] = new List<object>(); else State["games"] = Data.List(State["games"]);
            if (!State.ContainsKey("mods")) State["mods"] = new List<object>(); else State["mods"] = Data.List(State["mods"]);
            if (!State.ContainsKey("downloads")) State["downloads"] = new List<object>(); else State["downloads"] = Data.List(State["downloads"]);
            if (!(Data.Get(State, "settings") is Dictionary<string, object>)) State["settings"] = new Dictionary<string, object>();
            string configuredFolder;
            try { configuredFolder = NormalizeDownloadFolder(Data.Get(Settings, "downloadFolder")); } catch { configuredFolder = null; }
            Settings["downloadFolder"] = configuredFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NexusM");
            if (!(Data.Get(Settings, "yourGamesOnly") is bool)) Settings["yourGamesOnly"] = true;
            NormalizeIntegerSetting("maxParallelDownloads", 2, 1, 4);
            NormalizeIntegerSetting("maxDownloadRetries", 3, 0, 10);
            NormalizeIntegerSetting("downloadTimeoutSeconds", 90, 15, 600);
            NormalizeIntegerSetting("retryDelaySeconds", 2, 1, 60);
            if (!(Data.Get(Settings, "resumeDownloadsOnStartup") is bool)) Settings["resumeDownloadsOnStartup"] = true;
            long sequence = Downloads.Count == 0 ? 0 : Downloads.Max(row => Data.Num(row, "queueOrder"));
            foreach (var entry in Downloads.AsEnumerable().Reverse()) {
                var row = Data.Map(entry);
                if (row.ContainsKey("speed")) row["speed"] = 0L;
                bool serverWait = new[] { "rate-limited", "server-error" }.Contains(Data.Str(row, "errorCode")) && Data.Num(row, "retryAt") > Data.Now();
                if (row.ContainsKey("retryAt") && !serverWait) row["retryAt"] = 0L;
                string status = Data.Str(row, "status");
                if (!new[] { "completed", "cancelled", "canceled", "failed" }.Contains(status)) {
                    if (Data.Str(row, "folder").Length == 0) row["folder"] = Settings["downloadFolder"];
                    if (Data.Num(row, "queuedAt") <= 0) row["queuedAt"] = Data.Now();
                    if (Data.Num(row, "queueOrder") <= 0) row["queueOrder"] = ++sequence;
                }
                if (new[] { "downloading", "queued", "authorizing", "retrying", "waiting-authorization", "awaiting-authorization", "waiting", "awaiting_authorization" }.Contains(status)) {
                    row["status"] = "interrupted"; row["resumeOnStartup"] = true;
                    if (!serverWait) row["errorCode"] = "app_closed";
                    row["error"] = Data.Bool(Settings, "resumeDownloadsOnStartup") ? "Waiting to reconnect and resume this saved download." : "The app closed before this download finished. Retry to authorize and resume.";
                }
                else if (status == "interrupted" && !(Data.Get(row, "resumeOnStartup") is bool)) row["resumeOnStartup"] = true;
                else if (row.ContainsKey("resumeOnStartup") && new[] { "completed", "cancelled", "canceled", "failed" }.Contains(status)) row["resumeOnStartup"] = false;
            }
            State["connected"] = false;
            State.Remove("refreshProgress");
            State.Remove("clearingDownloadFolder");
            State.Remove("downloadUpdates");
            State.Remove("downloadReplacements");
        }
        private static Dictionary<string, object> ReadState(string path) {
            try { return Data.Json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch { return null; }
        }
        private void PreserveUnreadable(string path) {
            try { File.Copy(path, Path.Combine(Root, "state-unreadable-" + Guid.NewGuid().ToString("N") + ".json")); }
            catch (IOException) { stateWriteBlocked = true; }
            catch (UnauthorizedAccessException) { stateWriteBlocked = true; }
        }
        private void NormalizeIntegerSetting(string key, int fallback, int minimum, int maximum) {
            object value = Data.Get(Settings, key); long number = Data.Num(Settings, key);
            Settings[key] = (value is int || value is long) && number >= minimum && number <= maximum ? (int)number : fallback;
        }
        internal List<object> Games { get { return (List<object>)State["games"]; } }
        internal static string NormalizeDownloadFolder(object value) {
            const string message = "Enter a valid absolute download folder path, such as D:\\Downloads.";
            string folder = value as string;
            if (String.IsNullOrWhiteSpace(folder) || !System.Text.RegularExpressions.Regex.IsMatch(folder, @"^(?:[a-zA-Z]:[\\/]|\\\\[^\\]+\\[^\\]+)") ||
                folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || folder.IndexOfAny(new[] { '*', '?' }) >= 0 || folder.Substring(2).Contains(':'))
                throw new ArgumentException(message);
            try { return Path.GetFullPath(folder); }
            catch (ArgumentException) { throw new ArgumentException(message); }
            catch (NotSupportedException) { throw new ArgumentException(message); }
            catch (PathTooLongException) { throw new ArgumentException(message); }
        }
        internal List<object> Mods { get { return (List<object>)State["mods"]; } }
        internal List<object> Downloads { get { return (List<object>)State["downloads"]; } }
        internal Dictionary<string, object> Settings { get { return Data.Map(State["settings"]); } }
        internal string ReadKey() {
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(Root, "api-key.bin")), null, DataProtectionScope.CurrentUser)); } catch { return ""; }
        }
        internal void WriteKey(string key) {
            var target = Path.Combine(Root, "api-key.bin");
            if (String.IsNullOrWhiteSpace(key)) { if (File.Exists(target)) File.Delete(target); return; }
            File.WriteAllBytes(target, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
        }
        internal object PublicState() {
            Settings["hasApiKey"] = ReadKey().Length > 0;
            State["schemaVersion"] = 1;
            return State;
        }
        internal void Save() {
            if (stateWriteBlocked) throw new IOException("The unreadable saved state could not be preserved. Check access to the app data folder before saving or queuing downloads.");
            State["schemaVersion"] = 1;
            var next = StateFile + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(Data.Json.Serialize(State));
            using (var file = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            if (File.Exists(StateFile)) File.Replace(next, StateFile, restoredBackup ? null : BackupFile); else File.Move(next, StateFile);
            restoredBackup = false;
        }
    }
}
