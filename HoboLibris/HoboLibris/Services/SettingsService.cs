using System.IO;
using System.Text.Json;
using HoboLibris.Models;

namespace HoboLibris.Services
{
    /// <summary>Reads and writes settings.json and library.json in the data folder.</summary>
    public static class SettingsService
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static readonly object Gate = new();

        public static AppSettings Load()
        {
            AppSettings s = new();
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new();
                }
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not read settings.json, starting afresh: {ex.Message}");
            }
            if (string.IsNullOrEmpty(s.DeviceId))
            {
                s.DeviceId = Guid.NewGuid().ToString("N");
            }
            if (string.IsNullOrEmpty(s.DownloadDir))
            {
                s.DownloadDir = AppPaths.DefaultKoboDir;
            }
            if (string.IsNullOrEmpty(s.KindleDir))
            {
                s.KindleDir = AppPaths.DefaultKindleDir;
            }
            if (string.IsNullOrEmpty(s.KoboReaderDir))
            {
                // Kobo eReader books share the Kobo folder; this older setting just mirrors it.
                s.KoboReaderDir = s.DownloadDir;
            }
            return s;
        }

        public static void Save(AppSettings s)
        {
            lock (Gate)
            {
                try
                {
                    File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(s, Options));
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex, "Could not save settings.json");
                }
            }
        }

        public static List<StoreBook> LoadLibrary()
        {
            try
            {
                if (File.Exists(AppPaths.LibraryFile))
                {
                    return JsonSerializer.Deserialize<List<StoreBook>>(File.ReadAllText(AppPaths.LibraryFile), Options) ?? new();
                }
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not read library.json: {ex.Message}");
            }
            return new();
        }

        public static void SaveLibrary(IReadOnlyList<StoreBook> books)
        {
            lock (Gate)
            {
                try
                {
                    File.WriteAllText(AppPaths.LibraryFile, JsonSerializer.Serialize(books, Options));
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex, "Could not save library.json");
                }
            }
        }
    }
}
