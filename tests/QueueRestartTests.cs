using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NexusM
{
    // Kills only the child created by this fixture. All API/CDN requests terminate
    // in in-process handlers and all state/archives live under a unique temp root.
    internal static class QueueRestartTests
    {
        private const int ArchiveLength = 81920 * 4;
        private const int PartialLength = ArchiveLength / 2;
        private const string SyntheticKey = "queue-restart-fixture-only-key";
        private static readonly List<object> Checks = new List<object>();

        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--crash-child") {
                string childRoot = Path.GetFullPath(args[1]);
                if (!SafeRoot(childRoot)) return 2;
                try { RunChild(childRoot).GetAwaiter().GetResult(); return 3; }
                catch (Exception error) { File.WriteAllText(Path.Combine(childRoot, "child-error.txt"), error.ToString()); return 4; }
            }
            string report = Path.GetFullPath(args[0]);
            string root = Path.Combine(Path.GetTempPath(), "NexusM-QueueRestartTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { RunParent(root).GetAwaiter().GetResult(); }
            catch (Exception error) { Checks.Add(Data.Obj("test", "Forced restart fixture", "passed", false, "error", error.ToString())); }
            finally {
                Directory.CreateDirectory(Path.GetDirectoryName(report));
                File.WriteAllText(report, Data.Json.Serialize(Data.Obj("passed", Checks.All(check => Data.Bool(check, "passed")), "checks", Checks)), new UTF8Encoding(false));
                if (!SafeRoot(root)) throw new InvalidOperationException("Unsafe queue restart fixture cleanup path.");
                Directory.Delete(root, true);
            }
            return Checks.All(check => Data.Bool(check, "passed")) ? 0 : 1;
        }

        private static bool SafeRoot(string value)
        {
            string absolute = Path.GetFullPath(value);
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(absolute).StartsWith("NexusM-QueueRestartTests-", StringComparison.Ordinal) &&
                String.Equals(Path.GetDirectoryName(absolute).TrimEnd(Path.DirectorySeparatorChar), temporary.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Checks.Add(Data.Obj("test", name, "passed", true));
        }
        private static string Folder(string root) { return Path.Combine(root, "original-archives"); }
        private static string Partial(string root) { return Path.Combine(Folder(root), ".nexusm-fixturegame-1-101.part"); }
        private static string Id(long fileId) { return "fixturegame:1:" + fileId; }
        private static byte[] Payload(long fileId)
        {
            var bytes = new byte[ArchiveLength];
            for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)((index + fileId) % 251);
            return bytes;
        }
        private static DownloadRequest Request(long fileId, string folder)
        { return new DownloadRequest { Domain = "fixturegame", ModId = 1, FileId = fileId, Folder = folder }; }
        private static void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            var previous = field.GetValue(target) as HttpClient;
            if (previous != null) previous.Dispose();
            field.SetValue(target, value);
        }
        private static void PersistProgress(Storage store, object update, object gate)
        {
            lock (gate) {
                var incoming = Data.Map(update);
                var row = store.Downloads.Select(Data.Map).Single(item => Data.Str(item, "id") == Data.Str(incoming, "id"));
                foreach (var pair in incoming) row[pair.Key] = pair.Value;
                if (Data.Str(row, "status") == "completed") row["resumeOnStartup"] = false;
                store.Save();
            }
        }

        private static async Task RunChild(string root)
        {
            Directory.CreateDirectory(Folder(root));
            var store = new Storage(Path.Combine(root, "data"));
            store.Settings["downloadFolder"] = Folder(root);
            // Newest rows are displayed first; queueOrder retains the actual FIFO.
            foreach (long fileId in new long[] { 103, 102, 101 }) store.Downloads.Add(Data.Obj(
                "id", Id(fileId), "gameDomain", "fixturegame", "modId", 1L, "fileId", fileId,
                "name", "Restart fixture " + fileId, "status", fileId == 102 ? "waiting-authorization" : "queued",
                "folder", Folder(root), "queuedAt", 1000L + fileId, "queueOrder", fileId - 100,
                "resumeOnStartup", true, "downloadedBytes", 0L, "totalBytes", (long)ArchiveLength));
            store.Save();
            var gate = new object();
            using (var api = new NexusApi(SyntheticKey)) {
                Set(api, "client", new HttpClient(new FakeApi()));
                using (var service = new DownloadService(api, value => PersistProgress(store, value, gate),
                    new DownloadOptions { MaxParallelDownloads = 2, MaxDownloadRetries = 0 })) {
                    Set(service, "client", new HttpClient(new FakeCdn(root, true)) { Timeout = Timeout.InfiniteTimeSpan });
                    await service.DownloadAsync(Request(101, Folder(root)), CancellationToken.None);
                }
            }
        }

        private static async Task RunParent(string root)
        {
            var start = new ProcessStartInfo {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = "--crash-child \"" + root + "\"",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            // This is the only process handle ever killed by the fixture.
            using (Process child = Process.Start(start)) {
                if (child == null) throw new Exception("The isolated restart child did not start.");
                try {
                    var wait = Stopwatch.StartNew();
                    string ready = Path.Combine(root, "partial-ready");
                    while (!File.Exists(ready) && !child.HasExited && wait.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(25);
                    if (!File.Exists(ready)) {
                        string error = Path.Combine(root, "child-error.txt");
                        throw new Exception("Child did not reach its durable partial checkpoint. " + (File.Exists(error) ? File.ReadAllText(error) : ""));
                    }
                    Check(!child.HasExited && new FileInfo(Partial(root)).Length == PartialLength,
                        "a live isolated transfer reaches a real partial file before the forced process exit");
                    child.Kill();
                    Check(child.WaitForExit(5000), "only the fixture child is terminated and its archive handles close");
                }
                finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
            }

            var reopened = new Storage(Path.Combine(root, "data"));
            Check(reopened.Downloads.Select(row => Data.Str(row, "id")).OrderBy(id => id).SequenceEqual(new[] { Id(101), Id(102), Id(103) }),
                "forced exit preserves the active archive and every queued job without duplicate IDs");
            var ordered = reopened.Downloads.OrderBy(row => Data.Num(row, "queueOrder")).ToList();
            Check(ordered.Select(row => Data.Num(row, "fileId")).SequenceEqual(new long[] { 101, 102, 103 }) &&
                ordered.All(row => Data.Str(row, "status") == "interrupted" && Data.Bool(row, "resumeOnStartup") && Data.Str(row, "folder") == Folder(root)),
                "startup restores FIFO, original folders, and recovery intent for active and waiting jobs");
            byte[] partial = File.ReadAllBytes(Partial(root));
            Check(partial.SequenceEqual(Payload(101).Take(PartialLength)), "the crashed archive retains the exact downloaded byte prefix");
            var sidecar = Data.Map(Data.Json.DeserializeObject(File.ReadAllText(Partial(root) + ".json")));
            Check(Data.Str(sidecar, "id") == Id(101) && Data.Str(sidecar, "etag") == "\"queue-fixture-101-v1\"",
                "the crash retains the exact file identity and strong resume validator");

            string newDefault = Path.Combine(root, "changed-default");
            Directory.CreateDirectory(newDefault); reopened.Settings["downloadFolder"] = newDefault; reopened.Save();
            var server = new FakeCdn(root, false); var gate = new object();
            using (var api = new NexusApi(SyntheticKey)) {
                Set(api, "client", new HttpClient(new FakeApi()));
                using (var service = new DownloadService(api, value => PersistProgress(reopened, value, gate),
                    new DownloadOptions { MaxParallelDownloads = 2, MaxDownloadRetries = 0 })) {
                    Set(service, "client", new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan });
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10))) {
                        object[] completed = await Task.WhenAll(ordered.Select(row => service.DownloadAsync(
                            Request(Data.Num(row, "fileId"), Data.Str(row, "folder")), timeout.Token)));
                        foreach (var value in completed) DownloadService.AcknowledgeCompletion(value);
                    }
                }
            }
            Check(server.Offsets.Count == 3 && server.Offsets[101] == PartialLength && server.Offsets[102] == 0 && server.Offsets[103] == 0 && server.IfRange[101] == "\"queue-fixture-101-v1\"",
                "a fresh session resumes the partial with Range and If-Range while starting every queued file exactly once");
            Check(Directory.GetFiles(Folder(root), "*.zip").Length == 3 && Directory.GetFiles(newDefault).Length == 0 &&
                Directory.GetFiles(Folder(root), "*.part*").Length == 0 && reopened.Downloads.All(row => Data.Str(row, "status") == "completed"),
                "all pending jobs finish once in their pinned folder and acknowledged partial sidecars are removed");
            foreach (var row in reopened.Downloads) Check(File.ReadAllBytes(Data.Str(row, "path")).SequenceEqual(Payload(Data.Num(row, "fileId"))),
                "completed archive " + Data.Num(row, "fileId") + " is byte-exact after the forced restart");
            var again = new Storage(Path.Combine(root, "data"));
            Check(again.Downloads.Count == 3 && again.Downloads.All(row => Data.Str(row, "status") == "completed" && !Data.Bool(row, "resumeOnStartup")),
                "completed jobs remain terminal after another restart");
            string saved = File.ReadAllText(Path.Combine(root, "data", "state.json"));
            Check(!saved.Contains(SyntheticKey) && !saved.Contains("synthetic-cdn-secret"), "crash checkpoints and final queue state exclude API keys and signed CDN credentials");
        }

        private sealed class FakeApi : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (request.RequestUri.Host != "api.nexusmods.com") throw new Exception("Fixture blocked an unexpected API host.");
                string path = request.RequestUri.AbsolutePath, json;
                if (path.EndsWith("/users/validate.json", StringComparison.Ordinal)) json = "{\"user_id\":7,\"is_premium\":true}";
                else {
                    string[] parts = path.Split('/');
                    long fileId;
                    bool links = path.EndsWith("/download_link.json", StringComparison.Ordinal);
                    if (!Int64.TryParse(links ? parts[parts.Length - 2] : Path.GetFileNameWithoutExtension(path), out fileId) || fileId < 101 || fileId > 103)
                        throw new Exception("Fixture blocked an unexpected API route.");
                    json = links ? "[{\"URI\":\"https://cf-files.nexusmods.com/fixture/" + fileId + ".zip?key=synthetic-cdn-secret\"}]" :
                        "{\"file_id\":" + fileId + ",\"file_name\":\"queue-fixture-" + fileId + ".zip\",\"name\":\"Restart fixture " + fileId + "\",\"version\":\"1.0\"}";
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
        }
        private sealed class FakeCdn : HttpMessageHandler
        {
            private readonly string root;
            private readonly bool block;
            internal readonly Dictionary<long, long> Offsets = new Dictionary<long, long>();
            internal readonly Dictionary<long, string> IfRange = new Dictionary<long, string>();
            internal FakeCdn(string value, bool pause) { root = value; block = pause; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (request.RequestUri.Host != "cf-files.nexusmods.com" || request.Headers.Contains("apikey")) throw new Exception("Fixture blocked an unexpected CDN host or API credential.");
                long fileId;
                if (!Int64.TryParse(Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath), out fileId) || fileId < 101 || fileId > 103)
                    throw new Exception("Fixture blocked an unexpected CDN archive.");
                long offset = request.Headers.Range == null ? 0 : request.Headers.Range.Ranges.Single().From.Value;
                lock (Offsets) {
                    if (Offsets.ContainsKey(fileId)) throw new Exception("Fixture detected a duplicate archive request.");
                    Offsets[fileId] = offset; IfRange[fileId] = request.Headers.IfRange == null ? "" : request.Headers.IfRange.ToString();
                }
                byte[] payload = Payload(fileId);
                HttpContent content = block ? (HttpContent)new StreamContent(new CrashStream(payload, root)) : new ByteArrayContent(payload, (int)offset, payload.Length - (int)offset);
                content.Headers.ContentLength = payload.Length - offset;
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
                response.Headers.ETag = new EntityTagHeaderValue("\"queue-fixture-" + fileId + "-v1\"");
                if (offset > 0) content.Headers.ContentRange = new ContentRangeHeaderValue(offset, payload.Length - 1, payload.Length);
                return Task.FromResult(response);
            }
        }
        private sealed class CrashStream : Stream
        {
            private readonly byte[] bytes;
            private readonly string root;
            private int offset;
            internal CrashStream(byte[] value, string folder) { bytes = value; root = folder; }
            public override async Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken token)
            {
                if (offset < PartialLength) {
                    int read = Math.Min(count, PartialLength - offset);
                    Array.Copy(bytes, offset, buffer, start, read); offset += read; return read;
                }
                if (new FileInfo(Partial(root)).Length != PartialLength) throw new Exception("The archive prefix was not written before the next read.");
                using (var ready = new FileStream(Path.Combine(root, "partial-ready"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) ready.Flush(true);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return 0;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override long Length { get { return bytes.Length; } }
            public override long Position { get { return offset; } set { throw new NotSupportedException(); } }
            public override int Read(byte[] buffer, int start, int count) { throw new NotSupportedException(); }
            public override long Seek(long value, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int start, int count) { throw new NotSupportedException(); }
            public override void Flush() { }
        }
    }
}
