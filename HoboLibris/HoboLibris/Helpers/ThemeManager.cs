using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace HoboLibris.Helpers
{
    /// <summary>Light, dark, or whatever Windows uses for apps.</summary>
    public enum AppTheme
    {
        System,
        Light,
        Dark,
    }

    /// <summary>
    /// Light or dark: swaps the palette (Themes/Light.xaml or Themes/Dark.xaml), which every style
    /// looks up as a DynamicResource, and darkens the title bars. "System" follows the Windows app
    /// mode, also when it changes while HoboLibris is open.
    /// </summary>
    public static class ThemeManager
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        // Windows 10 20H1 and later; earlier Windows 10 builds used 19.
        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmUseImmersiveDarkModeOld = 19;

        private static AppTheme _choice = AppTheme.System;
        private static bool _started;

        /// <summary>Set by the --theme switch: wins over the saved choice for this run only.</summary>
        public static AppTheme? OverrideForRun { get; set; }

        /// <summary>True while the dark palette is in use.</summary>
        public static bool IsDark { get; private set; }

        public static AppTheme Parse(string? name) => name?.Trim().ToLowerInvariant() switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };

        public static string ToSetting(AppTheme theme) => theme.ToString().ToLowerInvariant();

        /// <summary>Apply the saved choice as the app starts, before the main window is shown.</summary>
        public static void Start(AppTheme choice)
        {
            _choice = choice;
            if (!_started)
            {
                _started = true;
                // Every window gets a title bar to match as it loads.
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler((s, _) => SetTitleBar((Window)s)));
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }
            Apply();
        }

        /// <summary>The person chose in Settings; that also ends any --theme override.</summary>
        public static void Choose(AppTheme choice)
        {
            _choice = choice;
            OverrideForRun = null;
            Apply();
        }

        private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            // Switching Windows between light and dark raises the General category.
            if (e.Category == UserPreferenceCategory.General)
            {
                Application.Current?.Dispatcher.BeginInvoke(Apply);
            }
        }

        private static void Apply()
        {
            var theme = OverrideForRun ?? _choice;
            var dark = theme == AppTheme.Dark || (theme == AppTheme.System && WindowsUsesDark());
            if (dark == IsDark)
            {
                return;
            }
            IsDark = dark;
            // The palette is the first merged dictionary in App.xaml.
            var palette = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/HoboLibris;component/Themes/{(dark ? "Dark" : "Light")}.xaml"),
            };
            Application.Current.Resources.MergedDictionaries[0] = palette;
            foreach (Window window in Application.Current.Windows)
            {
                SetTitleBar(window);
            }
            Services.LogManager.Info($"Theme: {(dark ? "dark" : "light")} ({theme})");
        }

        private static bool WindowsUsesDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch (Exception ex)
            {
                Services.LogManager.Debug($"Could not read the Windows app mode: {ex.Message}");
                return false;
            }
        }

        private static void SetTitleBar(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }
            try
            {
                var value = IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeOld, ref value, sizeof(int));
                }
            }
            catch (Exception ex)
            {
                Services.LogManager.Debug($"Could not set the title bar colour: {ex.Message}");
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
