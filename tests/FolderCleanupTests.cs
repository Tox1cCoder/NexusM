using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace NexusM
{
    internal static class FolderCleanupTests
    {
        private static string root;
        private static readonly List<object> checks = new List<object>();

        private static int Main(string[] args)
        {
            root = Path.Combine(Path.GetTempPath(), "NexusM-FolderCleanupTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "folder-cleanup-tests.json");
            try
            {
                Check("top-level archives and partials move while subfolders and their files remain", TopLevelFiles);
                Check("a plan exposes an immutable snapshot and excludes files added later", PlanSnapshot);
                Check("empty and missing dedicated folders need no creation or recycling", EmptyFolders);
                Check("drive, profile, library, Windows, and application roots are protected", ProtectedRoots);
                Check("relative, network, device, and alternate-stream paths are rejected", InvalidPaths);
                Check("provided application, state, and workspace paths are protected in both directions", ProvidedProtection);
                Check("a changed, missing, or replaced candidate remains untouched", ChangedFiles);
                Check("a replaced folder prevents recycling any replacement files", ChangedFolder);
                Check("folder validation repeats after every moved file", ChangedDuringRecycle);
                Check("a failed file does not prevent other snapshot files from moving", PartialFailure);
                Check("cancellation stops immediately and reports remaining files", Cancellation);
                Check("a recycler that leaves the file behind is not reported successful", NoOpRecycler);
                Check("file links and linked folder ancestors are protected when Windows allows test links", ReparsePoints);
            }
            catch (Exception error) { checks.Add(new { name = "Harness failure", passed = false, error = error.ToString() }); }
            finally
            {
                bool passed = checks.All(IsPassed);
                Directory.CreateDirectory(Path.GetDirectoryName(report));
                File.WriteAllText(report, new JavaScriptSerializer().Serialize(new { passed = passed, checks = checks }), new UTF8Encoding(false));
                string absolute = Path.GetFullPath(root);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\NexusM-FolderCleanupTests-";
                if (!absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path.");
                RemoveFixture(absolute);
            }
            return checks.All(IsPassed) ? 0 : 1;
        }

        private static bool IsPassed(object check) { return (bool)check.GetType().GetProperty("passed").GetValue(check, null); }
        private static void Check(string name, Action test)
        {
            try { test(); checks.Add(new { name = name, passed = true }); }
            catch (Exception error) { checks.Add(new { name = name, passed = false, error = error.Message }); }
        }
        private static void Expect(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static string Folder(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        private static string Put(string folder, string name, string content = "fixture archive") { string path = Path.Combine(folder, name); File.WriteAllText(path, content); return path; }
        private static DownloadFolderPlan Plan(string folder) { return DownloadFolderCleanup.Prepare(folder, new string[0]); }
        private static Action<string> Recycler(string name)
        {
            string destination = Folder(name);
            return delegate(string path) { File.Move(path, Path.Combine(destination, Path.GetFileName(path))); };
        }
        private static void Reject(string folder, params string[] protectedPaths)
        {
            bool rejected = false;
            try { DownloadFolderCleanup.Prepare(folder, protectedPaths); }
            catch (ArgumentException) { rejected = true; }
            Expect(rejected, "Unsafe folder was accepted: " + folder);
        }

        private static void TopLevelFiles()
        {
            string folder = Folder("top-level");
            Put(folder, "main.zip"); Put(folder, "optional.7z"); Put(folder, ".nexusm-game-1-2.part"); Put(folder, ".nexusm-game-1-2.part.json");
            string nested = Folder("top-level\\keep"); string retained = Put(nested, "nested.zip");
            DownloadFolderPlan plan = Plan(folder);
            Expect(plan.Files.Count == 4 && plan.Folder == folder, "The snapshot did not contain the four direct files.");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Recycle(plan, Recycler("recycled-top-level"));
            Expect(result.Moved == 4 && result.Failed == 0 && result.Skipped == 0 && !result.Cancelled, "Incorrect top-level recycle result.");
            Expect(File.Exists(retained) && Directory.Exists(folder), "A subfolder or the download folder itself was removed.");
        }

        private static void PlanSnapshot()
        {
            string folder = Folder("snapshot"); Put(folder, "first.zip");
            DownloadFolderPlan plan = Plan(folder); string later = Put(folder, "later.zip");
            bool immutable = false;
            try { plan.Files.Add(later); } catch (NotSupportedException) { immutable = true; }
            Expect(immutable, "The confirmed file snapshot can be mutated.");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Recycle(plan, Recycler("recycled-snapshot"));
            Expect(result.Moved == 1 && File.Exists(later), "A file added after confirmation was recycled.");
        }

        private static void EmptyFolders()
        {
            string empty = Folder("empty"), missing = Path.Combine(root, "not-created");
            int calls = 0;
            foreach (string folder in new[] { empty, missing })
            {
                DownloadFolderPlan plan = Plan(folder);
                DownloadFolderCleanupResult result = DownloadFolderCleanup.Recycle(plan, delegate { calls++; });
                Expect(plan.Files.Count == 0 && result.Moved == 0 && result.Failed == 0 && result.Skipped == 0, "An empty folder did not produce an empty result.");
            }
            Expect(calls == 0 && !Directory.Exists(missing), "An empty/missing folder was changed.");
        }

        private static void ProtectedRoots()
        {
            Reject(Path.GetPathRoot(root));
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Reject(profile); Reject(Path.Combine(profile, "Downloads")); Reject(Path.Combine(profile, "AppData"));
            foreach (Environment.SpecialFolder special in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
            {
                string path = Environment.GetFolderPath(special);
                if (!String.IsNullOrWhiteSpace(path)) Reject(path);
            }
            Reject(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
            Reject(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexusM-fixture"));
        }

        private static void InvalidPaths()
        {
            foreach (string path in new[] { "", " ", ".", "relative", "C:relative", "\\rooted", "\\\\server\\share\\folder", "\\\\?\\C:\\folder", "\\\\.\\C:\\folder", root + ":stream" }) Reject(path);
        }

        private static void ProvidedProtection()
        {
            string protectedRoot = Folder("protected"), child = Folder("protected\\state"), sibling = Folder("protected-sibling");
            Reject(protectedRoot, child); Reject(child, child); Reject(Path.Combine(child, "nested"), child);
            DownloadFolderPlan plan = DownloadFolderCleanup.Prepare(sibling, new[] { child });
            Expect(plan.Folder == sibling, "A similarly named sibling folder was incorrectly rejected.");
            string normalized = Path.Combine(protectedRoot, "not-used", "..", "state");
            Reject(normalized, child);
        }

        private static void ChangedFiles()
        {
            string folder = Folder("changed-files"); string missing = Put(folder, "missing.zip"), changed = Put(folder, "changed.zip");
            DownloadFolderPlan plan = Plan(folder);
            File.Move(missing, Path.Combine(Folder("outside-changed"), "missing.zip"));
            File.WriteAllText(changed, "a different replacement file that was never confirmed");
            int calls = 0;
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Recycle(plan, delegate { calls++; });
            Expect(calls == 0 && result.Moved == 0 && result.Skipped == 2 && result.Failed == 0 && File.Exists(changed), "Changed files reached the recycler.");
        }

        private static void ChangedFolder()
        {
            string folder = Folder("changed-folder"); Put(folder, "same-name.zip");
            DownloadFolderPlan plan = Plan(folder);
            Directory.Move(folder, Path.Combine(root, "original-folder"));
            Directory.CreateDirectory(folder); string replacement = Put(folder, "same-name.zip");
            Directory.SetCreationTimeUtc(folder, DateTime.UtcNow.AddDays(-2));
            int calls = 0;
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Recycle(plan, delegate { calls++; });
            Expect(calls == 0 && result.Skipped == 1 && File.Exists(replacement), "A replacement directory's file reached the recycler.");
        }

        private static void ChangedDuringRecycle()
        {
            string folder = Folder("changed-during"); Put(folder, "a.zip"); string remaining = Put(folder, "b.zip");
            DownloadFolderPlan plan = Plan(folder); Action<string> recycle = Recycler("recycled-changed-during");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Recycle(plan, delegate(string path) { recycle(path); Directory.SetCreationTimeUtc(folder, DateTime.UtcNow.AddDays(-3)); });
            Expect(result.Moved == 1 && result.Skipped == 1 && File.Exists(remaining), "Folder identity was not revalidated between files.");
        }

        private static void PartialFailure()
        {
            string folder = Folder("partial-failure"); Put(folder, "a.zip"); string failed = Put(folder, "b.zip"); Put(folder, "c.part");
            Action<string> recycle = Recycler("recycled-partial-failure");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Recycle(Plan(folder), delegate(string path) { if (path == failed) throw new IOException("fixture access failure"); recycle(path); });
            Expect(result.Moved == 2 && result.Failed == 1 && result.Skipped == 0 && File.Exists(failed), "A partial failure did not retain its file and continue.");
        }

        private static void Cancellation()
        {
            string folder = Folder("cancellation"); Put(folder, "a.zip"); string cancelled = Put(folder, "b.zip"), untouched = Put(folder, "c.zip");
            Action<string> recycle = Recycler("recycled-cancellation");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Recycle(Plan(folder), delegate(string path) { if (path == cancelled) throw new OperationCanceledException(); recycle(path); });
            Expect(result.Moved == 1 && result.Failed == 0 && result.Skipped == 2 && result.Cancelled && File.Exists(cancelled) && File.Exists(untouched), "Cancellation did not stop subsequent moves.");
        }

        private static void NoOpRecycler()
        {
            string folder = Folder("no-op"); string file = Put(folder, "archive.zip");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Recycle(Plan(folder), delegate { });
            Expect(result.Moved == 0 && result.Failed == 1 && File.Exists(file), "An unchanged file was reported as recycled.");
        }

        private static void ReparsePoints()
        {
            string target = Folder("link-target"); string targetFile = Put(target, "target.zip");
            string folderLink = Path.Combine(root, "folder-link");
            bool directorySupported = CreateSymbolicLink(folderLink, target, 3) || CreateSymbolicLink(folderLink, target, 1);
            if (directorySupported)
            {
                Reject(folderLink); Reject(Path.Combine(folderLink, "nested"));
                string folder = Folder("link-parent"), nestedLink = Path.Combine(folder, "nested-link");
                Expect(CreateSymbolicLink(nestedLink, target, 3) || CreateSymbolicLink(nestedLink, target, 1), "Creating the nested test link failed.");
                Expect(Plan(folder).Files.Count == 0 && File.Exists(targetFile), "A linked subfolder was traversed.");
            }
            string fileFolder = Folder("file-links"), fileLink = Path.Combine(fileFolder, "linked.zip");
            bool fileSupported = CreateSymbolicLink(fileLink, targetFile, 2) || CreateSymbolicLink(fileLink, targetFile, 0);
            if (fileSupported)
            {
                Expect(Plan(fileFolder).Files.Count == 0, "A linked file was included in the snapshot.");
                string ordinary = Put(fileFolder, "ordinary.zip"); DownloadFolderPlan plan = Plan(fileFolder);
                File.Move(ordinary, Path.Combine(target, "ordinary.zip"));
                Expect(CreateSymbolicLink(ordinary, targetFile, 2) || CreateSymbolicLink(ordinary, targetFile, 0), "Creating the replacement test link failed.");
                int calls = 0;
                DownloadFolderCleanupResult result = DownloadFolderCleanup.Recycle(plan, delegate { calls++; });
                Expect(calls == 0 && result.Skipped == 1 && File.Exists(targetFile), "A file replaced with a link reached the recycler.");
            }
            if (!directorySupported || !fileSupported) Console.WriteLine("Windows did not allow all symbolic-link fixtures; supported cases were checked.");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        private static void RemoveFixture(string folder)
        {
            // Explicit fixture root checked above; never recurse into a linked directory.
            foreach (string entry in Directory.GetFileSystemEntries(folder))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0) RemoveFixture(entry);
                    else Directory.Delete(entry, false);
                }
                else File.Delete(entry);
            }
            Directory.Delete(folder, false);
        }
    }
}
