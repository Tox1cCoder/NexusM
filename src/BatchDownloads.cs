using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexusM
{
    internal sealed partial class Controller
    {
        private bool preparingBatchDownload;

        private List<Dictionary<string, object>> BatchMods(object value) {
            var requested = Data.List(value);
            if (requested.Count == 0 || requested.Count > 100) throw new ArgumentException("Select between 1 and 100 mods for a batch download.");
            var result = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>();
            foreach (var entry in requested) {
                string domain = JsonTools.Domain(Data.Str(entry, "gameDomain")); long modId = Data.Num(entry, "modId");
                if (modId <= 0 || FindMod(domain, modId) == null) throw new ArgumentException("Choose mods that are still in your library.");
                if (seen.Add(domain + ":" + modId)) result.Add(Data.Obj("gameDomain", domain, "modId", modId));
            }
            return result;
        }

        private static string BatchFileCategory(object file) {
            string name = Data.Str(file, "category_name").Trim().ToUpperInvariant().Replace(' ', '_');
            if (name.Length == 0) name = Data.Str(file, "category").Trim().ToUpperInvariant().Replace(' ', '_');
            if (name == "MAIN_FILES") name = "MAIN";
            if (name == "OPTIONAL_FILES") name = "OPTIONAL";
            if (name.Length == 0) {
                if (Data.Num(file, "category_id") == 1) name = "MAIN";
                else if (Data.Num(file, "category_id") == 3) name = "OPTIONAL";
            }
            return name;
        }

        private bool DownloadAuthorizationPending(string id) {
            if (transfers.ContainsKey(id) || pending.Contains(id)) return true;
            return Store.Downloads.Any(row => Data.Str(row, "id") == id && new[] { "queued", "authorizing", "retrying", "downloading", "waiting-authorization" }.Contains(Data.Str(row, "status")));
        }

        private async Task<Dictionary<string, object>> InspectBatchMod(Dictionary<string, object> mod) {
            string domain = Data.Str(mod, "gameDomain"); long modId = Data.Num(mod, "modId");
            var result = Data.Obj("gameDomain", domain, "modId", modId, "eligible", false, "fileId", 0L, "name", "", "status", "ineligible", "reason", "");
            try {
                var response = await Api.FilesAsync(domain, modId, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                object rawFiles = Data.Get(response, "files");
                if (!(rawFiles is object[]) && !(rawFiles is IList)) throw new NexusApiException("Nexus returned an unreadable file list. Try again.", 502);
                var files = Data.List(rawFiles);
                var mainFiles = files.Where(file => BatchFileCategory(file) == "MAIN").ToList();
                if (FindMod(domain, modId) == null) result["reason"] = "This mod is no longer in your library.";
                else if (files.Any(file => BatchFileCategory(file) == "OPTIONAL")) result["reason"] = "This mod has optional files. Open it to choose your files.";
                else if (mainFiles.Count != 1) result["reason"] = mainFiles.Count == 0 ? "This mod has no main file. Open it to choose your files." : "This mod has multiple main files. Open it to choose your files.";
                else if (Data.Num(mainFiles[0], "file_id") <= 0) throw new NexusApiException("Nexus returned an invalid main file. Try again.", 502);
                else {
                    var file = mainFiles[0]; long fileId = Data.Num(file, "file_id");
                    result["fileId"] = fileId; result["name"] = Data.Str(file, "name");
                    if (DownloadAuthorizationPending(domain + ":" + modId + ":" + fileId)) {
                        result["status"] = "alreadyQueued"; result["reason"] = "This file is already being prepared or downloaded.";
                    }
                    else { result["eligible"] = true; result["status"] = "eligible"; result["file"] = file; result["fileResponse"] = response; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { result["status"] = "failed"; result["reason"] = error.Message; }
            return result;
        }

        private async Task<Dictionary<string, object>[]> InspectBatchMods(List<Dictionary<string, object>> mods) {
            var results = new Dictionary<string, object>[mods.Count]; int next = -1;
            Func<Task> worker = async delegate {
                while (true) {
                    int index = Interlocked.Increment(ref next);
                    if (index >= mods.Count) return;
                    lifetime.Token.ThrowIfCancellationRequested();
                    results[index] = await InspectBatchMod(mods[index]);
                }
            };
            await Task.WhenAll(Enumerable.Range(0, Math.Min(4, mods.Count)).Select(index => worker()));
            return results;
        }

        private static object PublicBatchCandidate(Dictionary<string, object> candidate) {
            var result = new Dictionary<string, object>(candidate); result.Remove("file"); result.Remove("fileResponse"); return result;
        }

        private async Task<object> GetBatchDownloadCandidates(object value) {
            var mods = BatchMods(value); RequireApi();
            var candidates = await InspectBatchMods(mods);
            return Data.Obj("candidates", candidates.Select(PublicBatchCandidate).ToArray());
        }

        internal async Task<object> StartBatchDownload(object value, Action<string, string> authorize = null) {
            RequireFolderAvailable();
            if (preparingBatchDownload) throw new InvalidOperationException("A batch download is already being prepared.");
            var mods = BatchMods(value); RequireApi(); preparingBatchDownload = true;
            try {
                // A fresh file list prevents a previous checkbox check from choosing
                // a stale main file after the author adds another choice.
                var candidates = await InspectBatchMods(mods);
                int queued = 0;
                foreach (var candidate in candidates) {
                    lifetime.Token.ThrowIfCancellationRequested(); RequireFolderAvailable();
                    if (!Data.Bool(candidate, "eligible")) continue;
                    try {
                        if (FindMod(Data.Str(candidate, "gameDomain"), Data.Num(candidate, "modId")) == null) throw new InvalidOperationException("This mod is no longer in your library.");
                        object raw = Data.Get(candidate, "file");
                        long size = Data.Num(raw, "size_in_bytes"); if (size == 0) size = Data.Num(raw, "size_kb") * 1024; if (size == 0) size = Data.Num(raw, "size") * 1024;
                        var file = Data.Obj("name", Data.Str(raw, "name"), "size", size);
                        await NotifyDownloadingUpdate(Data.Str(candidate, "gameDomain"), Data.Num(candidate, "modId"), Data.Num(candidate, "fileId"), Data.Get(candidate, "fileResponse"));
                        bool added = QueueDownloadAuthorization(Data.Str(candidate, "gameDomain"), Data.Num(candidate, "modId"), Data.Num(candidate, "fileId"), file, authorize);
                        candidate["status"] = added ? "queued" : "alreadyQueued";
                        if (added) queued++;
                        else candidate["reason"] = "This file is already being prepared or downloaded.";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { candidate["status"] = "failed"; candidate["reason"] = error.Message; }
                }
                int failed = candidates.Count(candidate => Data.Str(candidate, "status") == "failed");
                int skipped = candidates.Length - queued - failed;
                Store.State["notice"] = "Queued " + queued + (queued == 1 ? " mod." : " mods.") + (skipped > 0 ? " " + skipped + " skipped." : "") + (failed > 0 ? " " + failed + " could not be prepared. Try again." : "");
                Store.Save(); Emit();
                return Data.Obj("state", Store.PublicState(), "batch", Data.Obj("queued", queued, "skipped", skipped, "failed", failed, "candidates", candidates.Select(PublicBatchCandidate).ToArray()));
            }
            finally { preparingBatchDownload = false; if (!disposed) window.NextAuthorization(); }
        }

        private bool QueueDownloadAuthorization(string domain, long modId, long fileId, object file, Action<string, string> authorize) {
            RequireFolderAvailable(); lifetime.Token.ThrowIfCancellationRequested();
            string id = domain + ":" + modId + ":" + fileId;
            CheckRetryWindow(Store.Downloads.FirstOrDefault(row => Data.Str(row, "id") == id));
            if (DownloadAuthorizationPending(id)) return false;
            object previous = Store.Downloads.FirstOrDefault(row => Data.Str(row, "id") == id); int previousIndex = previous == null ? 0 : Store.Downloads.IndexOf(previous);
            var queue = previous == null ? new Dictionary<string, object>() : new Dictionary<string, object>(Data.Map(previous));
            long order = NextQueueOrder();
            queue["id"] = id; queue["gameDomain"] = domain; queue["modId"] = modId; queue["fileId"] = fileId;
            queue["name"] = Data.Str(file, "name"); queue["status"] = "waiting-authorization"; queue["authorizationMessage"] = "Preparing the Nexus free download..."; queue["needsUser"] = false;
            queue["total"] = Data.Num(file, "size"); queue["folder"] = Data.Str(previous, "status") == "completed" ? Folder : Data.Get(queue, "folder") ?? Folder;
            queue["queuedAt"] = Data.Now(); queue["queueOrder"] = order; queue["resumeOnStartup"] = true; queue["speed"] = 0L; queue["retryAt"] = 0L;
            queue["attempt"] = 0L; queue["error"] = ""; queue["errorCode"] = "";
            QueueEvent(queue, "queued", "Added to the saved queue. Waiting for fresh Nexus authorization.");
            PersistQueueAdmission(queue, previous, previousIndex); pending.Add(id); Emit();
            try {
                if (authorize != null) authorize("https://www.nexusmods.com/" + domain + "/mods/" + modId + "?tab=files&file_id=" + fileId + "&nmm=1", id);
                else window.OpenAuthorization("https://www.nexusmods.com/" + domain + "/mods/" + modId + "?tab=files&file_id=" + fileId + "&nmm=1", id);
            }
            catch (Exception error) { pending.Remove(id); queue["status"] = "failed"; queue["resumeOnStartup"] = false; queue["error"] = QueueMessage(error.Message); queue["errorCode"] = "authorization_failed"; QueueEvent(queue, "authorization_failed", error.Message); Store.Save(); Emit(); throw; }
            return true;
        }
    }
}
