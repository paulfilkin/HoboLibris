using System.IO;
using NLog;

namespace HoboLibris.Services
{
    /// <summary>
    /// Centralised logging using NLog. Log files go to %APPDATA%\HoboLibris\logs (see NLog.config).
    /// </summary>
    public static class LogManager
    {
        private static readonly Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        static LogManager()
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HoboLibris",
                "logs");
            Directory.CreateDirectory(logDirectory);
        }

        public static void Debug(string message) => Logger.Debug(message);
        public static void Info(string message) => Logger.Info(message);
        public static void Warn(string message) => Logger.Warn(message);
        public static void Error(string message) => Logger.Error(message);
        public static void Error(Exception ex, string message) => Logger.Error(ex, message);
        public static void Fatal(string message) => Logger.Fatal(message);
        public static void Fatal(Exception ex, string message) => Logger.Fatal(ex, message);
        public static void Shutdown() => NLog.LogManager.Shutdown();
    }
}

