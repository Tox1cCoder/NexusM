using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NexusM
{
    public sealed class NexusApi : IDisposable
    {
        public const int ModBatchSize = 80;
        private static readonly string AppVersion = typeof(NexusApi).Assembly.GetName().Version.ToString(3);
        private readonly HttpClient client;
        private readonly SemaphoreSlim concurrency = new SemaphoreSlim(4);
        private readonly string apiKey;
        private object validated;
        private readonly Dictionary<string, long> gameIds = new Dictionary<string, long>();
        private readonly object limitLock = new object();
        private Dictionary<string, object> limits = new Dictionary<string, object>();
        public object RateLimits { get { lock (limitLock) return new Dictionary<string, object>(limits); } }

        public NexusApi(string key)
        {
            if (String.IsNullOrWhiteSpace(key) || key.Contains("\r") || key.Contains("\n"))
                throw new ArgumentException("Set your Nexus personal API key in Settings first.");
            apiKey = key.Trim();
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = false, MaxConnectionsPerServer = 4 };
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        }

        public async Task<object> ValidateAsync(CancellationToken token = default(CancellationToken))
        {
            if (validated != null) return validated;
            var result = JsonTools.Dict(await GetAsync("users/validate.json", token).ConfigureAwait(false));
            // Nexus echoes the credential in this response. Never expose it to the UI or local JSON.
            result.Remove("key");
            result.Remove("email");
            validated = result;
            return result;
        }

        public Task<object> GamesAsync(CancellationToken token = default(CancellationToken)) { return GetAsync("games.json?include_unapproved=false", token); }
        public Task<object> GameAsync(string domain, CancellationToken token = default(CancellationToken)) { return GetAsync("games/" + JsonTools.Domain(domain) + ".json", token); }
        public Task<object> TrackedModsAsync(CancellationToken token = default(CancellationToken)) { return GetAsync("user/tracked_mods.json", token); }
        public async Task<object> ModAsync(string domain, long modId, CancellationToken token = default(CancellationToken))
        {
            object result = await GetAsync(ModPath(domain, modId) + ".json", token).ConfigureAwait(false);
            long gameId = JsonTools.Long(JsonTools.Dict(result), "game_id");
            if (gameId > 0) lock (gameIds) gameIds[JsonTools.Domain(domain)] = gameId;
            return result;
        }
        public Task<object> FilesAsync(string domain, long modId, CancellationToken token = default(CancellationToken)) { return GetAsync(ModPath(domain, modId) + "/files.json", token); }
        public Task<object> ChangelogsAsync(string domain, long modId, CancellationToken token = default(CancellationToken)) { return GetAsync(ModPath(domain, modId) + "/changelogs.json", token); }
        public Task<object> FileAsync(string domain, long modId, long fileId, CancellationToken token = default(CancellationToken))
        {
            CheckId(fileId);
            return GetAsync(ModPath(domain, modId) + "/files/" + fileId.ToString(CultureInfo.InvariantCulture) + ".json", token);
        }

        // The preview is a small public archive manifest, not an archive download.
        // Its request deliberately carries no API key, including on failure/redirects.
        public async Task<object> FileContentsAsync(string domain, long modId, long fileId, CancellationToken token = default(CancellationToken))
        {
            var file = JsonTools.Dict(await FileAsync(domain, modId, fileId, token).ConfigureAwait(false));
            if (JsonTools.Long(file, "file_id") != fileId)
                throw new NexusApiException("Nexus returned information for a different file. Please reopen the mod and try again.", 502);
            string link = JsonTools.Text(file, "content_preview_link");
            if (String.IsNullOrWhiteSpace(link)) return FilePreviewResult(false, new List<object>(), false, "Nexus has no content preview for this file.");
            Uri uri;
            if (!Uri.TryCreate(link, UriKind.Absolute, out uri) || uri.Scheme != "https" ||
                uri.Port != 443 || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Fragment) ||
                !((uri.Host == "file-metadata.nexusmods.com" && uri.AbsolutePath.StartsWith("/file/nexus-files-s3-meta/", StringComparison.Ordinal)) ||
                  (uri.Host == "mod-file-manifests.nexusmods.com" && uri.AbsolutePath.Length > 1)))
                throw new NexusApiException("Nexus provided an unsupported content preview address. Use Open on Nexus to view this file.", 502);
            return await FetchFilePreviewAsync(uri, token).ConfigureAwait(false);
        }

        private async Task<object> FetchFilePreviewAsync(Uri uri, CancellationToken token, int timeoutMilliseconds = 45000)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                // ResponseHeadersRead leaves the body outside HttpClient.Timeout.
                // Bound the entire metadata read so a stalled stream releases its slot.
                timeout.CancelAfter(timeoutMilliseconds);
                try { return await FetchFilePreviewCoreAsync(uri, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    throw new NexusApiException("Nexus's file preview did not respond in time. Please try again.", 408);
                }
            }
        }

        private async Task<object> FetchFilePreviewCoreAsync(Uri uri, CancellationToken token)
        {
            await concurrency.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "NexusM/" + AppVersion + " (archive content preview)");
                    HttpResponseMessage response;
                    try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
                    catch (TaskCanceledException) { token.ThrowIfCancellationRequested(); throw new NexusApiException("Nexus's file preview did not respond in time. Please try again.", 408); }
                    catch (HttpRequestException) { throw new NexusApiException("Could not reach Nexus's file preview. Check your connection and try again.", 0); }
                    using (response)
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                            return FilePreviewResult(false, new List<object>(), false, "Nexus has no content preview for this file yet.");
                        if (!response.IsSuccessStatusCode)
                            throw new NexusApiException("Nexus could not load this file preview. Please try again or use Open on Nexus.", (int)response.StatusCode);
                        // Current manifests include hashes for every file, so even a
                        // modest archive listing can exceed the legacy 4 MB limit.
                        const int maxBytes = 16 * 1024 * 1024;
                        if (response.Content.Headers.ContentLength > maxBytes)
                            throw new NexusApiException("This file preview is too large to display. Use Open on Nexus to view it.", 413);
                        string text;
                        try
                        {
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var buffer = new MemoryStream())
                            {
                                var bytes = new byte[8192]; int count;
                                while ((count = await stream.ReadAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false)) > 0)
                                {
                                    if (buffer.Length + count > maxBytes)
                                        throw new NexusApiException("This file preview is too large to display. Use Open on Nexus to view it.", 413);
                                    buffer.Write(bytes, 0, count);
                                }
                                text = Encoding.UTF8.GetString(buffer.ToArray());
                            }
                        }
                        catch (IOException) { throw new NexusApiException("Nexus's file preview could not be read. Please try again.", 502); }
                        object raw;
                        try { raw = JsonTools.Serializer().DeserializeObject(text); }
                        catch (ArgumentException) { throw new NexusApiException("Nexus returned an unreadable file preview. Please try again.", 502); }
                        catch (InvalidOperationException) { throw new NexusApiException("Nexus returned an unreadable file preview. Please try again.", 502); }
                        catch (MissingMethodException) { throw new NexusApiException("Nexus returned an unreadable file preview. Please try again.", 502); }
                        var root = JsonTools.Dict(raw);
                        var children = JsonTools.Get(root, "children");
                        var entries = new List<object>(); bool truncated = false;
                        if (children is object[] || children is System.Collections.IList)
                            CollectPreviewEntries(JsonTools.Array(children), "", 0, entries, ref truncated, token);
                        else {
                            var files = JsonTools.Get(root, "files");
                            if (JsonTools.Long(root, "version") != 1 || (!(files is object[]) && !(files is System.Collections.IList)))
                                throw new NexusApiException("Nexus returned an unreadable file preview. Please try again.", 502);
                            CollectManifestEntries(JsonTools.Array(files), entries, ref truncated, token);
                        }
                        return FilePreviewResult(true, entries, truncated, truncated ? "Some archive entries were omitted because the preview is too large." : "");
                    }
                }
            }
            finally { concurrency.Release(); }
        }

        private static object FilePreviewResult(bool available, List<object> entries, bool truncated, string notice)
        {
            return new Dictionary<string, object> { { "available", available }, { "entries", entries }, { "truncated", truncated }, { "notice", notice } };
        }

        private static void CollectManifestEntries(object[] files, List<object> entries, ref bool truncated, CancellationToken token)
        {
            foreach (var value in files)
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= 10000) { truncated = true; return; }
                var file = JsonTools.Dict(value);
                string path = JsonTools.Get(file, "file_path") as string;
                long size;
                if (String.IsNullOrWhiteSpace(path) || !Int64.TryParse(JsonTools.Text(file, "file_size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 0)
                    throw new NexusApiException("Nexus returned an unreadable file preview. Please try again.", 502);
                if (path.Length > 4096) { truncated = true; continue; }
                entries.Add(new Dictionary<string, object> { { "path", path.Replace('\\', '/') }, { "isDirectory", false }, { "size", size }, { "sizeLabel", "" } });
            }
        }

        private static void CollectPreviewEntries(object[] nodes, string parent, int depth, List<object> entries, ref bool truncated, CancellationToken token)
        {
            if (depth > 64) { truncated = true; return; }
            foreach (var value in nodes)
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= 10000) { truncated = true; return; }
                var node = JsonTools.Dict(value);
                string path = JsonTools.Text(node, "path").Replace('\\', '/');
                if (path.Length == 0) path = (parent.Length == 0 ? "" : parent + "/") + JsonTools.Text(node, "name");
                bool directory = JsonTools.Text(node, "type") == "directory";
                var children = JsonTools.Get(node, "children");
                if (path.Length > 4096) { truncated = true; continue; }
                if (path.Length > 0 && (directory || JsonTools.Text(node, "type") == "file"))
                {
                    string sizeLabel = JsonTools.Text(node, "size");
                    entries.Add(new Dictionary<string, object> { { "path", path }, { "isDirectory", directory }, { "size", PreviewSize(sizeLabel) }, { "sizeLabel", sizeLabel } });
                }
                if (children != null) CollectPreviewEntries(JsonTools.Array(children), path, depth + 1, entries, ref truncated, token);
            }
        }

        private static long PreviewSize(string value)
        {
            var parts = System.Text.RegularExpressions.Regex.Match(value ?? "", @"^\s*(\d+(?:\.\d+)?)\s*([kKmMgGtT]?[bB])?\s*$");
            double size;
            if (!parts.Success || !Double.TryParse(parts.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out size)) return 0;
            string unit = parts.Groups[2].Value.ToLowerInvariant();
            int exponent = unit == "kb" ? 1 : unit == "mb" ? 2 : unit == "gb" ? 3 : unit == "tb" ? 4 : 0;
            size *= Math.Pow(1000, exponent);
            return size > 0 && size <= Int64.MaxValue ? (long)size : 0;
        }

        // List refreshes need only these small fields. Fetching them together also includes
        // account download dates, instead of issuing a v1 request for every library entry.
        // Nexus may omit deleted or inaccessible mods and does not preserve input order.
        public async Task<object> ModsByUidAsync(IEnumerable<long> uids, CancellationToken token = default(CancellationToken))
        {
            if (uids == null) throw new ArgumentNullException("uids");
            token.ThrowIfCancellationRequested();
            var requested = new HashSet<long>();
            var ids = new List<string>();
            foreach (long uid in uids)
            {
                token.ThrowIfCancellationRequested();
                if (uid <= 0 || !requested.Add(uid)) continue;
                if (ids.Count == 100) throw new ArgumentException("Check at most 100 unique mod IDs per request.", "uids");
                ids.Add(uid.ToString(CultureInfo.InvariantCulture));
            }
            if (ids.Count == 0) return new object[0];
            const string query = "query NexusMLibraryStatus($uids: [ID!]!, $offset: Int!, $count: Int!) { modsByUid(uids: $uids, offset: $offset, count: $count) { totalCount nodes { uid modId gameId name summary version author pictureUrl status updatedAt viewerDownloaded viewerUpdateAvailable } } }";
            var results = new List<object>();
            var returned = new HashSet<long>();
            int offset = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var data = JsonTools.Dict(await SendGraphqlAsync(query, new { uids = ids.ToArray(), offset = offset, count = ModBatchSize }, token).ConfigureAwait(false));
                var collection = JsonTools.Dict(JsonTools.Get(data, "modsByUid"));
                object rawNodes = JsonTools.Get(collection, "nodes");
                long total = JsonTools.Long(collection, "totalCount");
                if ((!(rawNodes is object[]) && !(rawNodes is System.Collections.IList)) ||
                    JsonTools.Get(collection, "totalCount") == null || total < 0 || total > requested.Count)
                    throw new NexusApiException("Nexus returned an incomplete mod list. Your previous information was kept.", 502);
                var nodes = JsonTools.Array(rawNodes);
                if (nodes.Length == 0 && offset < total)
                    throw new NexusApiException("Nexus returned an incomplete mod list. Your previous information was kept.", 502);
                foreach (object item in nodes)
                {
                    token.ThrowIfCancellationRequested();
                    var node = JsonTools.Dict(item);
                    long uid = JsonTools.Long(node, "uid"), gameId = JsonTools.Long(node, "gameId"), modId = JsonTools.Long(node, "modId");
                    DateTimeOffset updated;
                    // Ignore malformed and unrelated rows; the caller retains its cached row
                    // when a requested UID is missing. IDs also guard against mismatched data.
                    if (uid <= 0 || !requested.Contains(uid) || returned.Contains(uid) || gameId <= 0 ||
                        gameId > Int32.MaxValue || modId <= 0 || modId > UInt32.MaxValue ||
                        ((gameId << 32) | modId) != uid || !(JsonTools.Get(node, "name") is string) ||
                        !(JsonTools.Get(node, "version") is string) || !(JsonTools.Get(node, "summary") is string) ||
                        String.IsNullOrWhiteSpace(JsonTools.Text(node, "status")) ||
                        !DateTimeOffset.TryParse(JsonTools.Text(node, "updatedAt"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out updated)) continue;
                    returned.Add(uid);
                    results.Add(node);
                }
                // Nexus currently caps a page at 80 even when count:100 is requested.
                // Follow the reported total so larger batches never silently lose rows.
                offset += nodes.Length;
                if (offset >= total) break;
            }
            return results.ToArray();
        }

        // Enumerating account history is not public API functionality, but known mod UIDs
        // can be checked in batches. A null date means Nexus has no download for this viewer.
        public async Task<object> BatchViewerDownloadsAsync(IEnumerable<long> uids, CancellationToken token = default(CancellationToken))
        {
            if (uids == null) throw new ArgumentNullException("uids");
            var distinct = new HashSet<long>();
            var batch = new List<string>();
            var results = new List<object>();
            foreach (long uid in uids)
            {
                token.ThrowIfCancellationRequested();
                if (uid <= 0 || !distinct.Add(uid)) continue;
                batch.Add(uid.ToString(CultureInfo.InvariantCulture));
                if (batch.Count == ModBatchSize)
                {
                    results.AddRange(await ViewerBatchAsync(batch.ToArray(), token).ConfigureAwait(false));
                    batch.Clear();
                }
            }
            if (batch.Count > 0) results.AddRange(await ViewerBatchAsync(batch.ToArray(), token).ConfigureAwait(false));
            return results.ToArray();
        }

        private async Task<object[]> ViewerBatchAsync(string[] uids, CancellationToken token)
        {
            const string query = "query NexusMDownloadStatus($uids: [ID!]!, $count: Int!) { modsByUid(uids: $uids, count: $count) { nodes { uid viewerDownloaded viewerUpdateAvailable } } }";
            var data = JsonTools.Dict(await SendGraphqlAsync(query, new { uids = uids, count = ModBatchSize }, token).ConfigureAwait(false));
            var nodes = JsonTools.Array(JsonTools.Get(JsonTools.Dict(JsonTools.Get(data, "modsByUid")), "nodes"));
            var results = new List<object>();
            foreach (object item in nodes)
            {
                var node = JsonTools.Dict(item);
                long uid = JsonTools.Long(node, "uid");
                if (uid <= 0) continue;
                results.Add(new Dictionary<string, object> { { "uid", uid },
                    { "viewerDownloaded", JsonTools.Get(node, "viewerDownloaded") },
                    { "viewerUpdateAvailable", JsonTools.Get(node, "viewerUpdateAvailable") } });
            }
            return results.ToArray();
        }

        public Task<object> DownloadLinksAsync(DownloadRequest request, CancellationToken token)
        {
            CheckId(request.FileId);
            string path = ModPath(request.Domain, request.ModId) + "/files/" + request.FileId + "/download_link.json";
            if (!String.IsNullOrEmpty(request.Key))
                path += "?key=" + Uri.EscapeDataString(request.Key) + "&expires=" + request.Expires.ToString(CultureInfo.InvariantCulture);
            return GetAsync(path, token);
        }

        public async Task<object> RequirementsAsync(string domain, long modId, CancellationToken token = default(CancellationToken))
        {
            CheckId(modId);
            domain = JsonTools.Domain(domain);
            long gameId;
            lock (gameIds) gameIds.TryGetValue(domain, out gameId);
            if (gameId <= 0)
            {
                var game = JsonTools.Dict(await GameAsync(domain, token).ConfigureAwait(false));
                gameId = JsonTools.Long(game, "id");
                if (gameId > 0) lock (gameIds) gameIds[domain] = gameId;
            }
            string query = "query NexusMRequirements($modId: ID!, $gameId: ID!) { mod(modId: $modId, gameId: $gameId) { modRequirements { nexusRequirements { nodes { modName modId gameId notes url externalRequirement } } dlcRequirements { notes gameExpansion { name } } } } }";
            var data = JsonTools.Dict(await SendGraphqlAsync(query, new { modId = modId.ToString(CultureInfo.InvariantCulture), gameId = gameId.ToString(CultureInfo.InvariantCulture) }, token).ConfigureAwait(false));
            return JsonTools.Get(JsonTools.Dict(JsonTools.Get(data, "mod")), "modRequirements");
        }

        public async Task<object> SendGraphqlAsync(string query, object variables, CancellationToken token = default(CancellationToken))
        {
            var payload = new { query = query, variables = variables };
            var raw = JsonTools.Dict(await RequestAsync(new Uri("https://api.nexusmods.com/v2/graphql"), payload, token).ConfigureAwait(false));
            var errors = JsonTools.Array(JsonTools.Get(raw, "errors"));
            if (errors.Length > 0)
                throw new NexusApiException("Nexus could not provide this optional account or requirements information. Use Open on Nexus to view it.", 400);
            return JsonTools.Get(raw, "data");
        }

        private Task<object> GetAsync(string path, CancellationToken token)
        {
            return RequestAsync(new Uri("https://api.nexusmods.com/v1/" + path), null, token);
        }

        private async Task<object> RequestAsync(Uri uri, object body, CancellationToken token)
        {
            await concurrency.WaitAsync(token).ConfigureAwait(false);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, uri))
                    {
                        request.Headers.TryAddWithoutValidation("apikey", apiKey);
                        request.Headers.TryAddWithoutValidation("Application-Name", "NexusM");
                        request.Headers.TryAddWithoutValidation("Application-Version", AppVersion);
                        request.Headers.TryAddWithoutValidation("User-Agent", "NexusM/" + AppVersion + " (personal desktop client)");
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");
                        if (body != null) request.Content = new StringContent(JsonTools.Serializer().Serialize(body), Encoding.UTF8, "application/json");
                        HttpResponseMessage response;
                        try { response = await client.SendAsync(request, token).ConfigureAwait(false); }
                        catch (TaskCanceledException)
                        {
                            token.ThrowIfCancellationRequested();
                            throw new NexusApiException("Nexus did not respond in time. Please try again.", 408);
                        }
                        catch (HttpRequestException)
                        {
                            throw new NexusApiException("Could not reach Nexus Mods. Check your connection and try again.", 0);
                        }
                        using (response)
                        {
                            UpdateLimits(response);
                            int code = (int)response.StatusCode;
                            if ((code == 502 || code == 503 || code == 504) && attempt < 2)
                            { await Task.Delay(1000 * (attempt + 1), token).ConfigureAwait(false); continue; }
                            if (!response.IsSuccessStatusCode) throw new NexusApiException(ErrorMessage(code), code);
                            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (text.Length > 24 * 1024 * 1024) throw new NexusApiException("Nexus returned too much data for this request.", 502);
                            try { return JsonTools.Serializer().DeserializeObject(text); }
                            catch (ArgumentException) { throw new NexusApiException("Nexus returned an unreadable response. Please try again.", 502); }
                        }
                    }
                }
            }
            finally { concurrency.Release(); }
        }

        private void UpdateLimits(HttpResponseMessage response)
        {
            string[] names = { "x-rl-hourly-limit", "x-rl-hourly-remaining", "x-rl-daily-limit", "x-rl-daily-remaining", "x-rl-hourly-reset", "x-rl-daily-reset" };
            lock (limitLock)
                foreach (string name in names)
                {
                    IEnumerable<string> values;
                    if (response.Headers.TryGetValues(name, out values)) limits[name] = String.Join(",", values);
                }
        }

        private static string ErrorMessage(int code)
        {
            if (code == 401) return "Nexus rejected your API key. Update it in Settings.";
            if (code == 403) return "Nexus denied this request. Free downloads must be authorized using Download with Manager on the website; the link can expire.";
            if (code == 404) return "This game, mod, or file is no longer available on Nexus Mods.";
            if (code == 429) return "Nexus API rate limit reached. Wait before checking again.";
            if (code == 410) return "This Nexus file or download link has expired or was removed.";
            if (code == 422) return "Nexus could not accept this request. Open a fresh download link on the website.";
            return "Nexus returned HTTP " + code + ". Please try again later.";
        }

        private static string ModPath(string domain, long modId)
        {
            CheckId(modId);
            return "games/" + JsonTools.Domain(domain) + "/mods/" + modId.ToString(CultureInfo.InvariantCulture);
        }
        private static void CheckId(long id) { if (id <= 0) throw new ArgumentException("Nexus IDs must be positive numbers."); }
        public void Dispose() { client.Dispose(); }
    }
}
