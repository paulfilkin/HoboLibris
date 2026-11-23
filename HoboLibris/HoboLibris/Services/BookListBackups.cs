using System.IO;

namespace HoboLibris.Services
{
    /// <summary>
    /// The copies of the eReader's book list (KoboReader-*.sqlite, with any journal files) that are
    /// kept before collections are saved.
    /// </summary>
    public static class BookListBackups
    {
        private const string Pattern = "KoboReader-*.sqlite*";

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
