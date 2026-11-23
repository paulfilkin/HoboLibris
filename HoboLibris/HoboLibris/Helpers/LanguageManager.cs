using System.Globalization;
using System.IO;

namespace HoboLibris.Helpers
{
    /// <summary>
    /// Which language the UI shows. The choice is kept in %APPDATA%\HoboLibris\language.txt.
    /// </summary>
    public static class LanguageManager
    {
        private static readonly string ConfigFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HoboLibris",
            "language.txt");

        public static readonly string[] Supported =
            { "en", "ar", "de", "es", "fr", "he", "it", "ja", "ko", "pt", "uk", "zh-CN" };

        /// <summary>Apply the saved language, or the system language when nothing was saved.</summary>
        public static void Initialize()
        {
            var saved = LoadSavedLanguage();
            if (!string.IsNullOrEmpty(saved))
            {
                SetLanguage(saved, save: false);
                return;
            }
            var system = CultureInfo.CurrentUICulture;
            var code = system.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                ? "zh-CN"
                : system.TwoLetterISOLanguageName;
            SetLanguage(Supported.Contains(code, StringComparer.OrdinalIgnoreCase) ? code : "en", save: false);
        }

        public static void SetLanguage(string languageCode, bool save = true)
        {
            try
            {
                var culture = new CultureInfo(languageCode);
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
                if (save)
                {
                    SaveLanguage(languageCode);
                }
            }
            catch (CultureNotFoundException)
            {
                var fallback = new CultureInfo("en");
                CultureInfo.CurrentUICulture = fallback;
                CultureInfo.CurrentCulture = fallback;
                CultureInfo.DefaultThreadCurrentUICulture = fallback;
                CultureInfo.DefaultThreadCurrentCulture = fallback;
            }
        }

        public static string GetCurrentLanguage()
        {
            var culture = CultureInfo.CurrentUICulture;
            return culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                ? "zh-CN"
                : culture.TwoLetterISOLanguageName;
        }

        /// <summary>True when the language reads right to left (Arabic, Hebrew).</summary>
        public static bool IsRightToLeft => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

        private static void SaveLanguage(string languageCode)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
                File.WriteAllText(ConfigFile, languageCode);
            }
            catch (Exception ex)
            {
                Services.LogManager.Warn($"Could not save the language choice: {ex.Message}");
            }
        }

        private static string? LoadSavedLanguage()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    return File.ReadAllText(ConfigFile).Trim();
                }
            }
            catch (Exception ex)
            {
                Services.LogManager.Warn($"Could not read the language choice: {ex.Message}");
            }
            return null;
        }

        public static string GetLanguageDisplayName(string languageCode)
        {
            return languageCode.ToLowerInvariant() switch
            {
                "en" => "English",
                "ar" => "العربية",
                "de" => "Deutsch",
                "es" => "Español",
                "fr" => "Français",
                "he" => "עברית",
                "it" => "Italiano",
                "ja" => "日本語",
                "ko" => "한국어",
                "pt" => "Português",
                "uk" => "Українська",
                "zh-cn" or "zh" => "简体中文",
                _ => languageCode.ToUpperInvariant()
            };
        }
    }
}
