using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NexusM
{
    internal sealed partial class Controller
    {
        private long lastQueueCheckpoint;
        private bool changingSettings, queuePersistenceFailed;
        private readonly HashSet<string> recoveryDelays = new HashSet<string>();
        private static readonly string[] TransportSettingKeys = { "maxParallelDownloads", "maxDownloadRetries", "downloadTimeoutSeconds", "retryDelaySeconds" };

        internal bool CanStartAuthorization {
            get { return Api != null && !disposed && !changingSettings && !clearingDownloadFolder && !preparingBatchDownload && transfers.Count < Data.Num(Store.Settings, "maxParallelDownloads"); }
        }
        private DownloadOptions QueueOptions() {
            return new DownloadOptions {
                MaxParallelDownloads = (int)Data.Num(Store.Settings, "maxParallelDownloads"),
                MaxDownloadRetries = (int)Data.Num(Store.Settings, "maxDownloadRetries"),
                DownloadTimeoutSeconds = (int)Data.Num(Store.Settings, "downloadTimeoutSeconds"),
                RetryDelaySeconds = (int)Data.Num(Store.Settings, "retryDelaySeconds")
            };
        }
        private static void ValidateQueueSettings(Dictionary<string, object> values) {
            string[] keys = { "maxParallelDownloads", "maxDownloadRetries", "downloadTimeoutSeconds", "retryDelaySeconds" };
            int[] minimum = { 1, 0, 15, 1 }, maximum = { 4, 10, 600, 60 };
            for (int i = 0; i < keys.Length; i++) if (values.ContainsKey(keys[i])) {
                object value = values[keys[i]]; long number = Data.Num(values, keys[i]);
                if (!(value is int || value is long) || number < minimum[i] || number > maximum[i])
                    throw new ArgumentException(keys[i] + " must be a whole number from " + minimum[i] + " to " + maximum[i] + ".");
            }
            if (values.ContainsKey("resumeDownloadsOnStartup") && !(values["resumeDownloadsOnStartup"] is bool))
                throw new ArgumentException("Automatic download recovery must be enabled or disabled.");
        }
        private long NextQueueOrder() {
            return Store.Downloads.Count == 0 ? 1 : Store.Downloads.Max(row => Data.Num(row, "queueOrder")) + 1;
        }
        private static void CheckRetryWindow(object row) {
            if (new[] { "rate-limited", "server-error" }.Contains(Data.Str(row, "errorCode")) && Data.Num(row, "retryAt") > Data.Now())
                throw new InvalidOperationException("Nexus asked this download to wait. Retry after the displayed server retry time.");
        }
        private static string QueueMessage(string message) {
            string safe = Regex.Replace(message ?? "", @"(?:https?|nxm)://[^\s<>]+", "[link removed]", RegexOptions.IgnoreCase);
            safe = Regex.Replace(safe, @"\b(?:apikey|api_key|key|token|signature|expires|user_id)\s*[:=]\s*[^\s&,;]+", "[redacted]", RegexOptions.IgnoreCase);
            safe = safe.Replace('\r', ' ').Replace('\n', ' ');
            return safe.Length > 400 ? safe.Substring(0, 400) : safe;
        }
        private static void QueueEvent(Dictionary<string, object> row, string code, string message) {
            var events = Data.List(Data.Get(row, "events"));
            events.Add(Data.Obj("time", Data.Now(), "code", code, "message", QueueMessage(message)));
            row["events"] = events.Skip(Math.Max(0, events.Count - 40)).ToList();
            row["updatedAt"] = Data.Now();
        }
        private bool SaveQueueState(Dictionary<string, object> row) {
            try { Store.State.Remove("queuePersistenceError"); Store.Save(); queuePersistenceFailed = false; return true; }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
            const string message = "Download progress could not be saved. The previously saved queue and partial files are kept; check free disk space and app data folder access before restarting.";
            if (!queuePersistenceFailed) { QueueEvent(row, "state_save_failed", message); window.Emit("notice", Data.Obj("message", message)); }
            queuePersistenceFailed = true; Store.State["queuePersistenceError"] = true; Store.State["notice"] = message;
            return false;
        }
        private void PersistQueueAdmission(Dictionary<string, object> row, object previous, int previousIndex) {
            string id = Data.Str(row, "id");
            Store.Downloads.RemoveAll(item => Data.Str(item, "id") == id); Store.Downloads.Insert(0, row);
            try { Store.Save(); }
            catch {
                Store.Downloads.Remove(row);
                if (previous != null) Store.Downloads.Insert(Math.Min(previousIndex, Store.Downloads.Count), previous);
                pending.Remove(id);
                if (previous != null && Data.Str(previous, "status") == "waiting-authorization") {
                    var old = Data.Map(previous); old["status"] = "interrupted"; old["resumeOnStartup"] = true; old["errorCode"] = "state_save_failed";
                    old["error"] = "The fresh authorization could not be saved. Check disk space and Retry to request another link."; Emit();
                }
                throw;
            }
        }
        internal void AuthorizationFailed(string id, string message, string code) {
            if (disposed) return;
            var row = Store.Downloads.Select(Data.Map).FirstOrDefault(item => Data.Str(item, "id") == id);
            if (row == null || Data.Str(row, "status") != "waiting-authorization") return;
            pending.Remove(id); row["status"] = "failed"; row["resumeOnStartup"] = false;
            row["error"] = QueueMessage(message); row["errorCode"] = code; row["needsUser"] = false; row["speed"] = 0L;
            CancelDownloadReplacement(id); QueueEvent(row, code, message); SaveQueueState(row); Emit();
        }
        private void RecoverCompletedDownloads() {
            foreach (var row in Store.Downloads.Select(Data.Map).Where(item => Data.Str(item, "status") == "completed" || (Data.Str(item, "status") == "interrupted" && Data.Bool(item, "resumeOnStartup"))).ToList()) {
                string domain; long modId = Data.Num(row, "modId"), fileId = Data.Num(row, "fileId"); object completed;
                try {
                    domain = JsonTools.Domain(Data.Str(row, "gameDomain"));
                    if (modId <= 0 || fileId <= 0) continue;
                    object folder = Data.Get(row, "folder");
                    if (folder == null && Data.Str(row, "path").Length > 0) folder = System.IO.Path.GetDirectoryName(Data.Str(row, "path"));
                    completed = DownloadService.RecoverCompletion(new DownloadRequest { Domain = domain, ModId = modId, FileId = fileId, Folder = Storage.NormalizeDownloadFolder(folder ?? Folder) });
                    if (completed == null) continue;
                }
                catch (Exception error) { row["error"] = "The saved archive could not be reconciled yet. " + QueueMessage(error.Message); row["errorCode"] = "recovery_check_failed"; continue; }
                foreach (var pair in Data.Map(completed)) row[pair.Key] = pair.Value;
                row["resumeOnStartup"] = false;
                QueueEvent(row, "completion_recovered", "Verified the completed archive left by the previous session; no download was repeated.");
                var history = Data.Map(Data.Get(Store.State, "fileDownloads")); history[Data.Str(row, "id")] = new Dictionary<string, object>(row); Store.State["fileDownloads"] = history;
                var mod = FindMod(domain, modId);
                if (mod != null && Data.Num(row, "downloadedAt") > Data.Num(mod, "lastDownloadedAt")) { mod["lastDownloadedAt"] = Data.Num(row, "downloadedAt"); mod["needsReview"] = Data.Num(mod, "updatedAt") > Math.Max(Data.Num(mod, "lastDownloadedAt"), Data.Num(mod, "reviewedAt")); }
                if (SaveQueueState(row)) DownloadService.AcknowledgeCompletion(row);
            }
        }
        private async Task ResumeAfterDelay(string id, long retryAt) {
            try {
                long seconds = Math.Max(1, Math.Min(3600, retryAt - Data.Now()));
                await Task.Delay(TimeSpan.FromSeconds(seconds), lifetime.Token);
                recoveryDelays.Remove(id);
                if (!disposed) ResumePendingDownloads();
            }
            catch (OperationCanceledException) { recoveryDelays.Remove(id); }
            catch (Exception error) {
                recoveryDelays.Remove(id);
                if (!disposed) Store.State["notice"] = "A saved download could not resume yet. " + QueueMessage(error.Message);
            }
        }
        internal void ResumePendingDownloads(Action<string, string> authorize = null) {
            if (disposed || changingSettings || Api == null || !Data.Bool(Store.State, "connected") || !Data.Bool(Store.Settings, "resumeDownloadsOnStartup")) return;
            foreach (var row in Store.Downloads.Select(Data.Map).OrderBy(item => Data.Num(item, "queueOrder")).ThenBy(item => Data.Num(item, "queuedAt")).ToList()) {
                string status = Data.Str(row, "status"), id = Data.Str(row, "id");
                if (!Data.Bool(row, "resumeOnStartup") || status != "interrupted" || pending.Contains(id) || transfers.ContainsKey(id)) continue;
                if (Data.Num(row, "retryAt") > Data.Now()) {
                    if (recoveryDelays.Add(id)) { var recovery = ResumeAfterDelay(id, Data.Num(row, "retryAt")); }
                    continue;
                }
                try {
                    string domain = JsonTools.Domain(Data.Str(row, "gameDomain")); long modId = Data.Num(row, "modId"), fileId = Data.Num(row, "fileId");
                    if (modId <= 0 || fileId <= 0 || id != domain + ":" + modId + ":" + fileId) throw new ArgumentException("The saved download has invalid Nexus identifiers. Remove it and choose the file again.");
                    row["folder"] = Storage.NormalizeDownloadFolder(Data.Get(row, "folder"));
                    row["status"] = "waiting-authorization"; row["authorizationMessage"] = "Restoring your saved queue and requesting a fresh Nexus download link.";
                    row["needsUser"] = false; row["error"] = ""; row["errorCode"] = ""; row["retryAt"] = 0L;
                    QueueEvent(row, "recovered", "Recovered the unfinished download; requesting fresh Nexus authorization.");
                    pending.Add(id);
                    if (!SaveQueueState(row)) {
                        pending.Remove(id); row["status"] = "interrupted"; row["resumeOnStartup"] = true; row["errorCode"] = "state_save_failed";
                        row["error"] = "Recovery is waiting for the queue to be saved. Check disk space and app data folder access, then Check for updates to continue.";
                        Emit(); continue;
                    }
                    Emit();
                    string url = "https://www.nexusmods.com/" + domain + "/mods/" + modId + "?tab=files&file_id=" + fileId + "&nmm=1";
                    if (authorize != null) authorize(url, id); else window.OpenAuthorization(url, id);
                }
                catch (Exception error) {
                    pending.Remove(id); row["status"] = "failed"; row["resumeOnStartup"] = false; row["errorCode"] = "recovery_failed"; row["error"] = QueueMessage(error.Message);
                    QueueEvent(row, "recovery_failed", error.Message); SaveQueueState(row); Emit();
                }
            }
        }
    }
}
