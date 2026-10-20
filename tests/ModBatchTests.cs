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

sealed class ModBatchHandler : HttpMessageHandler
{
    public int Calls;
    public string Mode = "normal";
    public string Query;
    public string[] Requested;
    public static long Uid(long id) { return (6234L << 32) | id; }
    public static Dictionary<string, object> Node(long id)
    {
        return new Dictionary<string, object> {
            { "uid", Uid(id).ToString(CultureInfo.InvariantCulture) }, { "modId", id }, { "gameId", 6234 },
            { "name", "Test mod " + id }, { "summary", "" }, { "version", "1.0" }, { "author", null },
            { "pictureUrl", null }, { "status", "published" }, { "updatedAt", "2026-10-20T03:19:48Z" },
            { "viewerDownloaded", "2026-10-20T11:52:36Z" }, { "viewerUpdateAvailable", false }
        };
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++;
        var body = JsonTools.Dict(JsonTools.Serializer().DeserializeObject(await request.Content.ReadAsStringAsync()));
        Query = JsonTools.Text(body, "query");
        var variables = JsonTools.Dict(JsonTools.Get(body, "variables"));
        Requested = JsonTools.Array(JsonTools.Get(variables, "uids")).Select(Convert.ToString).ToArray();
        int offset = (int)JsonTools.Long(variables, "offset");
        int count = variables.ContainsKey("count") ? (int)JsonTools.Long(variables, "count") : 100;
        if (Mode == "network") throw new HttpRequestException("synthetic test failure");
        if (Mode == "http") return new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
        object response;
        if (Mode == "errors") response = new { errors = new[] { new { message = "Synthetic GraphQL failure" } }, data = new { modsByUid = new { nodes = new object[0] } } };
        else if (Mode == "missing-envelope") response = new { data = new { modsByUid = new { } } };
        else if (Mode == "null-nodes") response = new { data = new { modsByUid = new { nodes = (object)null } } };
        else if (Mode == "empty") response = new { data = new { modsByUid = new { nodes = new object[0], totalCount = 0 } } };
        else
        {
            var nodes = Requested.Reverse().Skip(offset).Take(count).Select(x => (object)Node(long.Parse(x, CultureInfo.InvariantCulture) & UInt32.MaxValue)).ToList();
            if (Mode == "second-page-missing" && offset > 0) nodes.Clear();
            if (Mode == "second-page-http" && offset > 0) return new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
            if (Mode == "malformed")
            {
                nodes.Clear();
                nodes.Add(Node(2)); nodes.Add(null); nodes.Add(Node(999)); nodes.Add(Node(2));
                var wrongGame = Node(1); wrongGame["gameId"] = 1; nodes.Add(wrongGame);
                var invalidTime = Node(1); invalidTime["updatedAt"] = "invalid"; nodes.Add(invalidTime);
                var missingName = Node(1); missingName.Remove("name"); nodes.Add(missingName);
                var missingStatus = Node(1); missingStatus["status"] = null; nodes.Add(missingStatus);
            }
            response = new { data = new { modsByUid = new { nodes = nodes, totalCount = Requested.Length } } };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonTools.Serializer().Serialize(response), Encoding.UTF8, "application/json") };
    }
}

static class ModBatchTests
{
    static int passed;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    static NexusApi Api(ModBatchHandler handler)
    {
        var api = new NexusApi("synthetic-test-key");
        var field = typeof(NexusApi).GetField("client", BindingFlags.NonPublic | BindingFlags.Instance);
        ((HttpClient)field.GetValue(api)).Dispose();
        field.SetValue(api, new HttpClient(handler));
        return api;
    }
    static int Main(string[] args)
    {
        try { Run().GetAwaiter().GetResult(); if (args.Contains("--live")) Live().GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    static async Task Run()
    {
        var handler = new ModBatchHandler();
        using (var api = Api(handler))
        {
            var ids = Enumerable.Range(1, 100).Select(x => ModBatchHandler.Uid(x)).ToArray();
            var rows = JsonTools.Array(await api.ModsByUidAsync(ids.Take(NexusApi.ModBatchSize)));
            Check(handler.Calls == 1 && rows.Length == 80 && handler.Requested.Length == 80, "80 mod summaries and account dates use one request at the server page limit");
            int calls = handler.Calls;
            rows = JsonTools.Array(await api.ModsByUidAsync(ids));
            Check(handler.Calls == calls + 2 && rows.Length == 100, "100 requested UIDs follow pagination instead of silently dropping the last 20");
            Check(JsonTools.Long(JsonTools.Dict(rows[0]), "uid") == ids[99] && JsonTools.Text(JsonTools.Dict(rows[0]), "viewerDownloaded") == "2026-10-20T11:52:36Z", "Unordered responses retain UID and viewer dates");
            Check(handler.Query.Contains("updatedAt") && handler.Query.Contains("pictureUrl") && !handler.Query.Contains("description"), "List query includes update metadata without full descriptions");
            calls = handler.Calls;
            Check(JsonTools.Array(await api.ModsByUidAsync(new long[] { 0, -1 })).Length == 0 && handler.Calls == calls, "Empty and invalid UID input performs no network request");
            rows = JsonTools.Array(await api.ModsByUidAsync(new[] { ids[0], ids[0], ids[1], 0L }));
            Check(rows.Length == 2 && handler.Requested.Length == 2, "Duplicate UIDs are requested only once");
            bool rejected = false; calls = handler.Calls;
            try { await api.ModsByUidAsync(Enumerable.Range(1, 101).Select(x => ModBatchHandler.Uid(x))); } catch (ArgumentException) { rejected = true; }
            Check(rejected && handler.Calls == calls, "Oversized batches fail before sending any request");
            handler.Mode = "malformed";
            rows = JsonTools.Array(await api.ModsByUidAsync(new[] { ids[0], ids[1], ids[2] }));
            Check(rows.Length == 1 && JsonTools.Long(JsonTools.Dict(rows[0]), "uid") == ids[1], "Null, malformed, duplicate and unrelated nodes cannot replace cached mods");
            handler.Mode = "empty";
            Check(JsonTools.Array(await api.ModsByUidAsync(new[] { ids[0] })).Length == 0, "Deleted or inaccessible mods may be omitted without fabricated metadata");
            foreach (string mode in new[] { "second-page-missing", "second-page-http" })
            {
                handler.Mode = mode; rejected = false;
                try { await api.ModsByUidAsync(ids); } catch (NexusApiException) { rejected = true; }
                Check(rejected, "A " + mode + " failure cannot produce a misleading successful partial check");
            }
            foreach (string mode in new[] { "missing-envelope", "null-nodes", "errors", "http", "network" })
            {
                handler.Mode = mode; rejected = false;
                try { await api.ModsByUidAsync(new[] { ids[0] }); }
                catch (NexusApiException ex) { rejected = !ex.Message.Contains("synthetic-test-key"); }
                Check(rejected, "Batch " + mode + " failure is reported without credentials");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel(); calls = handler.Calls; rejected = false;
                try { await api.ModsByUidAsync(ids, cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
                Check(rejected && handler.Calls == calls, "Cancelled checks stop before network access");
            }
            handler.Mode = "normal"; calls = handler.Calls;
            rows = JsonTools.Array(await api.BatchViewerDownloadsAsync(Enumerable.Range(1, 101).Select(x => ModBatchHandler.Uid(x))));
            Check(rows.Length == 101 && handler.Calls == calls + 2, "Existing viewer-download batching remains compatible");
        }
        Console.WriteLine("Passed " + passed + " batch API checks.");
    }
    static async Task Live()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusM");
        byte[] protectedKey = File.ReadAllBytes(Path.Combine(root, "api-key.bin"));
        byte[] decrypted = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
        string key = Encoding.UTF8.GetString(decrypted); Array.Clear(decrypted, 0, decrypted.Length);
        var state = JsonTools.Dict(JsonTools.Serializer().DeserializeObject(File.ReadAllText(Path.Combine(root, "state.json"))));
        var ids = JsonTools.Array(JsonTools.Get(state, "mods")).Select(x => JsonTools.Long(JsonTools.Dict(x), "uid")).Where(x => x > 0).Distinct().Take(100).ToArray();
        if (ids.Length == 0) throw new Exception("No cached mod UIDs are available for the read-only live check.");
        using (var api = new NexusApi(key))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var nodes = JsonTools.Array(await api.ModsByUidAsync(ids));
            clock.Stop();
            Check(nodes.Length > 0 && nodes.Length <= ids.Length, "Live API returns verified library metadata and account dates");
            Console.WriteLine("Live batch: requested " + ids.Length + ", returned " + nodes.Length + ", elapsed " + clock.ElapsedMilliseconds + " ms.");
        }
    }
}
