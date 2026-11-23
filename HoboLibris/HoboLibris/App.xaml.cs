using System.Windows;
using HoboLibris.Helpers;
using HoboLibris.Resources;
using HoboLibris.Services;

namespace HoboLibris
{
    public partial class App : Application
    {
        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            LogManager.Info("=== HoboLibris starting ===");
            LogManager.Info($"Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            LogManager.Info($"OS: {Environment.OSVersion}");
            LogManager.Info($".NET: {Environment.Version}");

            // The language must be set before any window is created.
            LanguageManager.Initialize();
            if (LanguageManager.IsRightToLeft)
            {
                // Arabic and Hebrew: every window reads right to left. (Window already has its
                // own FlowDirection metadata, so it can't be overridden; set it as each one loads.)
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler((s, _) => ((Window)s).FlowDirection = FlowDirection.RightToLeft));
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();

            // --screenshot <file.png> [--kindle|--send|--collections]: save a picture of the main
            // window a few seconds after start-up and exit. For checking the look without a hand.
            var args = e.Args;
            var shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                mainWindow.PrepareScreenshot(args[shot + 1], args.Contains("--kindle"), args.Contains("--send"), args.Contains("--collections"));
            }
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            LogManager.Info($"Exiting with code {e.ApplicationExitCode}");
            LogManager.Shutdown();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            if (ex != null)
            {
                LogManager.Fatal(ex, "Unhandled exception");
            }
            else
            {
                LogManager.Fatal("Unhandled exception without details");
            }
            if (e.IsTerminating)
            {
                MessageBox.Show(
                    string.Format(Strings.Error_Critical, ex?.Message),
                    Strings.Error_CriticalTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogManager.Error(e.Exception, "Dispatcher exception");
            MessageBox.Show(
                string.Format(Strings.Error_Unexpected, e.Exception.Message),
                Strings.Common_Error,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
