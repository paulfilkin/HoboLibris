using System.IO;

namespace HoboLibris.Services
{
    /// <summary>
    /// Where HoboLibris keeps its own data (settings, sign-in, library list, book-list backups):
    /// %APPDATA%\HoboLibris. Data left under the app's earlier names is moved over once.
    /// </summary>
    public static class AppPaths
    {
        public const string DataFolderName = "HoboLibris";

        /// <summary>Names the app had before, newest first.</summary>
        private static readonly string[] OldDataFolders = { "KoboLibris", "BookBackup" };

        private static string? _dataDir;

        public static string DataDir => _dataDir ??= ResolveDataDir();

        public static string SettingsFile => Path.Combine(DataDir, "settings.json");
        public static string LibraryFile => Path.Combine(DataDir, "library.json");
        public static string SignInBrowserDir => Path.Combine(DataDir, "signin-browser");
        public static string BookListBackupDir => Path.Combine(DataDir, "Kobo book list backups");

        /// <summary>Where backups go unless the person chooses another folder: Documents\HoboLibris.</summary>
        public static string DefaultBackupRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), DataFolderName);

        public static string DefaultKoboDir => Path.Combine(DefaultBackupRoot, "Kobo");
        public static string DefaultKindleDir => Path.Combine(DefaultBackupRoot, "Kindle");

        private static string ResolveDataDir()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                appData = Path.GetTempPath();
            }
            var dir = Path.Combine(appData, DataFolderName);
            if (!File.Exists(Path.Combine(dir, "settings.json")))
            {
                var old = OldDataFolders
                    .Select(name => Path.Combine(appData, name))
                    .FirstOrDefault(o => File.Exists(Path.Combine(o, "settings.json")));
                if (old != null)
                {
                    MoveOldData(old, dir);
                }
            }
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Rename the old folder (same drive, quick); copy what matters if something holds it open.</summary>
        private static void MoveOldData(string old, string dir)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
                if (!Directory.Exists(dir))
                {
                    Directory.Move(old, dir);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not move the old data folder {old}: {ex.Message}");
            }
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var name in new[] { "settings.json", "library.json" })
                {
                    var from = Path.Combine(old, name);
                    if (File.Exists(from))
                    {
                        File.Copy(from, Path.Combine(dir, name), overwrite: true);
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not copy the old data from {old}: {ex.Message}");
            }
        }
    }
}
