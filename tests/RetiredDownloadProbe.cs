using System.Reflection;

[assembly: AssemblyTitle("Retired NexusM development probe")]
[assembly: AssemblyDescription("Obsolete diagnostic helper; exits immediately without opening a browser")]
[assembly: AssemblyVersion("1.0.0.0")]

// Older development sessions left FreeDownloadProbe.exe in the SDK cache.
// Nothing in NexusM uses it. Replace that obsolete executable in place so an
// old shortcut or command cannot start its browser or unhandled async callback.
internal static class RetiredDownloadProbe
{
    private static int Main(string[] args) { return 0; }
}
