using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NexusM
{
    internal sealed class DownloadFolderPlan
    {
        internal string Folder { get; private set; }
        internal IList<string> Files { get; private set; }
        internal readonly string[] ProtectedPaths;
        internal readonly long FolderCreated;
        internal readonly Dictionary<string, DownloadFileSnapshot> Snapshots;
        internal readonly Dictionary<string, string> SidecarParts;

        internal DownloadFolderPlan(string folder, string[] protectedPaths, long created, Dictionary<string, DownloadFileSnapshot> snapshots, Dictionary<string, string> sidecarParts)
        {
            Folder = folder;
            ProtectedPaths = protectedPaths;
            FolderCreated = created;
            Snapshots = snapshots;
            SidecarParts = sidecarParts;
            Files = new ReadOnlyCollection<string>(snapshots.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    internal sealed class DownloadFolderCleanupResult
    {
        internal int Deleted { get; set; }
        internal int Failed { get; set; }
        internal int Skipped { get; set; }
        internal bool Cancelled { get; set; }
    }

    internal sealed class DownloadFileSnapshot
    {
        internal readonly long Length, Created, Modified;
        internal DownloadFileSnapshot(FileInfo file) { Length = file.Length; Created = file.CreationTimeUtc.Ticks; Modified = file.LastWriteTimeUtc.Ticks; }
        internal bool Matches(FileInfo file) { return Length == file.Length && Created == file.CreationTimeUtc.Ticks && Modified == file.LastWriteTimeUtc.Ticks; }
    }

    internal static class DownloadFolderCleanup
    {
        internal static DownloadFolderPlan Prepare(string folder, IEnumerable<string> protectedPaths, IEnumerable<object> ownedFiles)
        {
            string absolute = CanonicalFolder(folder);
            string[] protectedFolders = (protectedPaths ?? Enumerable.Empty<string>()).Where(path => !String.IsNullOrWhiteSpace(path)).Select(CanonicalFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            ValidateFolder(absolute, protectedFolders);
            var snapshots = new Dictionary<string, DownloadFileSnapshot>(StringComparer.OrdinalIgnoreCase);
            var sidecarParts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long created = 0;
            if (Directory.Exists(absolute))
            {
                created = new DirectoryInfo(absolute).CreationTimeUtc.Ticks;
                var records = new Dictionary<string, IDictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in ownedFiles ?? Enumerable.Empty<object>())
                {
                    var record = value as IDictionary<string, object>;
                    string path = Text(record, "path");
                    if (!ValidCompletedRecord(record) || !DirectPath(path, absolute)) continue;
                    path = Path.GetFullPath(path);
                    IDictionary<string, object> previous;
                    if (!records.TryGetValue(path, out previous) || NewerOwnership(record, previous)) records[path] = record;
                }
                foreach (var candidate in records)
                {
                    try {
                        var file = new FileInfo(candidate.Key);
                        if (RegularFile(file) && OwnedSnapshot(candidate.Value, file)) snapshots[file.FullName] = new DownloadFileSnapshot(file);
                    } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (System.Security.SecurityException) { }
                }
                // A verified resume marker survives clearing the visible queue. The
                // reserved filename alone is deliberately insufficient ownership proof.
                foreach (string path in Directory.GetFiles(absolute, ".nexusm-*.part.json", System.IO.SearchOption.TopDirectoryOnly))
                {
                    try {
                        var marker = new FileInfo(path);
                        if (!RegularFile(marker)) continue;
                        var before = new DownloadFileSnapshot(marker);
                        if (!ValidResumeMarker(marker)) continue;
                        marker.Refresh();
                        if (!RegularFile(marker) || !before.Matches(marker)) continue;
                        string partialPath = path.Substring(0, path.Length - ".json".Length);
                        var partial = new FileInfo(partialPath);
                        if (Directory.Exists(partialPath) || (partial.Exists && !RegularFile(partial))) continue;
                        if (RegularFile(partial)) snapshots[partial.FullName] = new DownloadFileSnapshot(partial);
                        snapshots[marker.FullName] = before;
                        sidecarParts[marker.FullName] = partialPath;
                    } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (System.Security.SecurityException) { }
                }
            }
            return new DownloadFolderPlan(absolute, protectedFolders, created, snapshots, sidecarParts);
        }

        private static string Text(IDictionary<string, object> record, string key) {
            object value; return record != null && record.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }
        private static bool ValidCompletedRecord(IDictionary<string, object> record) {
            long mod, file;
            string domain = Text(record, "gameDomain");
            return Text(record, "status") == "completed" && Regex.IsMatch(domain, "^[a-zA-Z0-9_-]{1,100}$") &&
                Int64.TryParse(Text(record, "modId"), out mod) && mod > 0 && Int64.TryParse(Text(record, "fileId"), out file) && file > 0 &&
                String.Equals(Text(record, "id"), domain + ":" + mod.ToString(CultureInfo.InvariantCulture) + ":" + file.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }
        private static bool DirectPath(string path, string folder) {
            if (String.IsNullOrWhiteSpace(path) || path.Length < 3 || !Char.IsLetter(path[0]) || path[1] != ':' ||
                (path[2] != '\\' && path[2] != '/') || path.Substring(2).Contains(':')) return false;
            try { return String.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), folder, StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; } catch (NotSupportedException) { return false; } catch (PathTooLongException) { return false; }
        }
        private static bool RegularFile(FileInfo file) {
            return file.Exists && (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
        }
        private static bool OwnedSnapshot(IDictionary<string, object> record, FileInfo file) {
            string[] fields = { "fileLength", "fileCreatedUtcTicks", "fileModifiedUtcTicks" };
            if (fields.Any(record.ContainsKey)) {
                long length, created, modified;
                return fields.All(record.ContainsKey) && Int64.TryParse(Text(record, fields[0]), out length) &&
                    Int64.TryParse(Text(record, fields[1]), out created) && Int64.TryParse(Text(record, fields[2]), out modified) &&
                    length >= 0 && created > 0 && modified > 0 && file.Length == length && file.CreationTimeUtc.Ticks == created && file.LastWriteTimeUtc.Ticks == modified;
            }
            long downloaded;
            return Int64.TryParse(Text(record, "downloadedBytes"), out downloaded) && downloaded > 0 && downloaded == file.Length;
        }
        private static bool NewerOwnership(IDictionary<string, object> record, IDictionary<string, object> previous) {
            long current, old;
            Int64.TryParse(Text(record, "downloadedAt"), out current); Int64.TryParse(Text(previous, "downloadedAt"), out old);
            bool fingerprint = record.ContainsKey("fileLength") || record.ContainsKey("fileCreatedUtcTicks") || record.ContainsKey("fileModifiedUtcTicks");
            bool oldFingerprint = previous.ContainsKey("fileLength") || previous.ContainsKey("fileCreatedUtcTicks") || previous.ContainsKey("fileModifiedUtcTicks");
            return current > old || (current == old && fingerprint && !oldFingerprint);
        }
        private static bool ValidResumeMarker(FileInfo file) {
            Match name = Regex.Match(file.Name, @"^\.nexusm-([a-zA-Z0-9_-]{1,100})-([1-9][0-9]*)-([1-9][0-9]*)\.part\.json$", RegexOptions.IgnoreCase);
            long mod, id;
            if (!name.Success || !Int64.TryParse(name.Groups[2].Value, out mod) || !Int64.TryParse(name.Groups[3].Value, out id)) return false;
            try {
                using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    if (input.Length > 65536) return false;
                    using (var reader = new StreamReader(input, Encoding.UTF8)) {
                        var record = new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 8 }.DeserializeObject(reader.ReadToEnd()) as IDictionary<string, object>;
                        object etag, modified; long total;
                        string expected = name.Groups[1].Value + ":" + mod.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString(CultureInfo.InvariantCulture);
                        return record != null && String.Equals(Text(record, "id"), expected, StringComparison.OrdinalIgnoreCase) &&
                            record.TryGetValue("etag", out etag) && etag is string && record.TryGetValue("lastModified", out modified) && modified is string &&
                            Int64.TryParse(Text(record, "total"), out total) && total >= 0;
                    }
                }
            } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } catch (MissingMethodException) { return false; }
        }

        internal static DownloadFolderCleanupResult Delete(DownloadFolderPlan plan, Action<string> delete = null)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            delete = delete ?? File.Delete;
            var result = new DownloadFolderCleanupResult();
            for (int index = 0; index < plan.Files.Count; index++)
            {
                try
                {
                    ValidateFolder(plan.Folder, plan.ProtectedPaths);
                    if (!Directory.Exists(plan.Folder) || new DirectoryInfo(plan.Folder).CreationTimeUtc.Ticks != plan.FolderCreated)
                    {
                        result.Skipped += plan.Files.Count - index;
                        break;
                    }
                    string path = plan.Files[index];
                    string partialPath;
                    if (plan.SidecarParts.TryGetValue(path, out partialPath) && (File.Exists(partialPath) || Directory.Exists(partialPath))) {
                        result.Skipped++;
                        continue;
                    }
                    var file = new FileInfo(path);
                    if (!String.Equals(Path.GetDirectoryName(file.FullName), plan.Folder, StringComparison.OrdinalIgnoreCase) || !file.Exists ||
                        (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || !plan.Snapshots[path].Matches(file))
                    {
                        result.Skipped++;
                        continue;
                    }
                    delete(path);
                    if (!File.Exists(path)) result.Deleted++;
                    else result.Failed++;
                }
                catch (OperationCanceledException)
                {
                    result.Cancelled = true;
                    result.Skipped += plan.Files.Count - index;
                    break;
                }
                catch (IOException) { result.Failed++; }
                catch (UnauthorizedAccessException) { result.Failed++; }
                catch (ArgumentException) { result.Failed++; }
                catch (InvalidOperationException) { result.Failed++; }
                catch (System.Security.SecurityException) { result.Failed++; }
            }
            return result;
        }

        private static string CanonicalFolder(string folder)
        {
            if (String.IsNullOrWhiteSpace(folder) || folder.Length < 3 || !Char.IsLetter(folder[0]) || folder[1] != ':' || (folder[2] != '\\' && folder[2] != '/') || folder.Substring(2).Contains(':'))
                throw new ArgumentException("Choose a dedicated download folder on a local fixed drive.");
            string absolute = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetPathRoot(absolute);
            if (absolute.Length == 2) absolute += Path.DirectorySeparatorChar;
            if (String.Equals(absolute.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || new DriveInfo(root).DriveType != DriveType.Fixed)
                throw new ArgumentException("Choose a dedicated download folder on a local fixed drive, rather than the drive root.");
            return absolute;
        }

        private static void ValidateFolder(string folder, IEnumerable<string> protectedPaths)
        {
            CanonicalFolder(folder);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var broadFolders = new List<string> { profile, Path.Combine(profile, "Downloads"), Path.Combine(profile, "AppData"), Environment.GetEnvironmentVariable("PUBLIC") };
            foreach (Environment.SpecialFolder special in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.CommonMusic, Environment.SpecialFolder.CommonPictures, Environment.SpecialFolder.CommonVideos })
                broadFolders.Add(Environment.GetFolderPath(special));
            foreach (string broad in broadFolders.Where(path => !String.IsNullOrWhiteSpace(path)))
                if (Same(folder, broad)) throw new ArgumentException("Choose a dedicated subfolder instead of a personal or shared library folder.");
            foreach (Environment.SpecialFolder special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
            {
                string system = Environment.GetFolderPath(special);
                if (!String.IsNullOrWhiteSpace(system) && Within(folder, system)) throw new ArgumentException("Windows and application folders cannot be cleared.");
            }
            foreach (string protectedPath in protectedPaths)
                if (Within(protectedPath, folder) || Within(folder, protectedPath)) throw new ArgumentException("Choose a download folder outside NexusM's application, data, and project folders.");
            string current = folder;
            while (!String.IsNullOrEmpty(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { current = Path.GetDirectoryName(current); continue; }
                catch (DirectoryNotFoundException) { current = Path.GetDirectoryName(current); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked folders cannot be cleared. Choose a physical download folder.");
                if ((attributes & FileAttributes.Directory) == 0) throw new ArgumentException("The download location must be a folder.");
                current = Path.GetDirectoryName(current);
            }
        }

        private static bool Same(string left, string right)
        {
            return String.Equals(left.TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static bool Within(string child, string parent)
        {
            string normalizedParent = Path.GetFullPath(parent).TrimEnd('\\', '/');
            return Same(child, normalizedParent) || child.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
