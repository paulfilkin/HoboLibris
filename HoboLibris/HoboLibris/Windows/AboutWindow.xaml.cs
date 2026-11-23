using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using HoboLibris.Resources;
using HoboLibris.Services;

namespace HoboLibris.Windows
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            LoadAboutInfo();
        }

        private void LoadAboutInfo()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = string.Format(Strings.About_Version,
                version?.Major ?? 1, version?.Minor ?? 0, version?.Build ?? 0);
            DataFolderRun.Text = AppPaths.DataDir;
            CopyrightText.Text = string.Format(Strings.About_Copyright, DateTime.Now.Year);
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Strings.About_BrowserError, ex.Message), Strings.Common_Error,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                Process.Start(new ProcessStartInfo { FileName = AppPaths.DataDir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not open the data folder: {ex.Message}");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
