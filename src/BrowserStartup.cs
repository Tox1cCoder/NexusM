using System;
using System.IO;
using System.Text;

namespace NexusM
{
    // The host must remain usable when the web interface never becomes ready.
    internal sealed class BrowserStartup
    {
        internal bool Loading { get; private set; }
        internal bool Ready { get; private set; }
        internal string Failure { get; private set; }
        internal int Generation { get; private set; }
        private DateTime started;
        internal int Begin(DateTime now) { Generation++; Navigate(now); return Generation; }
        internal void Navigate(DateTime now) { started = now; Loading = true; Ready = false; Failure = ""; }
        internal bool Complete(int generation) { if (generation != Generation || !Loading) return false; Loading = false; Ready = true; Failure = ""; return true; }
        internal bool Fail(int generation, string reason) { if (generation != Generation) return false; Loading = false; Ready = false; Failure = reason; return true; }
        internal bool TimedOut(DateTime now, TimeSpan timeout) { return Loading && now - started >= timeout; }
    }

    internal static class StartupDiagnostics
    {
        internal static void Record(string root, string code)
        {
            // Only host-defined codes are written: no URLs, exception stacks,
            // API credentials, or library information enter this small log.
            try {
                string path = Path.Combine(root, "startup.log");
                string line = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + " " + code + Environment.NewLine;
                if (File.Exists(path) && new FileInfo(path).Length > 32768) File.WriteAllText(path, line, new UTF8Encoding(false));
                else File.AppendAllText(path, line, new UTF8Encoding(false));
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (System.Security.SecurityException) { }
        }
    }
}
