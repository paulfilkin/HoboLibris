using System.IO;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    /// <summary>
    /// The copies of the eReader's book list (KoboReader-*.sqlite, with any journal files) that are
    /// kept before collections are saved or articles removed.
    /// </summary>
    public static class BookListBackups
    {
        private const string Pattern = "KoboReader-*.sqlite*";

        /// <summary>
        /// Copy the eReader's book list (and its journal files, if any) into dir before it is written
        /// to, as KoboReader-&lt;time&gt;.sqlite.  Returns the copy's path.
        /// </summary>
        public static string Take(string db, string dir) => Take(db, dir, DateTime.UtcNow);

        /// <summary>
        /// Take a backup named by the given time.  When a backup of the same second is already there, the
        /// new one gets a number (KoboReader-&lt;time&gt;-2.sqlite, then -3), so neither is overwritten (#64).
        /// </summary>
        public static string Take(string db, string dir, DateTime now)
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotCreateBackupFolder, ex.Message), ex);
            }
            var stamp = Collections.KoboTimestamp(now).Replace(":", "").Replace("-", "");
            var backup = Path.Combine(dir, $"KoboReader-{stamp}.sqlite");
            for (var n = 2; Taken(backup); n++)
            {
                backup = Path.Combine(dir, $"KoboReader-{stamp}-{n}.sqlite");
            }
            try
            {
                File.Copy(db, backup, overwrite: false);
            }
            catch (Exception ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotBackUp, ex.Message), ex);
            }
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var side = db + suffix;
                if (File.Exists(side))
                {
                    try
                    {
                        File.Copy(side, backup + suffix, overwrite: false);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warn($"Could not back up {side}: {ex.Message}");
                    }
                }
            }
            return backup;
        }

        /// <summary>A backup name already in use, by the copy or any of its journal files.</summary>
        private static bool Taken(string backup) =>
            new[] { "", "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(backup + suffix));

        /// <summary>Every backup file in the folder, journal files included.</summary>
        public static List<string> Files(string dir) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, Pattern).ToList() : new List<string>();

        /// <summary>How many backups the folder holds, not counting journal files.</summary>
        public static int Count(string dir) =>
            Files(dir).Count(f => f.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase));

        /// <summary>True when the two paths name the same folder.</summary>
        public static bool SameFolder(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Move every backup file from one folder to another and return how many files moved. A file
        /// whose name is already taken in the new folder stays where it is.
        /// </summary>
        public static int Move(string from, string to)
        {
            if (SameFolder(from, to))
            {
                return 0;
            }
            Directory.CreateDirectory(to);
            var moved = 0;
            foreach (var file in Files(from))
            {
                var target = Path.Combine(to, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    LogManager.Warn($"Not moving {file}: {target} already exists");
                    continue;
                }
                File.Move(file, target);
                moved++;
            }
            return moved;
        }
    }
}
