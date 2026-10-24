using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace NexusM
{
    internal static class FolderCleanupTests
    {
        private static string root;
        private static readonly List<object> checks = new List<object>();
        private static readonly List<object> ownership = new List<object>();

        private static int Main(string[] args)
        {
            root = Path.Combine(Path.GetTempPath(), "NexusM-FolderCleanupTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "folder-cleanup-tests.json");
            try
            {
                Check("the default cleanup permanently deletes files without adding them to the Recycle Bin", DefaultDeletion);
                Check("only recorded archives and verified app partials are deleted while unrelated files and subfolders remain", TopLevelFiles);
                Check("a name or extension without ownership evidence never authorizes deletion", NoOwnershipEvidence);
                Check("legacy records require an exact path, valid identity, completed status, and matching byte count", LegacyOwnership);
                Check("persisted fingerprints preserve files changed or replaced before cleanup", PersistedFingerprints);
                Check("newer fingerprint evidence cannot be bypassed by an older legacy record", DuplicateOwnership);
                Check("verified resume markers work independently from a cleared download list", ResumeMarkers);
                Check("failed or changed partial files retain their resume marker", FailedPartialKeepsMarker);
                Check("a plan exposes an immutable snapshot and excludes files added later", PlanSnapshot);
                Check("empty and missing dedicated folders need no creation or deletion", EmptyFolders);
                Check("drive, profile, library, Windows, and application roots are protected", ProtectedRoots);
                Check("relative, network, device, and alternate-stream paths are rejected", InvalidPaths);
                Check("provided application, state, and workspace paths are protected in both directions", ProvidedProtection);
                Check("a changed, missing, or replaced candidate remains untouched", ChangedFiles);
                Check("a replaced folder prevents deleting any replacement files", ChangedFolder);
                Check("folder validation repeats after every deleted file", ChangedDuringDeletion);
                Check("a failed file does not prevent other snapshot files from being deleted", PartialFailure);
                Check("cancellation stops immediately and reports remaining files", Cancellation);
                Check("a delete action that leaves the file behind is not reported successful", NoOpDeletion);
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
        private static string Put(string folder, string name, string content = "fixture archive", bool owned = true) {
            string path = Path.Combine(folder, name); File.WriteAllText(path, content);
            if (owned) ownership.Add(Record(path));
            return path;
        }
        private static Dictionary<string, object> Record(string path, bool fingerprint = true) {
            var file = new FileInfo(path);
            var result = new Dictionary<string, object> { { "id", "game:1:2" }, { "gameDomain", "game" }, { "modId", 1L }, { "fileId", 2L },
                { "status", "completed" }, { "path", path }, { "downloadedBytes", file.Length }, { "downloadedAt", 1000L } };
            if (fingerprint) { result["fileLength"] = file.Length; result["fileCreatedUtcTicks"] = file.CreationTimeUtc.Ticks; result["fileModifiedUtcTicks"] = file.LastWriteTimeUtc.Ticks; }
            return result;
        }
        private static DownloadFolderPlan Plan(string folder) { return DownloadFolderCleanup.Prepare(folder, new string[0], ownership); }
        private static Action<string> Deleter()
        {
            return File.Delete;
        }
        private static void Reject(string folder, params string[] protectedPaths)
        {
            bool rejected = false;
            try { DownloadFolderCleanup.Prepare(folder, protectedPaths, ownership); }
            catch (ArgumentException) { rejected = true; }
            Expect(rejected, "Unsafe folder was accepted: " + folder);
        }

        private static void TopLevelFiles()
        {
            string folder = Folder("top-level");
            Put(folder, "main.zip"); Put(folder, "optional.7z");
            Put(folder, ".nexusm-game-1-2.part", "fixture partial", false);
            Put(folder, ".nexusm-game-1-2.part.json", "{\"id\":\"game:1:2\",\"total\":100,\"etag\":\"\",\"lastModified\":\"\"}", false);
            string unrelated = Put(folder, "manual.zip", "not a NexusM download", false), notes = Put(folder, "personal.txt", "keep notes", false);
            string nested = Folder("top-level\\keep"); string retained = Put(nested, "nested.zip");
            DownloadFolderPlan plan = Plan(folder);
            Expect(plan.Files.Count == 4 && plan.Folder == folder, "The snapshot did not contain the four direct files.");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(plan, Deleter());
            Expect(result.Deleted == 4 && result.Failed == 0 && result.Skipped == 0 && !result.Cancelled, "Incorrect top-level delete result.");
            Expect(File.Exists(retained) && Directory.Exists(folder) && File.Exists(unrelated) && File.Exists(notes), "An unrelated file, subfolder, or the download folder itself was removed.");
        }

        private static void NoOwnershipEvidence()
        {
            string folder = Folder("no-evidence");
            Put(folder, "manual.zip", "keep", false); Put(folder, "manual.7z", "keep", false);
            Put(folder, ".nexusm-game-1-2.part", "a reserved name is not proof", false);
            Put(folder, "manual.zip.nexusm.json", "{}", false);
            DownloadFolderPlan plan = DownloadFolderCleanup.Prepare(folder, new string[0], new object[0]);
            Expect(plan.Files.Count == 0, "An extension or reserved-looking filename was treated as app ownership.");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(plan);
            Expect(result.Deleted == 0 && Directory.GetFiles(folder).Length == 4, "Unrecorded files were deleted.");
        }

        private static void LegacyOwnership()
        {
            string folder = Folder("legacy-ownership");
            string known = Put(folder, "known.zip", "known bytes", false);
            var record = Record(known, false);
            Expect(DownloadFolderCleanup.Prepare(folder, new string[0], new object[] { record }).Files.SequenceEqual(new[] { known }), "A valid legacy completed record was not recognized.");
            foreach (var mutate in new Action<Dictionary<string, object>>[] {
                r => r["downloadedBytes"] = 0L, r => r["downloadedBytes"] = 1000L, r => r.Remove("downloadedBytes"),
                r => r["status"] = "failed", r => r["id"] = "game:1:99", r => r.Remove("id"),
                r => r["gameDomain"] = "../bad", r => r["modId"] = -1L, r => r["fileId"] = 0L,
                r => { r["path"] = ""; r["filename"] = "known.zip"; }, r => r["path"] = "known.zip"
            }) {
                var invalid = Record(known, false); mutate(invalid);
                Expect(DownloadFolderCleanup.Prepare(folder, new string[0], new object[] { invalid }).Files.Count == 0, "Incomplete or invalid legacy ownership was accepted.");
            }
            string other = Put(Folder("legacy-other-folder"), "known.zip", "known bytes", false);
            var elsewhere = Record(other, false); elsewhere["filename"] = "known.zip";
            Expect(DownloadFolderCleanup.Prepare(folder, new string[0], new object[] { elsewhere }).Files.Count == 0, "An old folder's basename was used to claim a current-folder file.");
            string child = Folder("legacy-ownership\\child"), nested = Put(child, "nested.zip", "known bytes", false);
            Expect(DownloadFolderCleanup.Prepare(folder, new string[0], new object[] { Record(nested, false) }).Files.Count == 0, "A recorded subfolder file entered direct cleanup.");
        }

        private static void PersistedFingerprints()
        {
            string folder = Folder("persisted-fingerprints"), file = Put(folder, "changed-before.zip", "same size", false);
            var record = Record(file);
            File.WriteAllText(file, "new bytes"); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
            Expect(DownloadFolderCleanup.Prepare(folder, new string[0], new object[] { record }).Files.Count == 0, "A same-size file changed before cleanup was still claimed by its old fingerprint.");
            foreach (var mutate in new Action<Dictionary<string, object>>[] {
                r => r.Remove("fileModifiedUtcTicks"), r => r["fileLength"] = "bad", r => r["fileCreatedUtcTicks"] = 0L, r => r["fileModifiedUtcTicks"] = 1L
            }) {
                var invalid = Record(file); mutate(invalid);
                Expect(DownloadFolderCleanup.Prepare(folder, new string[0], new object[] { invalid }).Files.Count == 0, "Invalid fingerprint evidence fell back to the legacy byte count.");
            }
            string empty = Put(folder, "empty.zip", "", false);
            Expect(DownloadFolderCleanup.Prepare(folder, new string[0], new object[] { Record(empty) }).Files.Count == 1, "A fingerprinted empty app file was not recognized.");
            Expect(DownloadFolderCleanup.Prepare(folder, new string[0], new object[] { Record(empty, false) }).Files.Count == 0, "A zero-byte legacy record was accepted without stronger ownership evidence.");
        }

        private static void DuplicateOwnership()
        {
            string folder = Folder("duplicate-ownership"), file = Put(folder, "same-path.zip", "same size", false);
            var fingerprint = Record(file); var legacy = Record(file, false); legacy["downloadedAt"] = 500L;
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
            foreach (object[] records in new[] { new object[] { legacy, fingerprint }, new object[] { fingerprint, legacy } })
                Expect(DownloadFolderCleanup.Prepare(folder, new string[0], records).Files.Count == 0, "An older legacy record bypassed a mismatched fingerprint.");
            legacy["downloadedAt"] = 1000L;
            foreach (object[] records in new[] { new object[] { legacy, fingerprint }, new object[] { fingerprint, legacy } })
                Expect(DownloadFolderCleanup.Prepare(folder, new string[0], records).Files.Count == 0, "A same-date legacy record bypassed fingerprint evidence.");
            var current = Record(file); current["downloadedAt"] = 1500L;
            Expect(DownloadFolderCleanup.Prepare(folder, new string[0], new object[] { current, legacy, current }).Files.Count == 1, "Duplicate valid records did not collapse to one confirmed path.");
        }

        private static void ResumeMarkers()
        {
            string folder = Folder("resume-markers");
            string partial = Put(folder, ".nexusm-game-1-2.part", "partial", false);
            string marker = Put(folder, ".nexusm-game-1-2.part.json", "{\"id\":\"game:1:2\",\"etag\":\"\\\"revision\\\"\",\"lastModified\":\"\",\"total\":100}", false);
            string orphan = Put(folder, ".nexusm-game-1-3.part.json", "{\"id\":\"game:1:3\",\"etag\":\"\",\"lastModified\":\"\",\"total\":100}", false);
            Put(folder, ".nexusm-game-1-4.part", "unproven", false);
            Put(folder, ".nexusm-game-1-4.part.json", "{\"id\":\"game:1:99\",\"etag\":\"\",\"lastModified\":\"\",\"total\":100}", false);
            Put(folder, ".nexusm-game-1-5.part.json", "{\"id\":\"game:1:5\"}", false);
            Put(folder, ".nexusm-game-1-6.part.json", "{\"__type\":\"not-a-resume-record\"}", false);
            Put(folder, ".nexusm-game-1-7.part.json", "not JSON", false);
            Put(folder, ".nexusm-game-1-8.part.json", new string('x', 65537), false);
            Put(folder, ".nexusm-game-1-0.part.json", "{\"id\":\"game:1:0\",\"etag\":\"\",\"lastModified\":\"\",\"total\":100}", false);
            DownloadFolderPlan plan = DownloadFolderCleanup.Prepare(folder, new string[0], new object[0]);
            Expect(plan.Files.OrderBy(x => x).SequenceEqual(new[] { partial, marker, orphan }.OrderBy(x => x)), "Resume markers did not select exactly the verified app pair and orphan sidecar.");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(plan);
            Expect(result.Deleted == 3 && !File.Exists(partial) && !File.Exists(marker) && !File.Exists(orphan) && Directory.GetFiles(folder).Length == 7, "Resume cleanup deleted an unrelated or malformed marker.");
        }

        private static void FailedPartialKeepsMarker()
        {
            string folder = Folder("failed-partial");
            string partial = Put(folder, ".nexusm-game-1-2.part", "partial", false);
            string marker = Put(folder, ".nexusm-game-1-2.part.json", "{\"id\":\"game:1:2\",\"etag\":\"\",\"lastModified\":\"\",\"total\":100}", false);
            DownloadFolderPlan plan = DownloadFolderCleanup.Prepare(folder, new string[0], new object[0]);
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(plan, delegate(string path) { if (path == partial) throw new IOException("Locked fixture partial"); File.Delete(path); });
            Expect(result.Failed == 1 && result.Skipped == 1 && File.Exists(partial) && File.Exists(marker), "A failed partial lost its resume marker.");
            plan = DownloadFolderCleanup.Prepare(folder, new string[0], new object[0]);
            File.WriteAllText(partial, "replacement partial bytes");
            result = DownloadFolderCleanup.Delete(plan);
            Expect(result.Deleted == 0 && result.Skipped == 2 && File.Exists(marker), "A changed partial lost its resume marker.");
        }

        private static List<string> RecycledFixtureFiles(string folder)
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            object bin = null, items = null;
            var paths = new List<string>();
            try
            {
                bin = shell.GetType().InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { 10 });
                if (bin == null) throw new InvalidOperationException("Recycle Bin enumeration is required for this fixture.");
                items = bin.GetType().InvokeMember("Items", BindingFlags.InvokeMethod, null, bin, null);
                int count = Convert.ToInt32(items.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, items, null));
                for (int index = 0; index < count; index++)
                {
                    object item = items.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, items, new object[] { index });
                    try
                    {
                        string original = Convert.ToString(item.GetType().InvokeMember("ExtendedProperty", BindingFlags.InvokeMethod, null, item, new object[] { "System.Recycle.DeletedFrom" }));
                        if (String.Equals(original, folder, StringComparison.OrdinalIgnoreCase))
                            paths.Add(Convert.ToString(item.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, item, null)));
                    }
                    finally { Marshal.FinalReleaseComObject(item); }
                }
            }
            finally
            {
                if (items != null) Marshal.FinalReleaseComObject(items);
                if (bin != null) Marshal.FinalReleaseComObject(bin);
                Marshal.FinalReleaseComObject(shell);
            }
            return paths;
        }

        private static void DefaultDeletion()
        {
            string folder = Folder("default-delete");
            string archive = Put(folder, "permanent-fixture.zip");
            try
            {
                DownloadFolderCleanup.Delete(Plan(folder));
                Expect(!File.Exists(archive), "Default cleanup left the file in the download folder.");
                Expect(RecycledFixtureFiles(folder).Count == 0, "Default cleanup moved the file to the Recycle Bin instead of permanently deleting it.");
            }
            finally
            {
                // Remove only this exact fixture's recycled files if the regression fails.
                string recycleRoot = Path.Combine(Path.GetPathRoot(folder), "$Recycle.Bin") + Path.DirectorySeparatorChar;
                foreach (string path in RecycledFixtureFiles(folder))
                {
                    string absolute = Path.GetFullPath(path), name = Path.GetFileName(path);
                    if (!absolute.StartsWith(recycleRoot, StringComparison.OrdinalIgnoreCase) || !name.StartsWith("$R", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unsafe recycled fixture cleanup path.");
                    File.Delete(absolute);
                    File.Delete(Path.Combine(Path.GetDirectoryName(absolute), "$I" + name.Substring(2)));
                }
            }
        }

        private static void PlanSnapshot()
        {
            string folder = Folder("snapshot"); Put(folder, "first.zip");
            DownloadFolderPlan plan = Plan(folder); string later = Put(folder, "later.zip");
            bool immutable = false;
            try { plan.Files.Add(later); } catch (NotSupportedException) { immutable = true; }
            Expect(immutable, "The confirmed file snapshot can be mutated.");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(plan, Deleter());
            Expect(result.Deleted == 1 && File.Exists(later), "A file added after confirmation was deleted.");
        }

        private static void EmptyFolders()
        {
            string empty = Folder("empty"), missing = Path.Combine(root, "not-created");
            int calls = 0;
            foreach (string folder in new[] { empty, missing })
            {
                DownloadFolderPlan plan = Plan(folder);
                DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(plan, delegate { calls++; });
                Expect(plan.Files.Count == 0 && result.Deleted == 0 && result.Failed == 0 && result.Skipped == 0, "An empty folder did not produce an empty result.");
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
            DownloadFolderPlan plan = DownloadFolderCleanup.Prepare(sibling, new[] { child }, ownership);
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
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(plan, delegate { calls++; });
            Expect(calls == 0 && result.Deleted == 0 && result.Skipped == 2 && result.Failed == 0 && File.Exists(changed), "Changed files reached deletion.");
        }

        private static void ChangedFolder()
        {
            string folder = Folder("changed-folder"); Put(folder, "same-name.zip");
            DownloadFolderPlan plan = Plan(folder);
            Directory.Move(folder, Path.Combine(root, "original-folder"));
            Directory.CreateDirectory(folder); string replacement = Put(folder, "same-name.zip");
            Directory.SetCreationTimeUtc(folder, DateTime.UtcNow.AddDays(-2));
            int calls = 0;
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(plan, delegate { calls++; });
            Expect(calls == 0 && result.Skipped == 1 && File.Exists(replacement), "A replacement directory's file reached deletion.");
        }

        private static void ChangedDuringDeletion()
        {
            string folder = Folder("changed-during"); Put(folder, "a.zip"); string remaining = Put(folder, "b.zip");
            DownloadFolderPlan plan = Plan(folder); Action<string> delete = Deleter();
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(plan, delegate(string path) { delete(path); Directory.SetCreationTimeUtc(folder, DateTime.UtcNow.AddDays(-3)); });
            Expect(result.Deleted == 1 && result.Skipped == 1 && File.Exists(remaining), "Folder identity was not revalidated between files.");
        }

        private static void PartialFailure()
        {
            string folder = Folder("partial-failure"); Put(folder, "a.zip"); string failed = Put(folder, "b.zip"); Put(folder, "c.part");
            Action<string> delete = Deleter();
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(Plan(folder), delegate(string path) { if (path == failed) throw new IOException("fixture access failure"); delete(path); });
            Expect(result.Deleted == 2 && result.Failed == 1 && result.Skipped == 0 && File.Exists(failed), "A partial failure did not retain its file and continue.");
        }

        private static void Cancellation()
        {
            string folder = Folder("cancellation"); Put(folder, "a.zip"); string cancelled = Put(folder, "b.zip"), untouched = Put(folder, "c.zip");
            Action<string> delete = Deleter();
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(Plan(folder), delegate(string path) { if (path == cancelled) throw new OperationCanceledException(); delete(path); });
            Expect(result.Deleted == 1 && result.Failed == 0 && result.Skipped == 2 && result.Cancelled && File.Exists(cancelled) && File.Exists(untouched), "Cancellation did not stop subsequent deletes.");
        }

        private static void NoOpDeletion()
        {
            string folder = Folder("no-op"); string file = Put(folder, "archive.zip");
            DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(Plan(folder), delegate { });
            Expect(result.Deleted == 0 && result.Failed == 1 && File.Exists(file), "An unchanged file was reported as deleted.");
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
                DownloadFolderCleanupResult result = DownloadFolderCleanup.Delete(plan, delegate { calls++; });
                Expect(calls == 0 && result.Skipped == 1 && File.Exists(targetFile), "A file replaced with a link reached deletion.");
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
