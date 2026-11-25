using System.IO;
using Microsoft.Data.Sqlite;
using HoboLibris.Models;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    /// <summary>
    /// Kobo eReader backup: a Kobo plugged in by USB (after tapping Connect on its screen) is a
    /// drive with a hidden ".kobo" folder. Books the person added themselves are ordinary files
    /// anywhere on the drive; books bought in the Kobo store are protected copies inside
    /// ".kobo\kepub", which the store list already covers, so they are counted but not listed.
    /// Titles and authors come from the eReader's own book list (".kobo\KoboReader.sqlite"),
    /// read without changing it. Files are copied unchanged.
    /// </summary>
    public static class KoboDeviceService
    {
        /// <summary>File types people add to a Kobo themselves (".kepub.epub" is covered by "epub").</summary>
        public static readonly string[] BookExtensions = { "epub", "pdf", "mobi", "cbz", "cbr", "txt", "rtf" };

        public record Reader(string Drive, string Root, string Origin);

        public record ScanResult(List<DeviceBook> Books, int Readers, int StoreOnDevice);

        /// <summary>Connected Kobo eReaders: drives with a ".kobo" folder.</summary>
        public static List<Reader> Drives()
        {
            var found = new List<Reader>();
            for (var letter = 'D'; letter <= 'Z'; letter++)
            {
                var drive = $"{letter}:";
                var root = drive + "\\";
                try
                {
                    if (Directory.Exists(Path.Combine(root, ".kobo")))
                    {
                        found.Add(new Reader(drive, root, string.Format(Strings.Reader_Origin, drive)));
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Debug($"Could not look at drive {drive}: {ex.Message}");
                }
            }
            return found;
        }

        /// <summary>The first connected eReader's root ("E:\"), or null.</summary>
        public static string? FirstRoot() => Drives().FirstOrDefault()?.Root;

        public static string DatabasePath(string root) => Path.Combine(root, ".kobo", "KoboReader.sqlite");

        /// <summary>
        /// Store previews (free samples) in the book list: content rows marked Accessibility 6, with no
        /// user id or entitlement, and a file in ".kobo\kepub" the size of their PreviewFileSize.  The
        /// eReader doesn't list them in My Books (#58; four arrived with a sync on 25 November 2026).
        /// None when the book list has no Accessibility column.
        /// </summary>
        public static HashSet<string> PreviewIds(SqliteConnection conn)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ContentID FROM content WHERE ContentType = 6 AND Accessibility = 6";
                using var rows = cmd.ExecuteReader();
                while (rows.Read())
                {
                    ids.Add(rows.GetString(0));
                }
            }
            catch (SqliteException ex)
            {
                LogManager.Debug($"No Accessibility column, so no previews to leave out: {ex.Message}");
            }
            return ids;
        }

        /// <summary>
        /// The eReader's serial number: the first field of ".kobo\version" ("N418...,4.38...,...").
        /// Null when the file is missing or empty.
        /// </summary>
        public static string? Serial(string root)
        {
            try
            {
                var path = Path.Combine(root, ".kobo", "version");
                if (!File.Exists(path))
                {
                    return null;
                }
                var first = File.ReadAllText(path).Split(',')[0].Trim();
                return first.Length == 0 ? null : first;
            }
            catch (Exception ex)
            {
                LogManager.Debug($"Could not read the eReader's serial number: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// A read-only connection that writes nothing to the eReader (immutable=1).  Not pooled: a
        /// pooled connection keeps the file open after it is closed, and Windows then refuses to
        /// eject the eReader because HoboLibris is using it.
        /// </summary>
        public static SqliteConnection OpenReadOnly(string db)
        {
            var uri = "file:///" + db.Replace('\\', '/') + "?immutable=1";
            var conn = new SqliteConnection($"Data Source={uri};Mode=ReadOnly;Pooling=False");
            conn.Open();
            return conn;
        }

        private record Entry(string Title, string Authors);

        /// <summary>
        /// Book list from KoboReader.sqlite: books added by the person (keyed by their path on the
        /// drive, lower case with "/"), and how many store books have a copy on the eReader.
        /// </summary>
        public static (Dictionary<string, (string Title, string Authors)> Added, int StoreOnDevice) ReadLibrary(string root)
        {
            var added = new Dictionary<string, (string, string)>();
            var storeOnDevice = 0;
            var db = DatabasePath(root);
            if (!File.Exists(db))
            {
                return (added, 0);
            }
            try
            {
                using var conn = OpenReadOnly(db);
                var previews = PreviewIds(conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ContentID, IFNULL(Title, ''), IFNULL(Attribution, '') FROM content WHERE ContentType = 6";
                using var rows = cmd.ExecuteReader();
                var kepub = Path.Combine(root, ".kobo", "kepub");
                while (rows.Read())
                {
                    var id = rows.GetString(0);
                    var title = rows.GetString(1);
                    var authors = rows.GetString(2);
                    const string prefix = "file:///mnt/onboard/";
                    if (id.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        added[id[prefix.Length..].ToLowerInvariant()] = (title, authors);
                    }
                    // A preview's file isn't a store book of the person's (#58).
                    else if (!previews.Contains(id) && File.Exists(Path.Combine(kepub, id)))
                    {
                        storeOnDevice++;
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not read the eReader's book list: {ex.Message}");
            }
            return (added, storeOnDevice);
        }

        private static bool IsBook(string path)
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return BookExtensions.Contains(ext);
        }

        /// <summary>Book files on the drive, skipping hidden and system folders (.kobo, .adds, System Volume Information).</summary>
        public static void Walk(string dir, int depth, List<string> outFiles)
        {
            if (depth > 6)
            {
                return;
            }
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch (Exception ex)
            {
                LogManager.Debug($"Could not list {dir}: {ex.Message}");
                return;
            }
            Array.Sort(entries, StringComparer.Ordinal);
            foreach (var p in entries)
            {
                var name = Path.GetFileName(p);
                if (name.StartsWith('.') || name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (Directory.Exists(p))
                {
                    Walk(p, depth + 1, outFiles);
                }
                else if (IsBook(p))
                {
                    outFiles.Add(p);
                }
            }
        }

        /// <summary>
        /// Books the person added to every connected Kobo eReader. A book whose title matches one
        /// in savedStoreTitles (already downloaded from the store) is marked as a duplicate.
        /// </summary>
        public static ScanResult Scan(IReadOnlyList<string> savedStoreTitles)
        {
            var books = new List<DeviceBook>();
            var storeOnDevice = 0;
            // Copy names already given, lower case, so two books never share one.
            var used = new HashSet<string>();
            var readers = Drives();
            foreach (var reader in readers)
            {
                var (library, store) = ReadLibrary(reader.Root);
                storeOnDevice += store;
                var files = new List<string>();
                Walk(reader.Root, 0, files);
                foreach (var file in files)
                {
                    var rel = Path.GetRelativePath(reader.Root, file);
                    var key = rel.Replace('\\', '/').ToLowerInvariant();
                    var name = Path.GetFileName(file);
                    var lower = name.ToLowerInvariant();
                    var format = lower.EndsWith(".kepub.epub", StringComparison.Ordinal)
                        ? "KEPUB"
                        : Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                    string title, authors;
                    if (library.TryGetValue(key, out var e) && e.Title.Trim().Length > 0)
                    {
                        title = e.Title.Trim();
                        authors = e.Authors.Trim();
                    }
                    else
                    {
                        title = FileNames.StemOf(name).Replace('_', ' ').Trim();
                        authors = "";
                    }
                    var duplicate = savedStoreTitles.Any(t => TitleMatching.SameTitle(t, title))
                        ? Strings.Reader_Duplicate
                        : null;
                    long size = 0;
                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch (Exception ex)
                    {
                        LogManager.Debug($"No size for {file}: {ex.Message}");
                    }
                    var copyName = FileNames.CopyName(title, authors, lower, format, used);
                    books.Add(new DeviceBook
                    {
                        Source = file,
                        Title = title,
                        Authors = authors,
                        Format = format,
                        Origin = reader.Origin,
                        Drive = reader.Drive,
                        Size = size,
                        TargetRel = copyName,
                        Duplicate = duplicate,
                    });
                }
            }
            books.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            return new ScanResult(books, readers.Count, storeOnDevice);
        }

        /// <summary>When no Kobo eReader can be read: what to do, in plain words.</summary>
        public static string ConnectionNote() => Strings.Reader_Note_Connect;
    }
}
