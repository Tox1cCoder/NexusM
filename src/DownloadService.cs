using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NexusM
{
    public sealed class DownloadService : IDisposable
    {
        private readonly NexusApi api;
        private readonly HttpClient client;
        private readonly Action<object> progress;
        private readonly DownloadOptions options;
        private readonly SemaphoreSlim queue;
        private readonly object finalizationLock = new object();
        private readonly object requestLock = new object();
        private readonly HashSet<string> requests = new HashSet<string>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly int timeoutMilliseconds;
        private readonly Func<int, CancellationToken, Task> delay = (milliseconds, token) => Task.Delay(milliseconds, token);

        private sealed class DownloadFailure : IOException
        {
            internal readonly string Code;
            internal readonly bool Retryable;
            internal readonly int RetryAfterSeconds;
            internal DownloadFailure(string code, string message, bool retryable = false, int retryAfterSeconds = 0) : base(message)
            { Code = code; Retryable = retryable; RetryAfterSeconds = retryAfterSeconds; }
        }

        public DownloadService(NexusApi nexusApi, Action<object> onProgress, DownloadOptions downloadOptions = null)
        {
            if (nexusApi == null) throw new ArgumentNullException("nexusApi");
            api = nexusApi; progress = onProgress;
            options = (downloadOptions ?? new DownloadOptions()).ValidatedCopy();
            timeoutMilliseconds = options.DownloadTimeoutSeconds * 1000;
            queue = new SemaphoreSlim(options.MaxParallelDownloads, options.MaxParallelDownloads);
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = options.MaxParallelDownloads });
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NexusM/" + typeof(DownloadService).Assembly.GetName().Version.ToString(3));
        }

        public Task<object> DownloadNxmAsync(string uri, string folder, CancellationToken token)
        { return DownloadAsync(DownloadRequest.ParseNxm(uri, folder), token); }

        public async Task<object> DownloadAsync(DownloadRequest request, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException("request");
            request.Domain = JsonTools.Domain(request.Domain);
            if (request.ModId <= 0 || request.FileId <= 0) throw new ArgumentException("Invalid Nexus mod or file ID.");
            if (String.IsNullOrWhiteSpace(request.Folder)) throw new ArgumentException("Choose a download folder in Settings first.");
            string folder = Path.GetFullPath(request.Folder), partial = PartialPath(request, folder);
            lock (requestLock) if (!requests.Add(request.Id)) throw new InvalidOperationException("This file is already downloading.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            token = cancellation.Token;
            var state = new Dictionary<string, object> {
                { "id", request.Id }, { "gameDomain", request.Domain }, { "modId", request.ModId }, { "fileId", request.FileId },
                { "name", "File " + request.FileId }, { "status", "queued" }, { "downloadedBytes", 0L }, { "totalBytes", 0L },
                { "speed", 0L }, { "error", "" }, { "errorCode", "" }, { "path", "" }, { "version", "" },
                { "attempt", 0 }, { "maxAttempts", options.MaxDownloadRetries + 1 }, { "retryAt", 0L } };
            bool acquired = false;
            Emit(state);
            try
            {
                await queue.WaitAsync(token).ConfigureAwait(false); acquired = true;
                token.ThrowIfCancellationRequested();
                object recovered = RecoverCompletion(request);
                if (recovered != null) { Emit(JsonTools.Dict(recovered)); return recovered; }
                Directory.CreateDirectory(folder);
                for (int attempt = 1; attempt <= options.MaxDownloadRetries + 1; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    state["attempt"] = attempt; state["retryAt"] = 0L;
                    state["status"] = "authorizing"; state["error"] = ""; state["errorCode"] = ""; Emit(state);
                    int retryMilliseconds = 0;
                    try
                    {
                        var account = JsonTools.Dict(await Timed(t => api.ValidateAsync(t, false), token).ConfigureAwait(false));
                        if (request.UserId > 0 && request.UserId != JsonTools.Long(account, "user_id"))
                            throw new DownloadFailure("account-mismatch", "This download link belongs to a different Nexus account. Generate a link using the account in Settings.");
                        bool premium = Object.Equals(JsonTools.Get(account, "is_premium"), true);
                        if (!premium && (String.IsNullOrEmpty(request.Key) || request.Expires <= 0 || request.UserId <= 0))
                            throw new DownloadFailure("authorization-expired", "Use Download with Manager on Nexus to authorize this free-account download.");
                        if (!String.IsNullOrEmpty(request.Key) && request.Expires <= JsonTools.UnixNow())
                            throw new DownloadFailure("authorization-expired", "This Nexus download link has expired. Use Download with Manager again to get a fresh link.");
                        var file = JsonTools.Dict(await Timed(t => api.FileAsync(request.Domain, request.ModId, request.FileId, t, false), token).ConfigureAwait(false));
                        long returnedFileId = JsonTools.Long(file, "file_id");
                        if (returnedFileId > 0 && returnedFileId != request.FileId)
                            throw new DownloadFailure("invalid-response", "Nexus returned information for a different file. Reopen the mod and try again.");
                        string filename = SafeFilename(JsonTools.Text(file, "file_name"));
                        if (String.IsNullOrEmpty(filename)) filename = request.Domain + "-" + request.ModId + "-" + request.FileId + ".zip";
                        state["filename"] = filename; state["name"] = JsonTools.Text(file, "name");
                        if (String.IsNullOrEmpty((string)state["name"])) state["name"] = filename;
                        if (((string)state["name"]).Length > 512) state["name"] = ((string)state["name"]).Substring(0, 512);
                        state["version"] = JsonTools.Text(file, "version");
                        if (((string)state["version"]).Length > 256) state["version"] = ((string)state["version"]).Substring(0, 256);
                        // Fetch fresh signed CDN links on every retry; they never enter persisted state.
                        var links = JsonTools.Array(await Timed(t => api.DownloadLinksAsync(request, t, false), token).ConfigureAwait(false));
                        var mirrors = new List<Uri>();
                        foreach (object item in links) {
                            Uri mirror;
                            if (Uri.TryCreate(JsonTools.Text(JsonTools.Dict(item), "URI"), UriKind.Absolute, out mirror) && IsNexusDownloadUri(mirror)) mirrors.Add(mirror);
                        }
                        if (mirrors.Count == 0) throw new DownloadFailure("invalid-response", "Nexus returned no supported download servers. Open a fresh download link and try again.");
                        await TransferAsync(mirrors[(attempt - 1) % mirrors.Count], partial, state, token).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        token.ThrowIfCancellationRequested();
                        var failure = Classify(error);
                        state["errorCode"] = failure.Code;
                        if (!failure.Retryable || attempt > options.MaxDownloadRetries) throw failure;
                        int seconds = Math.Max(failure.RetryAfterSeconds,
                            (int)Math.Min(300L, (long)options.RetryDelaySeconds * (1L << Math.Min(attempt - 1, 10))));
                        state["status"] = "retrying"; state["error"] = failure.Message;
                        state["speed"] = 0L; state["retryAt"] = JsonTools.UnixNow() + seconds; Emit(state);
                        retryMilliseconds = seconds * 1000;
                    }
                    if (retryMilliseconds > 0) { await delay(retryMilliseconds, token).ConfigureAwait(false); continue; }
                    // Finalization is deliberately outside stream retries. A successful archive
                    // must not be downloaded again because a move or UI notification failed.
                    token.ThrowIfCancellationRequested();
                    lock (finalizationLock) FinalizeDownload(partial, folder, state);
                    Emit(state); return new Dictionary<string, object>(state);
                }
                throw new DownloadFailure("download-failed", "Download failed. Retry this entry to try again.");
            }
            catch (OperationCanceledException)
            {
                state["status"] = "cancelled"; state["errorCode"] = "cancelled";
                state["error"] = "Download cancelled. The partial file is kept so a fresh Nexus link can resume it.";
                state["speed"] = 0L; state["retryAt"] = 0L; Emit(state); throw;
            }
            catch (Exception error)
            {
                var failure = Classify(error);
                state["status"] = "failed"; state["errorCode"] = failure.Code; state["error"] = failure.Message;
                state["speed"] = 0L; state["retryAt"] = failure.RetryAfterSeconds > 0 ? JsonTools.UnixNow() + failure.RetryAfterSeconds : 0L; Emit(state);
                throw failure;
            }
            finally { if (acquired) queue.Release(); lock (requestLock) requests.Remove(request.Id); cancellation.Dispose(); }
        }

        private static DownloadFailure Classify(Exception error)
        {
            var known = error as DownloadFailure;
            if (known != null) return known;
            var nexus = error as NexusApiException;
            if (nexus != null) return HttpFailure(nexus.StatusCode, nexus.RetryAfterSeconds);
            if (error is OperationCanceledException || error is TimeoutException)
                return new DownloadFailure("timeout", "The download server timed out. Check your connection and retry.", true);
            if (error is HttpRequestException)
                return new DownloadFailure("network", "The connection to the download server was interrupted.", true);
            if (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
                return new DownloadFailure("disk-error", "The download folder could not be written. Check free space, permissions, and whether another app is using the file.");
            return new DownloadFailure("download-failed", "Download failed. Check the folder and try a fresh Nexus download link.");
        }

        private static DownloadFailure HttpFailure(int code, int retryAfter)
        {
            if (code == 0) return new DownloadFailure("network", "Could not reach Nexus Mods. Check your connection and retry.", true);
            if (code == 401 || code == 403) return new DownloadFailure("authorization-expired", "The download authorization expired. Generate a fresh link using Download with Manager.");
            if (code == 404 || code == 410) return new DownloadFailure("file-unavailable", "This file is no longer available from Nexus. Open the mod to choose an available file.");
            if (code == 408) return new DownloadFailure("timeout", "The download server timed out. Check your connection and retry.", retryAfter <= 3600, retryAfter);
            if (code == 429) return new DownloadFailure("rate-limited", retryAfter > 3600 ? "Nexus requested a longer wait. Retry after the time shown." : "Nexus is limiting download requests. Waiting before retrying.", retryAfter <= 3600, retryAfter);
            if (code >= 500 && code <= 599) return new DownloadFailure("server-error", retryAfter > 3600 ? "The download server requested a longer wait. Retry after the time shown." : "The download server returned HTTP " + code + ". Waiting before retrying.", retryAfter <= 3600, retryAfter);
            return new DownloadFailure("invalid-response", "The download server returned HTTP " + code + ". Open a fresh Nexus link and try again.");
        }

        private async Task<T> Timed<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token, Action abort = null, bool disk = false)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(timeoutMilliseconds);
                var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (timeout.Token.Register(() => stopped.TrySetResult(true)))
                {
                    Task<T> pending = operation(timeout.Token);
                    if (await Task.WhenAny(pending, stopped.Task).ConfigureAwait(false) != pending)
                    {
                        if (abort != null) { try { abort(); } catch (Exception) { } }
                        // Observe a late fault and dispose a late response/stream even when a
                        // framework stream ignores cancellation of its outstanding read.
                        ObserveLateResult(pending);
                        token.ThrowIfCancellationRequested();
                        throw new DownloadFailure(disk ? "disk-timeout" : "timeout", disk ? "Writing the download folder timed out. Check the drive and retry." : "The download server timed out. Check your connection and retry.", !disk);
                    }
                    return await pending.ConfigureAwait(false);
                }
            }
        }

        private static void ObserveLateResult<T>(Task<T> pending)
        {
            pending.ContinueWith(task => {
                if (task.IsFaulted) { var ignored = task.Exception; }
                else if (task.Status == TaskStatus.RanToCompletion) { var disposable = task.Result as IDisposable; if (disposable != null) disposable.Dispose(); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task TransferAsync(Uri source, string partial, Dictionary<string, object> state, CancellationToken token)
        {
            string metadataPath = partial + ".json";
            var old = ReadMetadata(metadataPath);
            long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            string etag = StrongEtag(JsonTools.Text(old, "etag")), modified = JsonTools.Text(old, "lastModified");
            if (offset > 0 && ((etag.Length == 0 && modified.Length == 0) || JsonTools.Text(old, "id") != JsonTools.Text(state, "id") ||
                offset > JsonTools.Long(old, "total"))) offset = 0;
            using (var response = await OpenStreamAsync(source, offset, etag, modified, token).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0) {
                    ResetPartial(partial, metadataPath);
                    throw new DownloadFailure("resume-restarted", "The server could not resume this partial file. Restarting it from the beginning.", true);
                }
                if (!response.IsSuccessStatusCode) throw HttpFailure((int)response.StatusCode, RetryAfter(response));
                if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
                    throw new DownloadFailure("invalid-response", "The download server returned an unsupported response.");
                string media = response.Content.Headers.ContentType == null ? "" : response.Content.Headers.ContentType.MediaType;
                if (String.Equals(media, "text/html", StringComparison.OrdinalIgnoreCase) || String.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase))
                    throw new DownloadFailure("authorization-expired", "The download server returned a web page instead of a file. Generate a fresh Nexus download link.");
                long total;
                string responseEtag = response.Headers.ETag == null ? "" : StrongEtag(response.Headers.ETag.ToString());
                string responseModified = response.Content.Headers.LastModified.HasValue ? response.Content.Headers.LastModified.Value.ToString("R", CultureInfo.InvariantCulture) : "";
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range == null || !String.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) || range.From != offset || !range.Length.HasValue || !range.To.HasValue || range.To.Value != range.Length.Value - 1 || range.Length.Value <= 0)
                        throw new DownloadFailure("invalid-response", "The download server returned an invalid resume range.");
                    total = range.Length.Value;
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != total - offset)
                        throw new DownloadFailure("invalid-response", "The download server returned an invalid resume length.");
                    if (offset > 0 && (total != JsonTools.Long(old, "total") || (etag.Length > 0 ? etag != responseEtag : modified != responseModified))) {
                        ResetPartial(partial, metadataPath);
                        throw new DownloadFailure("resume-restarted", "The source file changed. Restarting the download from the beginning.", true);
                    }
                }
                else { offset = 0; total = response.Content.Headers.ContentLength ?? 0; }
                var fresh = new Dictionary<string, object> { { "id", state["id"] }, { "etag", responseEtag }, { "lastModified", responseModified }, { "total", total } };
                WriteMetadata(metadataPath, fresh);
                state["status"] = "downloading"; state["error"] = ""; state["errorCode"] = "";
                state["totalBytes"] = total; state["downloadedBytes"] = offset; Emit(state);
                long bytes = offset, sinceStart = 0, lastEvent = 0;
                var timer = Stopwatch.StartNew();
                try {
                    using (Stream input = await Timed(t => response.Content.ReadAsStreamAsync(), token, response.Dispose).ConfigureAwait(false))
                    using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
                    {
                        byte[] buffer = new byte[81920];
                        while (true) {
                            int read;
                            try { read = await Timed(t => input.ReadAsync(buffer, 0, buffer.Length, t), token, input.Dispose).ConfigureAwait(false); }
                            catch (IOException error) { if (error is DownloadFailure) throw; token.ThrowIfCancellationRequested(); throw new DownloadFailure("network", "The connection ended while reading the download. Retrying the partial file.", true); }
                            token.ThrowIfCancellationRequested();
                            if (read == 0) break;
                            if (total > 0 && bytes + read > total) throw new DownloadFailure("invalid-response", "The downloaded file exceeded its expected size.");
                            await Timed(async t => { await output.WriteAsync(buffer, 0, read, t).ConfigureAwait(false); return true; }, token, output.Dispose, true).ConfigureAwait(false);
                            bytes += read; sinceStart += read; state["downloadedBytes"] = bytes;
                            if (timer.ElapsedMilliseconds - lastEvent >= 250) {
                                state["speed"] = (long)(sinceStart / Math.Max(timer.Elapsed.TotalSeconds, .1)); Emit(state); lastEvent = timer.ElapsedMilliseconds;
                            }
                        }
                        await Timed(async t => { await output.FlushAsync(t).ConfigureAwait(false); return true; }, token, output.Dispose, true).ConfigureAwait(false);
                        output.Flush(true);
                        if (total > 0 && bytes != total) throw new DownloadFailure("network", "The connection ended before the entire file was received.", true);
                        if (bytes == 0) throw new DownloadFailure("invalid-response", "The download server returned an empty file.");
                        state["totalBytes"] = bytes;
                    }
                }
                finally {
                    try { bytes = new FileInfo(partial).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    state["downloadedBytes"] = bytes; state["speed"] = 0L;
                }
            }
        }

        private async Task<HttpResponseMessage> OpenStreamAsync(Uri source, long offset, string etag, string modified, CancellationToken token)
        {
            Uri current = source;
            for (int hop = 0; hop < 5; hop++) {
                if (!IsNexusDownloadUri(current)) throw new DownloadFailure("invalid-response", "Nexus redirected to an unsupported download address.");
                using (var request = new HttpRequestMessage(HttpMethod.Get, current)) {
                    if (offset > 0) { request.Headers.Range = new RangeHeaderValue(offset, null); if (etag.Length > 0) request.Headers.TryAddWithoutValidation("If-Range", etag); else if (modified.Length > 0) request.Headers.TryAddWithoutValidation("If-Range", modified); }
                    var response = await Timed(t => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, t), token, request.Dispose).ConfigureAwait(false);
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status < 400) {
                        Uri next = response.Headers.Location; response.Dispose();
                        if (next == null) throw new DownloadFailure("invalid-response", "The download server returned an invalid redirect.");
                        current = next.IsAbsoluteUri ? next : new Uri(current, next); continue;
                    }
                    return response;
                }
            }
            throw new DownloadFailure("invalid-response", "The download server returned too many redirects.");
        }

        private static int RetryAfter(HttpResponseMessage response)
        {
            var value = response.Headers.RetryAfter;
            if (value == null) return 0;
            double seconds = value.Delta.HasValue ? value.Delta.Value.TotalSeconds : value.Date.HasValue ? (value.Date.Value - DateTimeOffset.UtcNow).TotalSeconds : 0;
            return (int)Math.Max(0, Math.Min(Int32.MaxValue, Math.Ceiling(seconds)));
        }

        public static bool IsNexusDownloadUri(Uri uri)
        {
            if (uri == null || uri.Scheme != "https" || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Fragment) || !uri.IsDefaultPort) return false;
            string host = uri.DnsSafeHost.ToLowerInvariant();
            return host == "nexusmods.com" || host.EndsWith(".nexusmods.com", StringComparison.Ordinal) || host == "nexus-cdn.com" || host.EndsWith(".nexus-cdn.com", StringComparison.Ordinal);
        }

        private static string StrongEtag(string value)
        { EntityTagHeaderValue tag; return EntityTagHeaderValue.TryParse(value, out tag) && !tag.IsWeak ? tag.ToString() : ""; }

        private static string PartialPath(DownloadRequest request, string folder)
        { return Path.Combine(folder, ".nexusm-" + request.Domain + "-" + request.ModId + "-" + request.FileId + ".part"); }

        private static Dictionary<string, object> ReadMetadata(string path)
        {
            try { if (new FileInfo(path).Length > 16384) return new Dictionary<string, object>(); return JsonTools.Dict(JsonTools.Serializer().DeserializeObject(File.ReadAllText(path))); }
            catch { return new Dictionary<string, object>(); }
        }

        private static void WriteMetadata(string path, Dictionary<string, object> value)
        {
            string next = path + ".tmp";
            byte[] encoded = new UTF8Encoding(false).GetBytes(JsonTools.Serializer().Serialize(value));
            using (var output = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None)) { output.Write(encoded, 0, encoded.Length); output.Flush(true); }
            if (File.Exists(path)) File.Replace(next, path, null); else File.Move(next, path);
        }

        private static void ResetPartial(string partial, string metadata)
        { using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None)) output.Flush(true); TryDelete(metadata); }

        private static void FinalizeDownload(string partial, string folder, Dictionary<string, object> state)
        {
            var info = new FileInfo(partial);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || info.Length <= 0 || info.Length != JsonTools.Long(state, "downloadedBytes"))
                throw new DownloadFailure("disk-error", "The completed partial file changed before it could be saved. Retry this entry.");
            state["fileLength"] = info.Length; state["fileCreatedUtcTicks"] = info.CreationTimeUtc.Ticks; state["fileModifiedUtcTicks"] = info.LastWriteTimeUtc.Ticks;
            state["downloadedAt"] = JsonTools.UnixNow(); state["speed"] = 0L; state["retryAt"] = 0L;
            for (int collision = 0; collision < 100; collision++) {
                string target = AvailablePath(folder, SafeFilename(JsonTools.Text(state, "filename")), JsonTools.Long(state, "fileId"));
                state["path"] = target;
                var intent = new Dictionary<string, object>(state);
                var resume = ReadMetadata(partial + ".json");
                intent["phase"] = "finalizing"; intent["status"] = "completed";
                intent["etag"] = JsonTools.Text(resume, "etag"); intent["lastModified"] = JsonTools.Text(resume, "lastModified"); intent["total"] = info.Length;
                WriteMetadata(partial + ".json", intent);
                try { File.Move(partial, target); state["status"] = "completed"; return; }
                catch (IOException) { if (!File.Exists(target) && !Directory.Exists(target)) throw; }
            }
            throw new DownloadFailure("disk-error", "The download filename is busy. Retry this entry after checking the folder.");
        }

        private static bool MatchesReceipt(Dictionary<string, object> receipt, string path)
        {
            var file = new FileInfo(path);
            return file.Exists && (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 && file.Length > 0 && file.Length == JsonTools.Long(receipt, "fileLength") &&
                file.CreationTimeUtc.Ticks == JsonTools.Long(receipt, "fileCreatedUtcTicks") && file.LastWriteTimeUtc.Ticks == JsonTools.Long(receipt, "fileModifiedUtcTicks");
        }

        public static object RecoverCompletion(DownloadRequest request)
        {
            if (request == null || request.ModId <= 0 || request.FileId <= 0 || String.IsNullOrWhiteSpace(request.Folder)) return null;
            request.Domain = JsonTools.Domain(request.Domain);
            string folder = Path.GetFullPath(request.Folder), partial = PartialPath(request, folder);
            var receipt = ReadMetadata(partial + ".json");
            if (JsonTools.Text(receipt, "id") != request.Id || JsonTools.Text(receipt, "phase") != "finalizing") return null;
            string target = JsonTools.Text(receipt, "path");
            if (String.IsNullOrWhiteSpace(target)) return null;
            try {
                string targetFolder = Path.GetDirectoryName(Path.GetFullPath(target));
                if (String.IsNullOrEmpty(targetFolder) || !String.Equals(targetFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return null;
            } catch (ArgumentException) { return null; } catch (NotSupportedException) { return null; } catch (PathTooLongException) { return null; }
            if (!File.Exists(partial) && MatchesReceipt(receipt, target)) return CompletionState(receipt);
            if (MatchesReceipt(receipt, partial)) {
                var state = CompletionState(receipt);
                FinalizeDownload(partial, folder, state);
                return state;
            }
            return null;
        }

        private static Dictionary<string, object> CompletionState(Dictionary<string, object> receipt)
        {
            var state = new Dictionary<string, object>(receipt); state.Remove("phase"); state.Remove("etag"); state.Remove("lastModified"); state.Remove("total");
            state["status"] = "completed"; state["error"] = ""; state["errorCode"] = ""; state["speed"] = 0L; state["retryAt"] = 0L;
            return state;
        }

        public static void AcknowledgeCompletion(object value)
        {
            try {
                var row = JsonTools.Dict(value);
                if (JsonTools.Text(row, "status") != "completed") return;
                string target = JsonTools.Text(row, "path"), domain = JsonTools.Domain(JsonTools.Text(row, "gameDomain"));
                if (String.IsNullOrWhiteSpace(target)) return;
                var request = new DownloadRequest { Domain = domain, ModId = JsonTools.Long(row, "modId"), FileId = JsonTools.Long(row, "fileId") };
                string marker = PartialPath(request, Path.GetDirectoryName(Path.GetFullPath(target))) + ".json";
                var receipt = ReadMetadata(marker);
                if (JsonTools.Text(receipt, "phase") == "finalizing" && JsonTools.Text(receipt, "id") == request.Id &&
                    String.Equals(JsonTools.Text(receipt, "path"), target, StringComparison.OrdinalIgnoreCase) && MatchesReceipt(receipt, target)) TryDelete(marker);
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
        }

        private static string SafeFilename(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return "";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.', ' ');
            if (name.Length > 180) { string extension = Path.GetExtension(name); if (extension.Length > 20) extension = extension.Substring(0, 20); name = name.Substring(0, 150) + extension; }
            if (Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\\.)", RegexOptions.IgnoreCase)) name = "_" + name;
            return name;
        }

        private static string AvailablePath(string folder, string filename, long fileId)
        {
            string result = Path.Combine(folder, filename); int number = 0;
            while (File.Exists(result) || Directory.Exists(result)) { number++; result = Path.Combine(folder, Path.GetFileNameWithoutExtension(filename) + " (" + fileId + (number == 1 ? "" : "-" + number) + ")" + Path.GetExtension(filename)); }
            return result;
        }

        private void Emit(Dictionary<string, object> state)
        {
            if (progress == null) return;
            try { progress(new Dictionary<string, object>(state)); }
            catch (Exception) { /* A UI callback cannot change the outcome of an archive transfer. */ }
        }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        public void Dispose() { lifetime.Cancel(); client.Dispose(); }
    }
}
