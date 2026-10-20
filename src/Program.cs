using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NexusM
{
    internal static class Program
    {
        internal const string PipeName = "NexusM-";
        [STAThread]
        private static int Main(string[] args) {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 8;
            if (args.Length >= 2 && args[0] == "--self-test") return SelfTest.Run(args[1]).GetAwaiter().GetResult();
            bool smoke = args.Length >= 2 && args[0] == "--smoke-test";
            string identity = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value.Replace('-', '_');
            bool first;
            using (var mutex = new Mutex(true, "Local\\NexusM_" + identity + (smoke ? "_smoke" : ""), out first)) {
                string nxm = args.FirstOrDefault(x => x.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
                if (!first) {
                    try { using (var pipe = new NamedPipeClientStream(".", PipeName + identity, PipeDirection.Out)) { pipe.Connect(2500); using (var writer = new StreamWriter(pipe)) { writer.WriteLine(nxm ?? "activate"); writer.Flush(); } } } catch { }
                    return 0;
                }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                var storage = new Storage();
                using (var form = new MainForm(storage, nxm, smoke ? args[1] : null, identity)) {
                    Application.Run(form);
                }
            }
            return 0;
        }
    }

    internal static class SelfTest
    {
        internal static async Task<int> Run(string report) {
            var checks = new System.Collections.Generic.List<object>();
            bool pass = true;
            try {
                var parsed = Controller.ParseModUrl("https://www.nexusmods.com/games/baldursgate3/mods/5508?tab=files");
                checks.Add(Data.Obj("test", "Modern Nexus URL", "passed", Data.Str(parsed, "gameDomain") == "baldursgate3" && Data.Num(parsed, "modId") == 5508));
                bool rejected = false; try { Controller.ParseModUrl("https://nexusmods.com.evil.invalid/baldursgate3/mods/1"); } catch { rejected = true; }
                checks.Add(Data.Obj("test", "Reject lookalike domain", "passed", rejected));
                var storage = new Storage(); string key = storage.ReadKey();
                checks.Add(Data.Obj("test", "Windows-encrypted API key available", "passed", key.Length > 0));
                if (key.Length > 0) using (var api = new NexusApi(key)) {
                    var user = await api.ValidateAsync();
                    checks.Add(Data.Obj("test", "Live Nexus authentication without exposing credential", "passed", Data.Num(user, "user_id") > 0 && !Data.Map(user).ContainsKey("key")));
                    var favorites = await api.SendGraphqlAsync("query { favouriteGames { id name domainName } }", null);
                    checks.Add(Data.Obj("test", "Live favourites access", "passed", Data.Get(favorites, "favouriteGames") != null, "count", Data.List(Data.Get(favorites, "favouriteGames")).Count));
                    var mod = await api.ModAsync("baldursgate3", 5508);
                    checks.Add(Data.Obj("test", "Live mod metadata", "passed", Data.Num(mod, "mod_id") == 5508));
                    var files = await api.FilesAsync("baldursgate3", 5508);
                    checks.Add(Data.Obj("test", "Live file list", "passed", Data.List(Data.Get(files, "files")).Count > 0));
                    var changes = await api.ChangelogsAsync("baldursgate3", 5508);
                    checks.Add(Data.Obj("test", "Live changelogs", "passed", Data.Map(changes).Count > 0));
                    var requirements = await api.RequirementsAsync("baldursgate3", 5508);
                    checks.Add(Data.Obj("test", "Live requirements", "passed", requirements != null));
                    var known = await api.ModAsync("baldursgate3", 3423);
                    var downloadDates = Data.List(await api.BatchViewerDownloadsAsync(new[] { Data.Num(known, "uid") }));
                    checks.Add(Data.Obj("test", "Account download date for a known downloaded mod", "passed", downloadDates.Count == 1 && Data.Timestamp(Data.Get(downloadDates[0], "viewerDownloaded")) > 0));
                }
            } catch (Exception ex) { checks.Add(Data.Obj("test", "Unexpected integration error", "passed", false, "error", ex.Message)); }
            pass = checks.All(x => Data.Bool(x, "passed"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report)));
            File.WriteAllText(report, Data.Json.Serialize(Data.Obj("passed", pass, "checks", checks)));
            return pass ? 0 : 1;
        }
    }
}
