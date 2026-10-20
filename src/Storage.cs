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
        internal Storage(string root = null) {
            Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusM");
            Directory.CreateDirectory(Root);
            try { State = Data.Map(Data.Json.DeserializeObject(File.ReadAllText(StateFile))); } catch { State = new Dictionary<string, object>(); }
            if (!State.ContainsKey("games")) State["games"] = new List<object>(); else State["games"] = Data.List(State["games"]);
            if (!State.ContainsKey("mods")) State["mods"] = new List<object>(); else State["mods"] = Data.List(State["mods"]);
            if (!State.ContainsKey("downloads")) State["downloads"] = new List<object>(); else State["downloads"] = Data.List(State["downloads"]);
            if (!(Data.Get(State, "settings") is Dictionary<string, object>)) State["settings"] = new Dictionary<string, object>();
            string configuredFolder = Data.Get(Settings, "downloadFolder") as string;
            try { if (String.IsNullOrWhiteSpace(configuredFolder) || !RegexPath(configuredFolder)) configuredFolder = null; else configuredFolder = Path.GetFullPath(configuredFolder); } catch { configuredFolder = null; }
            Settings["downloadFolder"] = configuredFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NexusM");
            if (!(Data.Get(Settings, "yourGamesOnly") is bool)) Settings["yourGamesOnly"] = true;
            foreach (var entry in Downloads) {
                var row = Data.Map(entry);
                if (new[] { "downloading", "queued", "authorizing", "retrying", "waiting-authorization" }.Contains(Data.Str(row, "status"))) { row["status"] = "interrupted"; row["error"] = "The app closed before this download finished. Retry to authorize and resume."; }
            }
            State["connected"] = false;
            State.Remove("refreshProgress");
            State.Remove("clearingDownloadFolder");
        }
        internal List<object> Games { get { return (List<object>)State["games"]; } }
        private static bool RegexPath(string path) { return System.Text.RegularExpressions.Regex.IsMatch(path, @"^(?:[a-zA-Z]:[\\/]|\\\\[^\\]+\\[^\\]+)"); }
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
            State["schemaVersion"] = 1;
            State["downloads"] = Downloads.Take(500).ToList();
            var next = StateFile + ".tmp";
            File.WriteAllText(next, Data.Json.Serialize(State), new UTF8Encoding(false));
            if (File.Exists(StateFile)) File.Replace(next, StateFile, null); else File.Move(next, StateFile);
        }
    }
}
