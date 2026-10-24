using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NexusM;

internal sealed class FilePreviewHandler : HttpMessageHandler
{
    internal string Link = "https://file-metadata.nexusmods.com/file/nexus-files-s3-meta/3474/3423/Test archive.json";
    internal long FileId = 23974;
    internal string Json = "{\"children\":[{\"path\":\"Data\",\"type\":\"directory\",\"children\":[{\"path\":\"Data\\\\file<unsafe>.esp\",\"type\":\"file\",\"size\":\"12.8 kB\"}]},{\"path\":\"readme.txt\",\"type\":\"file\",\"size\":\"404 B\"}]}";
    internal HttpStatusCode Status = HttpStatusCode.OK;
    internal bool OversizedHeader;
    internal Stream ResponseStream;
    internal CancellationTokenSource CancelMetadata;
    internal readonly List<Uri> Requests = new List<Uri>();
    internal bool ApiAuthorized, MetadataAuthorized;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri);
        if (request.RequestUri.Host == "api.nexusmods.com")
        {
            ApiAuthorized = request.Headers.Contains("apikey");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonTools.Serializer().Serialize(new { file_id = FileId, content_preview_link = Link })) };
        }
        MetadataAuthorized = request.Headers.Contains("apikey") || request.Headers.Authorization != null;
        if (CancelMetadata != null) { CancelMetadata.Cancel(); await Task.Delay(100, token); }
        var response = new HttpResponseMessage(Status) { Content = ResponseStream == null ? (HttpContent)new StringContent(Json, Encoding.UTF8, "application/json") : new StreamContent(ResponseStream) };
        if (Status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://untrusted.invalid/redirect");
        if (OversizedHeader) response.Content.Headers.ContentLength = 4 * 1024 * 1024 + 1;
        return response;
    }
}

internal sealed class PreviewTestStream : Stream
{
    internal bool Stall, Disposed;
    internal byte[] Data = new byte[0];
    private int position;
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { return position; } set { throw new NotSupportedException(); } }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        if (Stall) await Task.Delay(Timeout.Infinite, token);
        token.ThrowIfCancellationRequested();
        int read = Math.Min(count, Data.Length - position);
        Array.Copy(Data, position, buffer, offset, read); position += read; return read;
    }
    public override int Read(byte[] buffer, int offset, int count) { return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult(); }
    public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    public override void Flush() { }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}

internal static class FilePreviewTests
{
    private static int passed;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static NexusApi Api(FilePreviewHandler handler)
    {
        var api = new NexusApi("synthetic-preview-key");
        FieldInfo field = typeof(NexusApi).GetField("client", BindingFlags.NonPublic | BindingFlags.Instance);
        ((HttpClient)field.GetValue(api)).Dispose(); field.SetValue(api, new HttpClient(handler)); return api;
    }
    private static Task<object> Preview(NexusApi api, CancellationToken token = default(CancellationToken)) { return api.FileContentsAsync("baldursgate3", 3423, 23974, token); }
    private static async Task<bool> Rejected(NexusApi api)
    {
        try { await Preview(api); return false; }
        catch (NexusApiException error) { return !error.Message.Contains("synthetic-preview-key") && !error.Message.Contains("unsafe response"); }
    }
    private static int Main(string[] args)
    {
        try { Run().GetAwaiter().GetResult(); if (args.Contains("--live")) Live().GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
    private static async Task Run()
    {
        var server = new FilePreviewHandler();
        using (var api = Api(server))
        {
            var result = JsonTools.Dict(await Preview(api)); var rows = JsonTools.Array(JsonTools.Get(result, "entries"));
            Check(JsonTools.Get(result, "available") is bool && (bool)result["available"] && rows.Length == 3, "A public manifest returns directories and exact archive files without downloading an archive");
            Check(server.Requests.Count == 2 && server.ApiAuthorized && !server.MetadataAuthorized, "Only the Nexus API request receives the personal API key");
            var file = JsonTools.Dict(rows[1]);
            Check(JsonTools.Text(file, "path") == "Data/file<unsafe>.esp" && JsonTools.Long(file, "size") == 12800 && JsonTools.Text(file, "sizeLabel") == "12.8 kB", "Preview paths normalize separators while preserving text and published sizes");
        }
        server = new FilePreviewHandler { Link = "" };
        using (var api = Api(server))
        {
            var result = JsonTools.Dict(await Preview(api));
            Check(!(bool)result["available"] && JsonTools.Array(result["entries"]).Length == 0 && server.Requests.Count == 1, "A file with no preview link returns a clear unavailable result without another request");
        }
        server = new FilePreviewHandler { FileId = 999 };
        using (var api = Api(server)) Check(await Rejected(api) && server.Requests.Count == 1, "Metadata for a different file cannot be previewed");
        foreach (string link in new[] {
            "http://file-metadata.nexusmods.com/file/nexus-files-s3-meta/test.json",
            "https://file-metadata.nexusmods.com.evil.invalid/file/nexus-files-s3-meta/test.json",
            "https://user:pass@file-metadata.nexusmods.com/file/nexus-files-s3-meta/test.json",
            "https://file-metadata.nexusmods.com:444/file/nexus-files-s3-meta/test.json",
            "https://file-metadata.nexusmods.com/file/nexus-files-s3-meta/test.json#fragment",
            "https://file-metadata.nexusmods.com/other/test.json" })
        {
            server = new FilePreviewHandler { Link = link };
            using (var api = Api(server)) Check(await Rejected(api) && server.Requests.Count == 1, "Unsafe or unsupported preview addresses are rejected before contacting them");
        }
        server = new FilePreviewHandler { Status = HttpStatusCode.NotFound };
        using (var api = Api(server)) Check(!(bool)JsonTools.Dict(await Preview(api))["available"] && !server.MetadataAuthorized, "A missing manifest retains a friendly unavailable state");
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Found, HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable })
        {
            server = new FilePreviewHandler { Status = status, Json = "unsafe response" };
            using (var api = Api(server)) Check(await Rejected(api) && server.Requests.Count == 2 && !server.MetadataAuthorized, "Preview errors and redirects cannot forward credentials or leak response bodies");
        }
        foreach (string body in new[] { "<html>unsafe response</html>", "{}", "{\"children\":null}", "{\"__type\":\"unsafe response\"}" })
        {
            server = new FilePreviewHandler { Json = body };
            using (var api = Api(server)) Check(await Rejected(api), "Unreadable or incomplete manifest responses return a controlled error");
        }
        server = new FilePreviewHandler { OversizedHeader = true };
        using (var api = Api(server)) Check(await Rejected(api), "The manifest size limit rejects an oversized advertised response");
        server = new FilePreviewHandler { ResponseStream = new PreviewTestStream { Data = new byte[4 * 1024 * 1024 + 1] } };
        using (var api = Api(server)) Check(await Rejected(api) && ((PreviewTestStream)server.ResponseStream).Disposed, "An unknown-length oversized stream is bounded and disposed");
        server = new FilePreviewHandler { Json = "{\"children\":[]}" };
        using (var api = Api(server))
        {
            var result = JsonTools.Dict(await Preview(api));
            Check((bool)result["available"] && JsonTools.Array(result["entries"]).Length == 0, "An available empty manifest stays distinct from an unavailable preview");
        }
        server = new FilePreviewHandler { Json = JsonTools.Serializer().Serialize(new { children = Enumerable.Range(0, 10001).Select(n => new { path = "file" + n, type = "file", size = "1 B" }).ToArray() }) };
        using (var api = Api(server))
        {
            var result = JsonTools.Dict(await Preview(api));
            Check((bool)result["truncated"] && JsonTools.Array(result["entries"]).Length == 10000, "Large archive listings are bounded and report truncation");
        }
        server = new FilePreviewHandler();
        using (var api = Api(server)) using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); bool cancelled = false;
            try { await Preview(api, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && server.Requests.Count == 0, "Cancelled previews make no network requests");
        }
        server = new FilePreviewHandler();
        using (var api = Api(server)) using (var cancel = new CancellationTokenSource())
        {
            server.CancelMetadata = cancel; bool cancelled = false;
            try { await Preview(api, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !server.MetadataAuthorized, "Cancellation during the manifest request exits without exposing credentials");
        }
        var stalled = new PreviewTestStream { Stall = true };
        server = new FilePreviewHandler { ResponseStream = stalled };
        using (var api = Api(server)) using (var cancel = new CancellationTokenSource())
        {
            cancel.CancelAfter(50); bool cancelled = false;
            try { await Preview(api, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && stalled.Disposed, "Cancellation during a stalled manifest stream releases its response");
        }
        stalled = new PreviewTestStream { Stall = true };
        server = new FilePreviewHandler { ResponseStream = stalled };
        using (var api = Api(server))
        {
            var fetch = typeof(NexusApi).GetMethod("FetchFilePreviewAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            bool timedOut = false;
            try { await (Task<object>)fetch.Invoke(api, new object[] { new Uri(server.Link), CancellationToken.None, 50 }); }
            catch (NexusApiException error) { timedOut = error.StatusCode == 408 && !error.Message.Contains("synthetic-preview-key"); }
            server.ResponseStream = null;
            var retry = JsonTools.Dict(await Preview(api));
            Check(timedOut && stalled.Disposed && (bool)retry["available"], "A stalled manifest body times out, releases its slot, and allows retry");
        }
        Console.WriteLine("Passed " + passed + " file preview checks.");
    }
    private static async Task Live()
    {
        string keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusM", "api-key.bin");
        byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser);
        string key = Encoding.UTF8.GetString(bytes); Array.Clear(bytes, 0, bytes.Length);
        using (var api = new NexusApi(key))
        {
            var result = JsonTools.Dict(await api.FileContentsAsync("baldursgate3", 3423, 23974));
            Check((bool)result["available"] && JsonTools.Array(result["entries"]).Length >= 2, "Live read-only Nexus preview contains the known archive's file listing");
        }
    }
}
