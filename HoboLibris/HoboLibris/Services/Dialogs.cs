using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace HoboLibris.Services
{
    /// <summary>Folder pickers and Explorer, from the UI thread.</summary>
    public static class Dialogs
    {
        /// <summary>The modern folder picker (a path can be typed or pasted). Null when cancelled.</summary>
        public static string? PickFolder(Window owner, string title, string? initial)
        {
            var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
            if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
            {
                dialog.InitialDirectory = initial;
            }
            return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
        }

        public static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not open {dir}: {ex.Message}");
            }
        }
    }
}
