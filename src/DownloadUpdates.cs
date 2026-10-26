using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexusM
{
    internal static class DownloadUpdateResolver
    {
        internal static object Resolve(long oldFileId, object response)
        {
            var files = Data.List(Data.Get(response, "files"));
            var edges = Data.List(Data.Get(response, "file_updates"))
                .Where(edge => Data.Num(edge, "old_file_id") > 0 && Data.Num(edge, "new_file_id") > 0)
                .GroupBy(edge => Data.Num(edge, "old_file_id"))
                .ToDictionary(group => group.Key, group => group.Select(edge => Data.Num(edge, "new_file_id")).Distinct().ToArray());
            long current = oldFileId;
            var visited = new HashSet<long>();
            while (visited.Add(current)) {
                long[] next;
                if (!edges.TryGetValue(current, out next)) {
                    if (current == oldFileId) return null;
                    var matches = files.Where(file => Data.Num(file, "file_id") == current && Active(file)).ToList();
                    return matches.Count == 1 ? matches[0] : null;
                }
                // Branches can represent choices between different variants.
                if (next.Length != 1) return null;
                current = next[0];
            }
            return null;
        }

        private static bool Active(object file)
        {
            string category = Data.Str(file, "category_name").Trim().ToUpperInvariant().Replace(' ', '_');
            long id = Data.Num(file, "category_id");
            return id != 4 && id != 6 && id != 7 && category != "OLD_VERSION" && category != "OLD_VERSIONS" && category != "DELETED" && category != "ARCHIVED";
        }
    }

    internal sealed partial class Controller
    {
        private Task downloadUpdatesTask;
        internal int DownloadUpdateCheckFailures { get; private set; }
        private readonly HashSet<string> downloadUpdateActions = new HashSet<string>();
        private string DownloadUpdateCheckNotice {
            get { return DownloadUpdateCheckFailures == 0 ? "" : " File updates for " + DownloadUpdateCheckFailures + (DownloadUpdateCheckFailures == 1 ? " downloaded mod" : " downloaded mods") + " could not be checked. Previous notifications were kept."; }
        }

        private DownloadFolderPlan RecordedArchivePlan(object row)
        {
            string path = Data.Str(row, "path");
            if (String.IsNullOrWhiteSpace(path)) return null;
            try {
                return DownloadFolderCleanup.PrepareRecordedFile(Path.GetDirectoryName(path), new[] { Store.Root, AppDomain.CurrentDomain.BaseDirectory }, row);
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (System.Security.SecurityException) { }
            return null;
        }

        private bool RetainedArchive(object row)
        {
            if (Data.Str(row, "status") != "completed") return false;
            var plan = RecordedArchivePlan(row);
            return plan != null && plan.Files.Count == 1;
        }

        private static string DownloadModKey(object row) { return Data.Str(row, "gameDomain") + ":" + Data.Num(row, "modId"); }
        private static string ArchiveKey(object row)
        {
            return Data.Json.Serialize(new object[] { Data.Str(row, "path"), Data.Num(row, "fileLength"), Data.Num(row, "fileCreatedUtcTicks"), Data.Num(row, "fileModifiedUtcTicks") });
        }

        internal Task CheckDownloadedUpdates()
        {
            if (downloadUpdatesTask != null && !downloadUpdatesTask.IsCompleted) return downloadUpdatesTask;
            downloadUpdatesTask = CheckDownloadedUpdatesCore();
            return downloadUpdatesTask;
        }

        private async Task CheckDownloadedUpdatesCore()
        {
            RequireApi();
            var retained = Store.Downloads.Where(RetainedArchive).ToList();
            var retainedIds = new HashSet<string>(retained.Select(row => Data.Str(row, "id")));
            var results = Data.List(Data.Get(Store.State, "downloadUpdates"))
                .Where(update => retainedIds.Contains(Data.Str(update, "id"))).ToList();
            var groups = retained.GroupBy(DownloadModKey).ToArray();
            var successful = new List<object>[groups.Length];
            int next = -1, failed = 0;
            Func<Task> worker = async delegate {
                while (true) {
                    int index = Interlocked.Increment(ref next);
                    if (index >= groups.Length) return;
                    lifetime.Token.ThrowIfCancellationRequested();
                    var group = groups[index];
                    try {
                        var sample = group.First();
                        var response = await Api.FilesAsync(Data.Str(sample, "gameDomain"), Data.Num(sample, "modId"), lifetime.Token);
                        if (!(Data.Get(response, "files") is object[]) && !(Data.Get(response, "files") is IList))
                            throw new NexusApiException("Nexus returned an unreadable file list.", 502);
                        lifetime.Token.ThrowIfCancellationRequested();
                        var found = new List<object>();
                        foreach (var row in group) {
                            if (!Store.Downloads.Contains(row) || !RetainedArchive(row)) continue;
                            object target = DownloadUpdateResolver.Resolve(Data.Num(row, "fileId"), response);
                            if (target != null || HasUnlinkedModUpdate(row)) found.Add(DownloadUpdate(row, target));
                        }
                        successful[index] = found;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { Interlocked.Increment(ref failed); /* Keep the last successful result until Nexus is reachable. */ }
                }
            };
            await Task.WhenAll(Enumerable.Range(0, Math.Min(4, groups.Length)).Select(index => worker()));
            if (disposed) return;
            DownloadUpdateCheckFailures = failed;
            for (int index = 0; index < groups.Length; index++) {
                if (successful[index] == null) continue;
                results.RemoveAll(update => DownloadModKey(update) == groups[index].Key);
                results.AddRange(successful[index]);
            }
            // A Keep choice can arrive while another mod's request is still pending.
            foreach (var update in results) Data.Map(update)["dismissed"] = Convert.ToString(Data.Get(Data.Get(Store.State, "dismissedDownloadUpdates"), Data.Str(update, "id"))) == Data.Str(update, "updateKey");
            Store.State["downloadUpdates"] = results;
            PruneDownloadedUpdates();
            Store.Save(); Emit();
        }

        internal void PruneDownloadedUpdates()
        {
            Store.State["downloadUpdates"] = Data.List(Data.Get(Store.State, "downloadUpdates"))
                .Where(update => Store.Downloads.Any(row => Data.Str(row, "id") == Data.Str(update, "id") &&
                    Data.Str(update, "archiveKey") == ArchiveKey(row) && RetainedArchive(row))).ToList();
        }

        private bool HasUnlinkedModUpdate(object row)
        {
            var mod = FindMod(Data.Str(row, "gameDomain"), Data.Num(row, "modId"));
            string version = Data.Str(mod, "version");
            return version.Length > 0 && Data.Str(row, "version").Length > 0 && version != Data.Str(row, "version") &&
                Data.Num(mod, "updatedAt") > Data.Num(row, "downloadedAt");
        }

        private Dictionary<string, object> DownloadUpdate(object row, object target)
        {
            string id = Data.Str(row, "id"), domain = Data.Str(row, "gameDomain"); long modId = Data.Num(row, "modId"), targetId = Data.Num(target, "file_id");
            var mod = FindMod(domain, modId);
            bool reviewOnly = target == null;
            string updateKey = reviewOnly ? "mod:" + Data.Num(mod, "updatedAt") : "file:" + targetId;
            string dismissed = Convert.ToString(Data.Get(Data.Get(Store.State, "dismissedDownloadUpdates"), id));
            return Data.Obj("id", id, "gameDomain", domain, "modId", modId, "modName", Data.Str(mod, "name"),
                "oldFileId", Data.Num(row, "fileId"), "oldName", Data.Str(row, "name"), "oldFilename", Data.Str(row, "filename"), "oldVersion", Data.Str(row, "version"), "oldPath", Data.Str(row, "path"),
                "fileId", targetId, "name", Data.Str(target, "name"), "filename", Data.Str(target, "file_name"), "version", reviewOnly ? Data.Str(mod, "version") : Data.Str(target, "version"),
                "reviewOnly", reviewOnly, "updateKey", updateKey, "archiveKey", ArchiveKey(row), "dismissed", dismissed == updateKey);
        }

        internal async Task<object> HandleDownloadedUpdate(string action, Dictionary<string, object> p, Action<string, string> authorize = null, Action<string> delete = null)
        {
            RequireFolderAvailable();
            string id = Data.Str(p, "id");
            if (!downloadUpdateActions.Add(id)) throw new InvalidOperationException("This download update is already being handled.");
            try {
                var row = Store.Downloads.FirstOrDefault(item => Data.Str(item, "id") == id && RetainedArchive(item));
                if (row == null) throw new InvalidOperationException("The old archive is no longer available or has changed. Check for updates again.");
                var update = Data.List(Data.Get(Store.State, "downloadUpdates")).FirstOrDefault(item => Data.Str(item, "id") == id);
                if (update == null || Data.Num(p, "fileId") != Data.Num(update, "fileId") || Data.Str(p, "updateKey") != Data.Str(update, "updateKey") ||
                    Data.Str(p, "archiveKey") != Data.Str(update, "archiveKey") || Data.Str(update, "archiveKey") != ArchiveKey(row))
                    throw new InvalidOperationException("This update or old archive has changed. Check for updates again.");
                if (action == "dismissDownloadUpdate") {
                    // A later Keep choice supersedes a queued replacement's delete intent.
                    CancelDownloadReplacement(id);
                    var dismissed = Data.Map(Data.Get(Store.State, "dismissedDownloadUpdates")); dismissed[id] = Data.Str(update, "updateKey"); Store.State["dismissedDownloadUpdates"] = dismissed;
                    Data.Map(update)["dismissed"] = true; Store.Save(); Emit(); return Store.PublicState();
                }
                if (action != "replaceDownloadUpdate" && action != "deleteDownloadUpdate") throw new ArgumentException("Unknown download update action.");
                RequireApi();
                string domain = Data.Str(row, "gameDomain"); long modId = Data.Num(row, "modId");
                var response = await Api.FilesAsync(domain, modId, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested(); RequireFolderAvailable();
                var target = DownloadUpdateResolver.Resolve(Data.Num(row, "fileId"), response);
                bool reviewOnly = Data.Bool(update, "reviewOnly");
                if (reviewOnly ? target != null || !HasUnlinkedModUpdate(row) || Data.Str(update, "updateKey") != "mod:" + Data.Num(FindMod(domain, modId), "updatedAt") : target == null || Data.Num(target, "file_id") != Data.Num(update, "fileId"))
                    throw new InvalidOperationException("The available update changed. Check for updates again before continuing.");
                if (!Store.Downloads.Contains(row) || !RetainedArchive(row)) throw new InvalidOperationException("The old archive changed. It was kept.");
                if (DownloadAuthorizationPending(id)) throw new InvalidOperationException("Finish the old file's pending download first.");
                if (action == "deleteDownloadUpdate") {
                    if (HasActiveTransfers || pending.Count > 0) throw new InvalidOperationException("Finish or cancel pending downloads before deleting the old archive.");
                    DeleteUpdatedArchive(row, delete);
                }
                else {
                    if (reviewOnly) throw new InvalidOperationException("Open this mod to review and choose its files. Nexus did not identify a single replacement for the old archive.");
                    long fileId = Data.Num(target, "file_id"); string targetId = domain + ":" + modId + ":" + fileId;
                    if (DownloadAuthorizationPending(targetId)) throw new InvalidOperationException("The newer file is already being downloaded. Wait for it to finish.");
                    var replacements = Data.Map(Data.Get(Store.State, "downloadReplacements"));
                    replacements[id] = Data.Obj("old", new Dictionary<string, object>(Data.Map(row)), "targetId", targetId, "fileId", fileId);
                    Store.State["downloadReplacements"] = replacements; Store.Save();
                    try {
                        details.Remove(domain + ":" + modId);
                        if (authorize != null) {
                            if (!QueueDownloadAuthorization(domain, modId, fileId, target, authorize)) throw new InvalidOperationException("The newer file is already being prepared.");
                        }
                        else await AuthorizeDownload(domain, modId, fileId);
                    }
                    catch { replacements.Remove(id); Store.Save(); throw; }
                    Data.Map(update)["dismissed"] = true;
                    var dismissed = Data.Map(Data.Get(Store.State, "dismissedDownloadUpdates")); dismissed[id] = Data.Str(update, "updateKey"); Store.State["dismissedDownloadUpdates"] = dismissed;
                }
                Store.Save(); Emit(); return Store.PublicState();
            }
            finally { downloadUpdateActions.Remove(id); }
        }

        private void DeleteUpdatedArchive(object row, Action<string> delete)
        {
            var plan = RecordedArchivePlan(row);
            if (plan == null || plan.Files.Count != 1) throw new InvalidOperationException("The old archive changed or is no longer available. It was kept.");
            var result = DownloadFolderCleanup.Delete(plan, delete);
            if (result.Deleted != 1) throw new IOException("The old archive could not be deleted or changed while being checked. It was kept in Downloads.");
            string id = Data.Str(row, "id");
            var history = Data.Map(Data.Get(Store.State, "fileDownloads"));
            if (Data.Num(Data.Get(history, id), "downloadedAt") <= Data.Num(row, "downloadedAt")) history[id] = new Dictionary<string, object>(Data.Map(row));
            Store.State["fileDownloads"] = history;
            Store.Downloads.RemoveAll(item => Data.Str(item, "id") == id);
            Store.State["downloadUpdates"] = Data.List(Data.Get(Store.State, "downloadUpdates")).Where(item => Data.Str(item, "id") != id).ToList();
            Data.Map(Data.Get(Store.State, "downloadReplacements")).Remove(id);
        }

        internal void CancelDownloadReplacement(string targetId)
        {
            var replacements = Data.Map(Data.Get(Store.State, "downloadReplacements"));
            foreach (string key in replacements.Where(pair => Data.Str(pair.Value, "targetId") == targetId || pair.Key == targetId).Select(pair => pair.Key).ToArray()) replacements.Remove(key);
        }

        internal void CompleteDownloadReplacement(object final, Action<string> delete = null)
        {
            if (Data.Str(final, "status") != "completed") return;
            string targetId = Data.Str(final, "id");
            var replacements = Data.Map(Data.Get(Store.State, "downloadReplacements"));
            foreach (var pair in replacements.Where(item => Data.Str(item.Value, "targetId") == targetId).ToArray()) {
                object old = Data.Get(pair.Value, "old");
                replacements.Remove(pair.Key);
                if (!RetainedArchive(final)) {
                    Store.State["notice"] = "The updated archive was downloaded, but its saved file identity could not be verified. The old archive was kept.";
                    continue;
                }
                var retained = Store.Downloads.FirstOrDefault(row => Data.Str(row, "id") == pair.Key);
                if (retained == null || Data.Str(old, "path").Equals(Data.Str(final, "path"), StringComparison.OrdinalIgnoreCase) ||
                    Data.Str(retained, "path") != Data.Str(old, "path") || new[] { "fileLength", "fileCreatedUtcTicks", "fileModifiedUtcTicks" }.Any(field => Data.Num(retained, field) != Data.Num(old, field))) continue;
                try {
                    DeleteUpdatedArchive(old, delete);
                    Store.State["notice"] = "The updated archive was downloaded and the old version was deleted.";
                }
                catch (Exception ex) { Store.State["notice"] = "The updated archive was downloaded. " + ex.Message; }
            }
        }
    }
}
