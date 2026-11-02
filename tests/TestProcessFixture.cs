using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

namespace NexusM
{
    // Deliberately simple child process for testing launcher failure handling without WebView2.
    internal static class TestProcessFixture
    {
        private static int Main(string[] args)
        {
            string mode = args[0];
            if (mode == "child") { Thread.Sleep(Timeout.Infinite); return 0; }
            string report = args[1];
            var json = new JavaScriptSerializer();
            if (mode == "missing") return 0;
            if (mode == "malformed") { File.WriteAllText(report, "{invalid report"); return 0; }
            if (mode == "hang")
            {
                var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "child") { UseShellExecute = false, CreateNoWindow = true });
                File.WriteAllText(report, json.Serialize(new { passed = false, running = true, phase = "hung-fixture", currentCheck = "stalled child", processId = Process.GetCurrentProcess().Id, childProcessId = child.Id }));
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }
            bool staleWasRemoved = !File.Exists(report);
            File.WriteAllText(report, json.Serialize(new { passed = false, running = true, phase = "preparing-fixture", currentCheck = "writing fresh report" }));
            Thread.Sleep(350);
            File.WriteAllText(report, json.Serialize(new { passed = mode != "failed", running = mode == "unfinished", phase = "complete", staleWasRemoved = staleWasRemoved, argument = args.Length > 2 ? args[2] : "", checks = new[] { new { name = "Fixture result", passed = true } } }));
            return mode == "nonzero" ? 7 : 0;
        }
    }
}
