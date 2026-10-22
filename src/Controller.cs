using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NexusM
{
    internal sealed class Controller : IDisposable
    {
        internal readonly Storage Store;
        internal NexusApi Api;
        internal bool HasActiveTransfers { get { return transfers.Count > 0 || clearingDownloadFolder; } }
        private DownloadService downloader;
        private readonly MainForm window;
        private bool initialized;
        private bool disposed, clearingDownloadFolder;
        private Task initializationTask, refreshTask;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private List<object> catalogue;
        private readonly Dictionary<string, CancellationTokenSource> transfers = new Dictionary<string, CancellationTokenSource>();
        private readonly Dictionary<string, object> details = new Dictionary<string, object>();
        private readonly HashSet<string> pending = new HashSet<string>();
        internal Controller(MainForm form, Storage storage) { window = form; Store = storage; ResetApi(); }
        private void ResetApi() {
            string key = Store.ReadKey();
            if (Api != null) Api.Dispose();
            if (downloader != null) downloader.Dispose();
            Api = key.Length == 0 ? null : new NexusApi(key);
            downloader = Api == null ? null : new DownloadService(Api, DownloadProgress);
        }
        private void RequireApi() { if (Api == null) throw new InvalidOperationException("Add your personal Nexus API key in Settings."); }
        internal async Task<object> Handle(string action, object payload) {
            var p = Data.Map(payload);
            if (action == "getState") return Store.PublicState();
            if (action == "initialize") { await Initialize(); return Store.PublicState(); }
            if (action == "saveSettings") {
                string folder = null;
                if (p.ContainsKey("downloadFolder")) { RequireFolderAvailable(); folder = Storage.NormalizeDownloadFolder(p["downloadFolder"]); }
                if (p.ContainsKey("yourGamesOnly") && !(p["yourGamesOnly"] is bool)) throw new ArgumentException("Your games filter must be enabled or disabled.");
                if (p.ContainsKey("apiKey") && !String.IsNullOrWhiteSpace(Data.Str(p, "apiKey"))) {
                    if (transfers.Count > 0) throw new InvalidOperationException("Finish or cancel active downloads before changing your API key.");
                    string key = Data.Str(p, "apiKey").Trim();
                    using (var candidate = new NexusApi(key)) { await candidate.ValidateAsync(); }
                    if (initializationTask != null) await initializationTask;
                    if (refreshTask != null) await refreshTask;
                    Store.WriteKey(key); ResetApi(); initialized = false; initializationTask = null; await Connect();
                }
                if (folder != null) { RequireFolderAvailable(); Directory.CreateDirectory(folder); Store.Settings["downloadFolder"] = folder; }
                if (p.ContainsKey("yourGamesOnly")) Store.Settings["yourGamesOnly"] = (bool)p["yourGamesOnly"];
                Store.Save(); Emit(); return Store.PublicState();
            }
            if (action == "browseFolder") {
                RequireFolderAvailable();
                using (var dialog = new FolderBrowserDialog { Description = "Choose where NexusM saves mod archives", SelectedPath = Folder, ShowNewFolderButton = true }) {
                    if (dialog.ShowDialog(window) == DialogResult.OK) { RequireFolderAvailable(); return Data.Obj("path", Storage.NormalizeDownloadFolder(dialog.SelectedPath)); }
                }
                return Data.Obj("cancelled", true);
            }
            if (action == "syncGames" || (action == "importWebsite" && Data.Str(p, "kind") == "favorites")) { await SyncGames(); Emit(); return Store.PublicState(); }
            if (action == "addGame") { await AddGame(Data.Str(p, "query")); Emit(); return Store.PublicState(); }
            if (action == "removeGame") { string domain = Data.Str(p, "domain"); Store.Games.RemoveAll(x => Data.Str(x, "domain") == domain); var excluded = Data.List(Data.Get(Store.State, "excludedGames")); if (!excluded.Any(x => Convert.ToString(x) == domain)) excluded.Add(domain); Store.State["excludedGames"] = excluded; Store.Save(); Emit(); return Store.PublicState(); }
            if (action == "addMod") {
                var parsed = ParseModUrl(Data.Str(p, "url"));
                await EnsureGame(Data.Str(parsed, "gameDomain"));
                await RefreshOne(Data.Str(parsed, "gameDomain"), Data.Num(parsed, "modId"));
                Store.Save(); Emit(); return Store.PublicState();
            }
            if (action == "removeMod") { Store.Mods.RemoveAll(x => Data.Str(x, "gameDomain") == Data.Str(p, "gameDomain") && Data.Num(x, "modId") == Data.Num(p, "modId")); Store.Save(); Emit(); return Store.PublicState(); }
            if (action == "refresh") {
                if (initializationTask != null && !initializationTask.IsCompleted) await initializationTask;
                else await Refresh(Data.Str(p, "gameDomain"));
                Emit(); return Store.PublicState();
            }
            if (action == "getModDetails") return await Details(Data.Str(p, "gameDomain"), Data.Num(p, "modId"));
            if (action == "startDownload") { await AuthorizeDownload(Data.Str(p, "gameDomain"), Data.Num(p, "modId"), Data.Num(p, "fileId")); return Store.PublicState(); }
            if (action == "cancelDownload") {
                string id = Data.Str(p, "id");
                CancellationTokenSource cancel; if (transfers.TryGetValue(id, out cancel)) cancel.Cancel();
                var row = Store.Downloads.FirstOrDefault(x => Data.Str(x, "id") == id);
                if (row != null && !transfers.ContainsKey(id)) { Data.Map(row)["status"] = "cancelled"; pending.Remove(id); window.CancelAuthorization(id); Store.Save(); Emit(); }
                return Store.PublicState();
            }
            if (action == "clearDownloads") {
                var activeStatuses = new HashSet<string>(new[] { "queued", "authorizing", "retrying", "downloading" });
                var removable = Store.Downloads.Where(x => !transfers.ContainsKey(Data.Str(x, "id")) && !activeStatuses.Contains(Data.Str(x, "status"))).ToList();
                var fileHistory = Data.Map(Data.Get(Store.State, "fileDownloads"));
                foreach (var row in removable) {
                    string id = Data.Str(row, "id");
                    if (Data.Str(row, "status") == "completed") {
                        string fileKey = Data.Str(row, "gameDomain") + ":" + Data.Num(row, "modId") + ":" + Data.Num(row, "fileId");
                        if (Data.Num(row, "modId") > 0 && Data.Num(row, "fileId") > 0 && Data.Num(Data.Get(fileHistory, fileKey), "downloadedAt") <= Data.Num(row, "downloadedAt")) fileHistory[fileKey] = new Dictionary<string, object>(Data.Map(row));
                    }
                    pending.Remove(id); Store.Downloads.Remove(row);
                }
                Store.State["fileDownloads"] = fileHistory;
                foreach (var row in removable) window.CancelAuthorization(Data.Str(row, "id"));
                Store.Save(); Emit(); return Store.PublicState();
            }
            if (action == "clearDownloadFolder") return ClearDownloadFolder();
            if (action == "retryDownload") {
                RequireFolderAvailable();
                var row = Store.Downloads.FirstOrDefault(x => Data.Str(x, "id") == Data.Str(p, "id"));
                if (row == null) throw new ArgumentException("That download is no longer in the queue.");
                await AuthorizeDownload(Data.Str(row, "gameDomain"), Data.Num(row, "modId"), Data.Num(row, "fileId")); return Store.PublicState();
            }
            if (action == "openFolder") { Directory.CreateDirectory(Folder); System.Diagnostics.Process.Start("explorer.exe", "\"" + Folder + "\""); return null; }
            if (action == "openUrl") { window.OpenSafeUrl(Data.Str(p, "url")); return null; }
            if (action == "registerNxm") { RegisterNxm(); Store.Settings["registerNxm"] = true; Store.Save(); Emit(); return Store.PublicState(); }
            if (action == "importWebsite") { window.OpenHistory(Data.Str(p, "gameDomain")); return Data.Obj("opened", true); }
            if (action == "importTracked") { await ImportTracked(); Emit(); return Store.PublicState(); }
            if (action == "importData") { await ImportText(Data.Str(p, "text")); Emit(); return Store.PublicState(); }
            if (action == "exportData") {
                using (var dialog = new SaveFileDialog { Filter = "NexusM backup (*.json)|*.json", FileName = "NexusM-backup-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json" })
                    if (dialog.ShowDialog(window) == DialogResult.OK) File.WriteAllText(dialog.FileName, Data.Json.Serialize(ExportData()), new System.Text.UTF8Encoding(false));
                return Data.Obj("saved", true);
            }
            if (action == "clearCache") { await window.ClearCache(); return Data.Obj("cleared", true); }
            if (action == "markReviewed") {
                var mod = FindMod(Data.Str(p, "gameDomain"), Data.Num(p, "modId"));
                if (mod != null) { mod["reviewedAt"] = Data.Now(); mod["needsReview"] = false; Store.Save(); Emit(); }
                return Store.PublicState();
            }
            throw new ArgumentException("Unknown app action: " + action);
        }
        private string Folder { get { return Data.Str(Store.Settings, "downloadFolder"); } }
        private void RequireFolderAvailable() {
            if (clearingDownloadFolder) throw new InvalidOperationException("Wait for download folder cleanup to finish.");
        }
        private void RequireIdleDownloads() {
            var finished = new HashSet<string>(new[] { "completed", "failed", "cancelled", "canceled", "interrupted" });
            if (transfers.Count > 0 || pending.Count > 0 || Store.Downloads.Any(x => !finished.Contains(Data.Str(x, "status"))))
                throw new InvalidOperationException("Finish or cancel pending downloads before clearing the download folder.");
        }
        internal object ClearDownloadFolder(Func<DownloadFolderPlan, bool> confirm = null, Action<string> recycle = null) {
            RequireFolderAvailable(); RequireIdleDownloads();
            var plan = DownloadFolderCleanup.Prepare(Folder, new[] { Store.Root, AppDomain.CurrentDomain.BaseDirectory });
            if (plan.Files.Count == 0) {
                Store.State["notice"] = "There are no files to clear in the download folder. Subfolders were kept.";
                Store.Save(); Emit(); return Store.PublicState();
            }
            clearingDownloadFolder = true; Store.State["clearingDownloadFolder"] = true; Emit();
            try {
                bool approved = confirm != null ? confirm(plan) : MessageBox.Show(window,
                    "Move " + plan.Files.Count + " files from this folder to the Recycle Bin?\n\n" + plan.Folder +
                    "\n\nThis includes downloaded archives, unfinished downloads, and other files directly in this folder. Subfolders and your download history will be kept.",
                    "Clear download folder", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                if (!approved) return Store.PublicState();
                if (disposed) throw new OperationCanceledException();
                RequireIdleDownloads();
                var result = DownloadFolderCleanup.Recycle(plan, recycle);
                Store.State["notice"] = "Moved " + result.Moved + " files to the Recycle Bin." +
                    (result.Cancelled ? " Cleanup was cancelled." : "") +
                    (result.Failed > 0 ? " " + result.Failed + " files could not be moved." : "") +
                    (result.Skipped > 0 ? " " + result.Skipped + " files were skipped." : "") +
                    " Subfolders and download history were kept.";
                return Store.PublicState();
            }
            finally {
                clearingDownloadFolder = false; Store.State.Remove("clearingDownloadFolder");
                if (!disposed) { Store.Save(); Emit(); window.NextAuthorization(); }
            }
        }
        private Task Initialize() {
            if (Api == null) return Task.FromResult(0);
            if (initialized) return initializationTask ?? Task.FromResult(0);
            initialized = true; initializationTask = InitializeCore(); return initializationTask;
        }
        private async Task InitializeCore() {
            Progress(true, "connecting", 0, 0, "Connecting to Nexus Mods...");
            try {
                await Connect(); Emit();
                string gamesNotice = "";
                try { await SyncGames(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { gamesNotice = " Your saved games were kept because game sync failed: " + ex.Message; }
                Emit(); await Refresh("");
                if (gamesNotice.Length > 0) { Store.State["notice"] = Data.Str(Store.State, "notice") + gamesNotice; Store.Save(); Emit(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!disposed) { Store.State["notice"] = "Your saved library is available offline. " + ex.Message; Store.Save(); Emit(); } }
            finally { if (!disposed) { Progress(false, "", 0, 0, ""); Emit(); } }
        }
        private void Progress(bool running, string phase, int done, int total, string message) {
            if (disposed) return;
            var progress = Data.Obj("running", running, "phase", phase, "done", done, "total", total, "message", message);
            Store.State["refreshProgress"] = progress; window.Emit("refreshProgress", progress);
        }
        private async Task Connect() {
            RequireApi(); var account = Data.Map(await Api.ValidateAsync(lifetime.Token));
            Store.State["user"] = Data.Obj("name", Data.Str(account, "name"), "isPremium", Data.Bool(account, "is_premium"), "id", Data.Num(account, "user_id"));
            Store.State["connected"] = true; Store.State.Remove("notice");
        }
        internal async Task SyncGames() {
            RequireApi();
            var response = await Api.SendGraphqlAsync("query NexusMFavourites { favouriteGames { id name domainName artworkSchema } gameArtwork { schemaV1 { tile } schemaV2 { tile } } }", null, lifetime.Token);
            var favorites = Data.List(Data.Get(response, "favouriteGames"));
            var replacement = new List<object>();
            foreach (var game in favorites) {
                string domain = Data.Str(game, "domainName");
                if (Data.List(Data.Get(Store.State, "excludedGames")).Any(x => Convert.ToString(x) == domain)) continue;
                var existing = Store.Games.FirstOrDefault(x => Data.Str(x, "domain") == domain);
                var row = Data.Map(existing ?? Data.Obj("id", Data.Num(game, "id"), "domain", domain, "name", Data.Str(game, "name")));
                row["artworkSchema"] = Data.Str(game, "artworkSchema");
                string template = Data.Str(Data.Get(Data.Get(response, "gameArtwork"), Data.Str(game, "artworkSchema") == "V2" ? "schemaV2" : "schemaV1"), "tile");
                row["imageUrl"] = String.IsNullOrEmpty(template) ? GameImage(Data.Num(game, "id"), Data.Str(game, "artworkSchema")) : template.Replace("{gameId}", Data.Num(game, "id").ToString());
                row["localOnly"] = false;
                replacement.Add(row);
            }
            foreach (var local in Store.Games.Where(x => Data.Bool(x, "localOnly"))) if (!replacement.Any(x => Data.Str(x, "domain") == Data.Str(local, "domain"))) replacement.Add(local);
            Store.State["games"] = replacement;
            Store.State["lastGamesSyncAt"] = Data.Now(); Store.Save();
        }
        private static string GameImage(long id, string schema) {
            return schema == "V2" ? "https://images.nexusmods.com/images/games/v2/" + id + "/tile.jpg" : "https://images.nexusmods.com/images/games/4_3/tile_" + id + ".jpg";
        }
        private async Task AddGame(string query) {
            RequireApi(); query = (query ?? "").Trim();
            if (query.Length == 0) throw new ArgumentException("Enter a game name or Nexus game URL.");
            Uri uri;
            if (Uri.TryCreate(query, UriKind.Absolute, out uri) && IsNexus(uri)) {
                var segments = uri.AbsolutePath.Trim('/').Split('/');
                query = segments.Length > 1 && segments[0] == "games" ? segments[1] : segments[0];
            }
            if (catalogue == null) catalogue = Data.List(await Api.GamesAsync());
            var exact = catalogue.FirstOrDefault(x => Data.Str(x, "domain_name").Equals(query, StringComparison.OrdinalIgnoreCase) || Data.Str(x, "name").Equals(query, StringComparison.OrdinalIgnoreCase));
            if (exact == null) {
                var matches = catalogue.Where(x => Data.Str(x, "name").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Take(8).ToList();
                if (matches.Count != 1) throw new ArgumentException(matches.Count == 0 ? "No Nexus game matched. Try its URL or game domain." : "Several games matched: " + String.Join(", ", matches.Select(x => Data.Str(x, "name"))) + ". Enter an exact name or URL.");
                exact = matches[0];
            }
            await EnsureGame(Data.Str(exact, "domain_name")); Store.Save();
        }
        private async Task EnsureGame(string domain) {
            domain = JsonTools.Domain(domain);
            var excluded = Data.List(Data.Get(Store.State, "excludedGames")); excluded.RemoveAll(x => Convert.ToString(x) == domain); Store.State["excludedGames"] = excluded;
            if (Store.Games.Any(x => Data.Str(x, "domain") == domain)) return;
            RequireApi(); var raw = Data.Map(await Api.GameAsync(domain));
            Store.Games.Add(Data.Obj("id", Data.Num(raw, "id"), "domain", domain, "name", Data.Str(raw, "name"), "imageUrl", GameImage(Data.Num(raw, "id"), "V1"), "localOnly", true));
        }
        internal static Dictionary<string, object> ParseModUrl(string input) {
            Uri uri;
            if (!Uri.TryCreate((input ?? "").Trim(), UriKind.Absolute, out uri) || !IsNexus(uri) || uri.Scheme != "https") throw new ArgumentException("Enter a Nexus Mods HTTPS mod-page URL.");
            var match = Regex.Match(uri.AbsolutePath, "^/(?:games/)?([a-zA-Z0-9_-]+)/mods/([1-9][0-9]*)/?$");
            long id;
            if (!match.Success || !long.TryParse(match.Groups[2].Value, out id)) throw new ArgumentException("The link must point to a mod page, such as nexusmods.com/baldursgate3/mods/123.");
            return Data.Obj("gameDomain", JsonTools.Domain(match.Groups[1].Value), "modId", id);
        }
        internal static bool IsNexus(Uri uri) { return uri.Host == "nexusmods.com" || uri.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase); }
        private Dictionary<string, object> FindMod(string domain, long id) { return Store.Mods.Select(Data.Map).FirstOrDefault(x => Data.Str(x, "gameDomain") == domain && Data.Num(x, "modId") == id); }
        private Dictionary<string, object> NormalizeMod(string domain, long id, object raw, Dictionary<string, object> old) {
            var result = old ?? Data.Obj("gameDomain", domain, "modId", id);
            foreach (var pair in new[] { new[] {"name", "name"}, new[] {"summary", "summary"}, new[] {"version", "version"}, new[] {"author", "author"}, new[] {"pictureUrl", "picture_url"} }) result[pair[0]] = Data.Str(raw, pair[1]);
            result["updatedAt"] = Data.Num(raw, "updated_timestamp");
            result["uid"] = Data.Num(raw, "uid");
            if (Data.Num(result, "updatedAt") == 0) result["updatedAt"] = Data.Timestamp(Data.Get(raw, "updated_time"));
            result["url"] = "https://www.nexusmods.com/" + domain + "/mods/" + id;
            result["needsReview"] = Data.Num(result, "updatedAt") > Math.Max(Data.Num(result, "lastDownloadedAt"), Data.Num(result, "reviewedAt"));
            result["available"] = Data.Bool(raw, "available"); result.Remove("error");
            return result;
        }
        private async Task RefreshOne(string domain, long id) {
            RequireApi(); var raw = await Api.ModAsync(domain, id);
            var old = FindMod(domain, id); var updated = NormalizeMod(domain, id, raw, old);
            if (old == null) Store.Mods.Add(updated);
        }
        private Task Refresh(string domain) {
            if (refreshTask != null && !refreshTask.IsCompleted) return refreshTask;
            refreshTask = RefreshCore(domain); return refreshTask;
        }
        private async Task ResolveModUids(List<object> selected) {
            var gameIds = new Dictionary<string, long>();
            foreach (var game in Store.Games) if (Data.Num(game, "id") > 0) gameIds[Data.Str(game, "domain")] = Data.Num(game, "id");
            foreach (var entry in selected) {
                long uid = Data.Num(entry, "uid");
                if (uid > 0 && (uid & UInt32.MaxValue) == Data.Num(entry, "modId") && !gameIds.ContainsKey(Data.Str(entry, "gameDomain"))) gameIds[Data.Str(entry, "gameDomain")] = uid >> 32;
            }
            var groups = selected.GroupBy(x => Data.Str(x, "gameDomain")).ToArray();
            await Task.WhenAll(groups.Select(async group => {
                long gameId;
                if (!gameIds.TryGetValue(group.Key, out gameId)) {
                    try { gameId = Data.Num(await Api.GameAsync(group.Key, lifetime.Token), "id"); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { foreach (var entry in group) { Data.Map(entry)["uid"] = 0L; Data.Map(entry)["error"] = ex.Message; } return; }
                }
                foreach (var entry in group) {
                    long modId = Data.Num(entry, "modId");
                    if (gameId > 0 && gameId <= Int32.MaxValue && modId > 0 && modId <= UInt32.MaxValue) Data.Map(entry)["uid"] = (gameId << 32) | modId;
                    else { Data.Map(entry)["uid"] = 0L; Data.Map(entry)["error"] = "The Nexus game or mod ID could not be resolved. Previous information was kept."; }
                }
            }));
        }
        private void ApplyBatchMod(object entry, object raw) {
            var mod = Data.Map(entry);
            foreach (var pair in new[] { new[] { "name", "name" }, new[] { "summary", "summary" }, new[] { "version", "version" }, new[] { "author", "author" }, new[] { "pictureUrl", "pictureUrl" } }) {
                string value = Data.Str(raw, pair[1]);
                if (!String.IsNullOrWhiteSpace(value)) mod[pair[0]] = value;
            }
            mod["updatedAt"] = Data.Timestamp(Data.Get(raw, "updatedAt"));
            long downloadedAt = Data.Timestamp(Data.Get(raw, "viewerDownloaded"));
            if (downloadedAt > Data.Num(mod, "lastDownloadedAt")) mod["lastDownloadedAt"] = downloadedAt;
            mod["available"] = Data.Str(raw, "status").Equals("published", StringComparison.OrdinalIgnoreCase);
            mod["needsReview"] = Data.Num(mod, "updatedAt") > Math.Max(Data.Num(mod, "lastDownloadedAt"), Data.Num(mod, "reviewedAt"));
            mod["lastCheckedAt"] = Data.Now(); mod.Remove("error");
            details.Remove(Data.Str(mod, "gameDomain") + ":" + Data.Num(mod, "modId"));
        }
        private async Task RefreshCore(string domain) {
            RequireApi(); var activeDomains = new HashSet<string>(Store.Games.Select(x => Data.Str(x, "domain")));
            bool yourGamesOnly = Data.Bool(Store.Settings, "yourGamesOnly");
            var selected = Store.Mods.Where(x => String.IsNullOrEmpty(domain) ? !yourGamesOnly || activeDomains.Contains(Data.Str(x, "gameDomain")) : Data.Str(x, "gameDomain") == domain).ToList();
            int done = 0, failed = 0;
            Progress(true, "mods", 0, selected.Count, "Checking current mod information...");
            try {
                if (selected.Count == 0) { Store.State["notice"] = "Import your Nexus download history or add a mod URL first."; return; }
                await ResolveModUids(selected);
                var resolved = selected.Where(x => Data.Num(x, "uid") > 0).ToList();
                done = failed = selected.Count - resolved.Count;
                // Four workers bound network concurrency and update the UI after each batch.
                int next = 0;
                Func<Task> worker = async delegate {
                    while (next < resolved.Count) {
                        lifetime.Token.ThrowIfCancellationRequested();
                        var batch = resolved.Skip(next).Take(NexusApi.ModBatchSize).ToList(); next += batch.Count;
                        try {
                            var nodes = Data.List(await Api.ModsByUidAsync(batch.Select(x => Data.Num(x, "uid")), lifetime.Token)).ToDictionary(x => Data.Num(x, "uid"));
                            foreach (var entry in batch) {
                                object node;
                                if (nodes.TryGetValue(Data.Num(entry, "uid"), out node)) ApplyBatchMod(entry, node);
                                else { Data.Map(entry)["error"] = "Nexus did not return this mod. Its previous information was kept."; failed++; }
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { foreach (var entry in batch) Data.Map(entry)["error"] = ex.Message; failed += batch.Count; }
                        done += batch.Count;
                        Progress(true, "mods", done, selected.Count, "Checked " + done + " of " + selected.Count + " mods."); Emit();
                    }
                };
                await Task.WhenAll(Enumerable.Range(0, Math.Min(4, Math.Max(1, (resolved.Count + NexusApi.ModBatchSize - 1) / NexusApi.ModBatchSize))).Select(x => worker()));
                Store.State["lastCheckedAt"] = Data.Now();
                Store.State["notice"] = failed == 0 ? "Update check complete." : "Checked " + done + " mods; " + failed + " could not be reached. Their previous information was kept.";
                Store.State["rateLimits"] = Api.RateLimits;
            }
            finally {
                if (!disposed) {
                    Progress(false, "", done, selected.Count, ""); Store.Save(); Emit();
                }
            }
        }
        private async Task<object> Details(string domain, long id) {
            RequireApi(); domain = JsonTools.Domain(domain); string cacheKey = domain + ":" + id;
            object cached; if (details.TryGetValue(cacheKey, out cached)) return cached;
            var rawMod = await Api.ModAsync(domain, id);
            var normalized = NormalizeMod(domain, id, rawMod, FindMod(domain, id));
            normalized = new Dictionary<string, object>(normalized); normalized["description"] = Data.Str(rawMod, "description");
            var rawFiles = await Api.FilesAsync(domain, id);
            var files = new List<object>();
            foreach (var file in Data.List(Data.Get(rawFiles, "files"))) {
                long fileId = Data.Num(file, "file_id");
                object saved = Data.Get(Data.Get(Store.State, "fileDownloads"), domain + ":" + id + ":" + fileId);
                if (saved == null) saved = Store.Downloads.FirstOrDefault(x => Data.Str(x, "gameDomain") == domain && Data.Num(x, "modId") == id && Data.Num(x, "fileId") == fileId && Data.Str(x, "status") == "completed");
                long size = Data.Num(file, "size_in_bytes"); if (size == 0) size = Data.Num(file, "size_kb") * 1024; if (size == 0) size = Data.Num(file, "size") * 1024;
                files.Add(Data.Obj("fileId", fileId, "name", Data.Str(file, "name"), "version", Data.Str(file, "version"), "category", Data.Str(file, "category_name"), "categoryName", Data.Str(file, "category_name"), "description", Data.Str(file, "description"), "changelog", Data.Str(file, "changelog_html"), "size", size, "uploadedAt", Data.Num(file, "uploaded_timestamp"), "downloadedAt", Data.Num(saved, "downloadedAt"), "url", Data.Str(normalized, "url") + "?tab=files&file_id=" + fileId));
            }
            object changes = null; string notes = "";
            try { changes = await Api.ChangelogsAsync(domain, id); } catch { notes = "Some changelogs could not be loaded. Open on Nexus for the complete page."; }
            var requirements = new List<object>();
            try {
                var rawRequirements = await Api.RequirementsAsync(domain, id);
                foreach (var req in Data.List(Data.Get(Data.Get(rawRequirements, "nexusRequirements"), "nodes"))) requirements.Add(Data.Obj("name", Data.Str(req, "modName"), "url", Data.Str(req, "url"), "notes", Data.Str(req, "notes")));
                foreach (var req in Data.List(Data.Get(rawRequirements, "dlcRequirements"))) requirements.Add(Data.Obj("name", Data.Str(Data.Get(req, "gameExpansion"), "name"), "url", "", "notes", Data.Str(req, "notes")));
            } catch { notes = "Requirements could not be loaded through the API. Read the author’s description and Open on Nexus for requirements and pinned posts."; }
            var result = Data.Obj("mod", normalized, "files", files, "changelogs", changes ?? new Dictionary<string, object>(), "requirements", requirements, "requirementsNotice", notes.Length == 0 ? "Read the author's description and changelog for installation instructions. Pinned posts and newly changed requirements may need Open on Nexus." : notes, "fileUpdates", Data.Get(rawFiles, "file_updates"));
            if (details.Count >= 20) details.Clear(); details[cacheKey] = result;
            return result;
        }
        private async Task ImportTracked() {
            RequireApi(); var tracked = Data.List(await Api.TrackedModsAsync());
            foreach (var mod in tracked) { string domain = Data.Str(mod, "domain_name"); if (domain == "") continue; await EnsureGame(domain); await RefreshOne(domain, Data.Num(mod, "mod_id")); }
            Store.State["notice"] = tracked.Count == 0 ? "Your Nexus tracked-mod list is empty. Import download history or add mod URLs." : "Imported " + tracked.Count + " tracked mods.";
            Store.Save();
        }
        internal async Task ImportHistory(object items) {
            int count = 0;
            foreach (var entry in Data.List(items)) {
                string domain = Data.Str(entry, "gameDomain"); long id = Data.Num(entry, "modId");
                if (domain == "" || id <= 0) continue;
                try { domain = JsonTools.Domain(domain); } catch { continue; }
                var old = FindMod(domain, id);
                if (old == null) { old = Data.Obj("gameDomain", domain, "modId", id, "name", Data.Str(entry, "name"), "pictureUrl", Data.Str(entry, "thumbnailUrl"), "url", "https://www.nexusmods.com/" + domain + "/mods/" + id); Store.Mods.Add(old); }
                long downloaded = Data.Timestamp(Data.Get(entry, "lastDownloadedAt")); if (downloaded == 0) downloaded = Data.Timestamp(Data.Get(entry, "lastDownloadedUtc"));
                if (downloaded > Data.Num(old, "lastDownloadedAt")) old["lastDownloadedAt"] = downloaded;
                if (Data.Num(entry, "reviewedAt") > Data.Num(old, "reviewedAt")) old["reviewedAt"] = Data.Num(entry, "reviewedAt");
                foreach (string field in new[] { "name", "author", "pictureUrl", "version", "summary" }) if (!String.IsNullOrWhiteSpace(Data.Str(entry, field))) old[field] = Data.Str(entry, field);
                long updated = Data.Timestamp(Data.Get(entry, "updatedAt")); if (updated == 0) updated = Data.Timestamp(Data.Get(entry, "updatedUtc"));
                if (updated > 0) old["updatedAt"] = updated;
                old["needsReview"] = Data.Num(old, "updatedAt") > Math.Max(Data.Num(old, "lastDownloadedAt"), Data.Num(old, "reviewedAt")); count++;
            }
            // The import can include games outside current favourites; keep the website favourites strip intact.
            Store.State["notice"] = "Imported " + count + " mod history entries."; Store.Save(); Emit();
            if (count > 0 && initialized && Api != null) {
                if (initializationTask != null) await initializationTask;
                if (refreshTask != null) await refreshTask;
                await Refresh("");
            }
        }
        private async Task ImportText(string text) {
            if (String.IsNullOrWhiteSpace(text) || text.Length > 24 * 1024 * 1024) throw new ArgumentException("Choose a JSON/HTML history file or paste up to 24 MB of history content.");
            object parsed = null; try { parsed = Data.Json.DeserializeObject(text); } catch { }
            if (parsed != null) {
                var map = Data.Map(parsed);
                var games = Data.List(Data.Get(map, "games"));
                foreach (var game in games) { string domain = Data.Str(game, "domain").ToLowerInvariant(); if (Regex.IsMatch(domain, "^[a-zA-Z0-9_-]{1,100}$") && !Store.Games.Any(x => Data.Str(x, "domain") == domain)) Store.Games.Add(Data.Obj("id", Data.Num(game, "id"), "domain", domain, "name", Data.Str(game, "name"), "imageUrl", Data.Str(game, "imageUrl"), "localOnly", Data.Bool(game, "localOnly"))); }
                if (map.ContainsKey("excludedGames")) Store.State["excludedGames"] = Data.List(map["excludedGames"]).Select(x => Convert.ToString(x).ToLowerInvariant()).Where(x => Regex.IsMatch(x, "^[a-z0-9_-]{1,100}$")).Distinct().Cast<object>().ToList();
                var entries = map.ContainsKey("mods") ? Data.Get(map, "mods") : map.ContainsKey("items") ? Data.Get(map, "items") : parsed;
                var rows = Data.List(entries);
                if (rows.Count == 0 && !(Data.Num(map, "schemaVersion") == 1 && map.ContainsKey("games") && map.ContainsKey("mods"))) throw new ArgumentException("No history entries were found. A NexusM JSON backup or an array of gameDomain/modId records is supported.");
                await ImportHistory(rows);
                foreach (var record in Data.List(Data.Get(map, "downloads"))) RestoreDownload(record);
                foreach (var record in Data.Map(Data.Get(map, "fileDownloads")).Values) RestoreDownload(record);
                Store.Save(); return;
            }
            var imported = new List<object>();
            var rowsHtml = Regex.Matches(text, "<tr\\b[^>]*>([\\s\\S]*?)</tr>", RegexOptions.IgnoreCase);
            foreach (Match row in rowsHtml) {
                var link = Regex.Match(row.Value, "(?:https?://(?:www\\.)?nexusmods\\.com)?/(?:games/)?([a-zA-Z0-9_-]+)/mods/([1-9][0-9]*)", RegexOptions.IgnoreCase);
                if (!link.Success) continue;
                var title = Regex.Match(row.Value, "<a[^>]*href=[\"'][^\"']*/mods/" + link.Groups[2].Value + "[^\"']*[\"'][^>]*>([\\s\\S]*?)</a>", RegexOptions.IgnoreCase);
                string name = System.Net.WebUtility.HtmlDecode(Regex.Replace(title.Groups[1].Value, "<[^>]+>", "")).Trim();
                imported.Add(Data.Obj("gameDomain", link.Groups[1].Value, "modId", long.Parse(link.Groups[2].Value), "name", name));
            }
            if (imported.Count == 0) {
                foreach (Match link in Regex.Matches(text, "https://(?:www\\.)?nexusmods\\.com/(?:games/)?([a-zA-Z0-9_-]+)/mods/([1-9][0-9]*)", RegexOptions.IgnoreCase)) imported.Add(Data.Obj("gameDomain", link.Groups[1].Value, "modId", long.Parse(link.Groups[2].Value), "name", "Mod " + link.Groups[2].Value));
            }
            if (imported.Count == 0) throw new ArgumentException("No mod-page URLs were found. Use Import from Nexus for full history dates, or import JSON/HTML containing Nexus mod links.");
            await ImportHistory(imported);
            Store.State["notice"] = "Imported " + imported.Count + " mod links. This text did not include reliable download dates; use the Nexus history importer to preserve them."; Store.Save();
        }
        private async Task AuthorizeDownload(string domain, long modId, long fileId) {
            RequireFolderAvailable(); RequireApi(); domain = JsonTools.Domain(domain); if (modId <= 0 || fileId <= 0) throw new ArgumentException("Choose a mod file first.");
            string id = domain + ":" + modId + ":" + fileId;
            if (transfers.ContainsKey(id)) throw new InvalidOperationException("This file is already downloading.");
            var detail = await Details(domain, modId);
            RequireFolderAvailable();
            var file = Data.List(Data.Get(detail, "files")).FirstOrDefault(x => Data.Num(x, "fileId") == fileId);
            if (file == null) throw new ArgumentException("This file is no longer listed by Nexus.");
            if (Data.Str(file, "category").Equals("ARCHIVED", StringComparison.OrdinalIgnoreCase) || Data.Str(file, "category").Equals("DELETED", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("This file is unavailable. Choose an active file or open its Nexus page.");
            Store.Downloads.RemoveAll(x => Data.Str(x, "id") == id);
            var queue = Data.Obj("id", id, "gameDomain", domain, "modId", modId, "fileId", fileId, "name", Data.Str(file, "name"), "status", "waiting-authorization", "authorizationMessage", "Preparing the Nexus free download...", "needsUser", false, "bytes", 0, "total", Data.Num(file, "size"));
            Store.Downloads.Insert(0, queue); pending.Add(id); Store.Save(); Emit();
            window.OpenAuthorization("https://www.nexusmods.com/" + domain + "/mods/" + modId + "?tab=files&file_id=" + fileId + "&nmm=1", id);
        }
        internal async Task ReceiveNxm(string uri) {
            RequireFolderAvailable(); RequireApi(); var request = DownloadRequest.ParseNxm(uri, Folder); string id = request.Id;
            if (transfers.ContainsKey(id)) return;
            Directory.CreateDirectory(Folder);
            var cancellation = new CancellationTokenSource(); transfers[id] = cancellation; pending.Remove(id);
            try {
                object result = await downloader.DownloadNxmAsync(uri, Folder, cancellation.Token);
                var final = Data.Map(result);
                Store.Downloads.RemoveAll(x => Data.Str(x, "id") == id); Store.Downloads.Insert(0, final);
                var fileHistory = Data.Map(Data.Get(Store.State, "fileDownloads")); fileHistory[id] = new Dictionary<string, object>(final); Store.State["fileDownloads"] = fileHistory;
                var mod = FindMod(request.Domain, request.ModId);
                if (mod == null) { try { await RefreshOne(request.Domain, request.ModId); mod = FindMod(request.Domain, request.ModId); } catch { } }
                if (mod != null) { mod["lastDownloadedAt"] = Data.Now(); mod["needsReview"] = Data.Num(mod, "updatedAt") > Data.Now(); }
                details.Remove(request.Domain + ":" + request.ModId); Store.Save(); Emit();
            } catch (OperationCanceledException) { Store.Save(); Emit(); }
            catch (Exception ex) { Store.State["notice"] = ex.Message; Store.Save(); Emit(); }
            finally { transfers.Remove(id); cancellation.Dispose(); window.NextAuthorization(); }
        }
        internal void AuthorizationStatus(string id, string message, bool needsUser) {
            var row = Store.Downloads.Select(Data.Map).FirstOrDefault(x => Data.Str(x, "id") == id);
            if (row == null || Data.Str(row, "status") != "waiting-authorization") return;
            if (Data.Str(row, "authorizationMessage") == message && Data.Bool(row, "needsUser") == needsUser) return;
            row["authorizationMessage"] = message; row["needsUser"] = needsUser; Emit();
        }
        private void DownloadProgress(object value) {
            window.OnUi(delegate {
                var update = Data.Map(value); string id = Data.Str(update, "id");
                var row = Store.Downloads.Select(Data.Map).FirstOrDefault(x => Data.Str(x, "id") == id);
                if (row == null) { row = new Dictionary<string, object>(); Store.Downloads.Insert(0, row); }
                foreach (var pair in update) row[pair.Key] = pair.Value;
                row["bytes"] = Data.Num(update, "downloadedBytes"); row["total"] = Data.Num(update, "totalBytes");
                window.Emit("download", row);
                if (Data.Str(row, "status") != "downloading") { Store.Save(); Emit(); }
            });
        }
        private void RestoreDownload(object value) {
            string domain = Data.Str(value, "gameDomain").ToLowerInvariant(); long modId = Data.Num(value, "modId"), fileId = Data.Num(value, "fileId");
            if (!Regex.IsMatch(domain, "^[a-zA-Z0-9_-]{1,100}$") || modId <= 0 || fileId <= 0) return;
            string id = domain + ":" + modId + ":" + fileId;
            string status = Data.Str(value, "status");
            if (!new[] { "completed", "failed", "cancelled", "interrupted" }.Contains(status)) status = "interrupted";
            var row = Data.Obj("id", id, "gameDomain", domain, "modId", modId, "fileId", fileId, "name", Data.Str(value, "name"), "filename", Data.Str(value, "filename"), "status", status, "downloadedBytes", Data.Num(value, "downloadedBytes"), "totalBytes", Data.Num(value, "totalBytes"), "path", Data.Str(value, "path"), "version", Data.Str(value, "version"), "downloadedAt", Data.Num(value, "downloadedAt"), "error", Data.Str(value, "error"));
            if (!Store.Downloads.Any(x => Data.Str(x, "id") == id)) Store.Downloads.Add(row);
            if (status == "completed") { var fileHistory = Data.Map(Data.Get(Store.State, "fileDownloads")); if (Data.Num(Data.Get(fileHistory, id), "downloadedAt") <= Data.Num(row, "downloadedAt")) fileHistory[id] = row; Store.State["fileDownloads"] = fileHistory; }
        }
        private void RegisterNxm() {
            string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
            using (var key = Registry.CurrentUser.CreateSubKey("Software\\Classes\\nxm")) { key.SetValue("", "URL:Nexus Mods download"); key.SetValue("URL Protocol", ""); using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + executable + "\",0"); using (var command = key.CreateSubKey("shell\\open\\command")) command.SetValue("", "\"" + executable + "\" \"%1\""); }
        }
        internal object ExportData() { return Data.Obj("schemaVersion", 1, "games", Store.Games, "mods", Store.Mods, "downloads", Store.Downloads, "fileDownloads", Data.Get(Store.State, "fileDownloads"), "excludedGames", Data.Get(Store.State, "excludedGames")); }
        private void Emit() { window.Emit("state", Store.PublicState()); }
        public void Dispose() {
            if (disposed) return;
            disposed = true; lifetime.Cancel();
            foreach (var token in transfers.Values) token.Cancel();
            if (downloader != null) downloader.Dispose(); if (Api != null) Api.Dispose();
            Store.State.Remove("refreshProgress"); Store.Save();
        }
    }
}
