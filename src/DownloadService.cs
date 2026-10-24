using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NexusM
{
    // One active stream keeps free-account downloads predictable and the disk footprint small.
    public sealed class DownloadService : IDisposable
    {
        private readonly NexusApi api;
        private readonly HttpClient client;
        private readonly Action<object> progress;
        private readonly SemaphoreSlim queue = new SemaphoreSlim(1);

        public DownloadService(NexusApi nexusApi, Action<object> onProgress)
        {
            api = nexusApi;
            progress = onProgress;
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
            client.Timeout = Timeout.InfiniteTimeSpan;
            // Credentials belong only on api.nexusmods.com and are never sent to a CDN.
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NexusM/0.1.0");
        }

        public Task<object> DownloadNxmAsync(string uri, string folder, CancellationToken token)
        {
            return DownloadAsync(DownloadRequest.ParseNxm(uri, folder), token);
        }

        public async Task<object> DownloadAsync(DownloadRequest request, CancellationToken token)
        {
            request.Domain = JsonTools.Domain(request.Domain);
            if (request.ModId <= 0 || request.FileId <= 0) throw new ArgumentException("Invalid Nexus mod or file ID.");
            if (String.IsNullOrWhiteSpace(request.Folder)) throw new ArgumentException("Choose a download folder in Settings first.");
            string folder = Path.GetFullPath(request.Folder);
            var state = new Dictionary<string, object> {
                { "id", request.Id }, { "gameDomain", request.Domain }, { "modId", request.ModId }, { "fileId", request.FileId },
                { "name", "File " + request.FileId }, { "status", "queued" }, { "downloadedBytes", 0L }, { "totalBytes", 0L },
                { "speed", 0L }, { "error", "" }, { "path", "" }, { "version", "" } };
            Emit(state);
            bool acquired = false;
            try
            {
                await queue.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                state["status"] = "authorizing";
                Emit(state);
                var account = JsonTools.Dict(await api.ValidateAsync(token).ConfigureAwait(false));
                long accountId = JsonTools.Long(account, "user_id");
                if (request.UserId > 0 && request.UserId != accountId)
                    throw new ArgumentException("This download link belongs to a different Nexus account. Generate a link using the account in Settings.");
                bool premium = Object.Equals(JsonTools.Get(account, "is_premium"), true);
                if (!premium && (String.IsNullOrEmpty(request.Key) || request.Expires <= 0 || request.UserId <= 0))
                    throw new ArgumentException("Use Download with Manager on Nexus to authorize this free-account download.");
                if (!String.IsNullOrEmpty(request.Key) && request.Expires <= JsonTools.UnixNow())
                    throw new ArgumentException("This Nexus download link has expired. Use Download with Manager again to get a fresh link.");

                var file = JsonTools.Dict(await api.FileAsync(request.Domain, request.ModId, request.FileId, token).ConfigureAwait(false));
                string filename = SafeFilename(JsonTools.Text(file, "file_name"));
                if (String.IsNullOrEmpty(filename)) filename = request.Domain + "-" + request.ModId + "-" + request.FileId + ".zip";
                state["name"] = JsonTools.Text(file, "name");
                if (String.IsNullOrEmpty((string)state["name"])) state["name"] = filename;
                state["filename"] = filename;
                state["version"] = JsonTools.Text(file, "version");
                var links = JsonTools.Array(await api.DownloadLinksAsync(request, token).ConfigureAwait(false));
                var mirrors = new List<Uri>();
                foreach (object item in links)
                {
                    Uri mirror;
                    if (Uri.TryCreate(JsonTools.Text(JsonTools.Dict(item), "URI"), UriKind.Absolute, out mirror) && IsNexusDownloadUri(mirror)) mirrors.Add(mirror);
                }
                if (mirrors.Count == 0) throw new IOException("Nexus returned no supported download servers. Open a fresh download link and try again.");
                Directory.CreateDirectory(folder);
                string partial = Path.Combine(folder, ".nexusm-" + request.Domain + "-" + request.ModId + "-" + request.FileId + ".part");
                Exception last = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        await TransferAsync(mirrors[attempt % mirrors.Count], partial, state, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        string finalPath = AvailablePath(folder, filename, request.FileId);
                        File.Move(partial, finalPath);
                        TryDelete(partial + ".json");
                        state["path"] = finalPath;
                        state["status"] = "completed";
                        state["downloadedAt"] = JsonTools.UnixNow();
                        state["speed"] = 0L;
                        // Keep the exact archive identity so cleanup can leave a
                        // different file subsequently placed at this path alone.
                        try {
                            var saved = new FileInfo(finalPath);
                            state["fileLength"] = saved.Length;
                            state["fileCreatedUtcTicks"] = saved.CreationTimeUtc.Ticks;
                            state["fileModifiedUtcTicks"] = saved.LastWriteTimeUtc.Ticks;
                        } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        Emit(state);
                        return state;
                    }
                    catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); last = new IOException("The download server timed out."); }
                    catch (HttpRequestException) { last = new IOException("The connection to the download server was interrupted."); }
                    catch (IOException ex) { last = ex; }
                    if (attempt < 2)
                    {
                        state["status"] = "retrying";
                        state["error"] = "Connection interrupted. Resuming download…";
                        Emit(state);
                        await Task.Delay(1200 * (attempt + 1), token).ConfigureAwait(false);
                    }
                }
                throw new IOException(last == null ? "Download failed. Open a new Nexus link and try again." : last.Message);
            }
            catch (OperationCanceledException)
            {
                state["status"] = "cancelled";
                state["error"] = "Download cancelled. The partial file is kept so a fresh Nexus link can resume it.";
                state["speed"] = 0L;
                Emit(state);
                throw;
            }
            catch (Exception ex)
            {
                state["status"] = "failed";
                state["error"] = ex is NexusApiException || ex is ArgumentException || ex is IOException ? ex.Message : "Download failed. Check the folder and try a fresh Nexus download link.";
                state["speed"] = 0L;
                Emit(state);
                throw new IOException((string)state["error"]);
            }
            finally { if (acquired) queue.Release(); }
        }

        private async Task TransferAsync(Uri source, string partial, Dictionary<string, object> state, CancellationToken token)
        {
            string metadataPath = partial + ".json";
            var old = ReadMetadata(metadataPath);
            long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            string etag = JsonTools.Text(old, "etag");
            string lastModified = JsonTools.Text(old, "lastModified");
            // Never append to an unidentified or externally changed partial file.
            if (offset > 0 && (String.IsNullOrEmpty(etag) && String.IsNullOrEmpty(lastModified) ||
                JsonTools.Text(old, "id") != JsonTools.Text(state, "id") ||
                offset > JsonTools.Long(old, "total"))) offset = 0;
            using (var response = await OpenStreamAsync(source, offset, etag, lastModified, token).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Retry from byte zero; a 416 response is not proof of a complete download.
                    using (var truncate = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None)) { }
                    TryDelete(metadataPath);
                    throw new IOException("The download server could not resume this file. Restarting it.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    int code = (int)response.StatusCode;
                    throw new IOException(code == 401 || code == 403 ? "The download authorization expired. Generate a fresh link using Download with Manager." : "The download server returned HTTP " + code + ". Try again with a fresh Nexus link.");
                }
                string mediaType = response.Content.Headers.ContentType == null ? "" : response.Content.Headers.ContentType.MediaType;
                if (String.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) || String.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The download server returned a web page instead of a file. Generate a fresh Nexus download link.");
                long total;
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    ContentRangeHeaderValue range = response.Content.Headers.ContentRange;
                    if (range == null || range.From != offset || !range.Length.HasValue || !range.To.HasValue || range.To.Value != range.Length.Value - 1)
                        throw new IOException("The download server returned an invalid resume range.");
                    total = range.Length.Value;
                    long previousTotal = JsonTools.Long(old, "total");
                    if (offset > 0 && previousTotal != total) throw new IOException("The download changed while resuming. Cancel and start again.");
                }
                else
                {
                    offset = 0;
                    total = response.Content.Headers.ContentLength ?? 0;
                }
                var fresh = new Dictionary<string, object> {
                    { "id", state["id"] }, { "etag", response.Headers.ETag == null || response.Headers.ETag.IsWeak ? "" : response.Headers.ETag.ToString() },
                    { "lastModified", response.Content.Headers.LastModified.HasValue ? response.Content.Headers.LastModified.Value.ToString("R", CultureInfo.InvariantCulture) : "" },
                    { "total", total } };
                // This small sidecar contains identifiers/validators only, never signed URLs or keys.
                File.WriteAllText(metadataPath, JsonTools.Serializer().Serialize(fresh));
                state["status"] = "downloading";
                state["error"] = "";
                state["totalBytes"] = total;
                state["downloadedBytes"] = offset;
                Emit(state);
                var timer = Stopwatch.StartNew();
                long bytes = offset;
                long sinceStart = 0;
                long lastEvent = 0;
                using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    byte[] buffer = new byte[81920];
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(90));
                        int read = await input.ReadAsync(buffer, 0, buffer.Length, idle.Token).ConfigureAwait(false);
                        idle.CancelAfter(Timeout.Infinite);
                        if (read == 0) break;
                        await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                        bytes += read;
                        sinceStart += read;
                        if (total > 0 && bytes > total) throw new IOException("The downloaded file exceeded its expected size.");
                        if (timer.ElapsedMilliseconds - lastEvent >= 250)
                        {
                            state["downloadedBytes"] = bytes;
                            state["speed"] = (long)(sinceStart / Math.Max(timer.Elapsed.TotalSeconds, .1));
                            Emit(state);
                            lastEvent = timer.ElapsedMilliseconds;
                        }
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                    if (total > 0 && bytes != total) throw new IOException("The connection ended before the entire file was received.");
                    if (bytes == 0) throw new IOException("The download server returned an empty file.");
                    state["downloadedBytes"] = bytes;
                    state["totalBytes"] = bytes;
                }
            }
        }

        private async Task<HttpResponseMessage> OpenStreamAsync(Uri source, long offset, string etag, string modified, CancellationToken token)
        {
            Uri current = source;
            for (int hop = 0; hop < 5; hop++)
            {
                if (!IsNexusDownloadUri(current)) throw new IOException("Nexus redirected to an unsupported download address.");
                using (var request = new HttpRequestMessage(HttpMethod.Get, current))
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    if (offset > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(offset, null);
                        if (!String.IsNullOrEmpty(etag)) request.Headers.TryAddWithoutValidation("If-Range", etag);
                        else if (!String.IsNullOrEmpty(modified)) request.Headers.TryAddWithoutValidation("If-Range", modified);
                    }
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status < 400)
                    {
                        Uri next = response.Headers.Location;
                        response.Dispose();
                        if (next == null) throw new IOException("The download server returned an invalid redirect.");
                        current = next.IsAbsoluteUri ? next : new Uri(current, next);
                        continue;
                    }
                    return response;
                }
            }
            throw new IOException("The download server returned too many redirects.");
        }

        public static bool IsNexusDownloadUri(Uri uri)
        {
            if (uri == null || uri.Scheme != "https" || !String.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return false;
            string host = uri.DnsSafeHost.ToLowerInvariant();
            return host == "nexusmods.com" || host.EndsWith(".nexusmods.com", StringComparison.Ordinal) ||
                host == "nexus-cdn.com" || host.EndsWith(".nexus-cdn.com", StringComparison.Ordinal);
        }

        private static Dictionary<string, object> ReadMetadata(string path)
        {
            try
            {
                if (new FileInfo(path).Length > 16384) return new Dictionary<string, object>();
                return JsonTools.Dict(JsonTools.Serializer().DeserializeObject(File.ReadAllText(path)));
            }
            catch { return new Dictionary<string, object>(); }
        }

        private static string SafeFilename(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return "";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.', ' ');
            if (name.Length > 180)
            {
                string extension = Path.GetExtension(name);
                if (extension.Length > 20) extension = extension.Substring(0, 20);
                name = name.Substring(0, 150) + extension;
            }
            if (Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\\.)", RegexOptions.IgnoreCase)) name = "_" + name;
            return name;
        }

        private static string AvailablePath(string folder, string filename, long fileId)
        {
            string result = Path.Combine(folder, filename);
            int number = 0;
            while (File.Exists(result) || Directory.Exists(result))
            {
                number++;
                result = Path.Combine(folder, Path.GetFileNameWithoutExtension(filename) + " (" + fileId + (number == 1 ? "" : "-" + number) + ")" + Path.GetExtension(filename));
            }
            return result;
        }

        private void Emit(Dictionary<string, object> state)
        {
            if (progress != null) progress(new Dictionary<string, object>(state));
        }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        public void Dispose() { client.Dispose(); }
    }
}
