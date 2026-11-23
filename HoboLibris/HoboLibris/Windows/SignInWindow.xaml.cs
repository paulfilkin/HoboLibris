using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Web.WebView2.Core;
using HoboLibris.Services;

namespace HoboLibris.Windows
{
    /// <summary>
    /// The Kobo sign-in page in a web view. When the page sends the browser to
    /// kobo://UserAuthenticated, the address is kept as ReturnUrl and the window closes.
    /// </summary>
    public partial class SignInWindow : Window
    {
        private readonly string _url;

        /// <summary>The kobo://UserAuthenticated address, once the page sent it.</summary>
        public string? ReturnUrl { get; private set; }

        public SignInWindow(string url)
        {
            _url = url;
            InitializeComponent();
            Loaded += SignInWindow_Loaded;
        }

        private async void SignInWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.SignInBrowserDir);
                var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.SignInBrowserDir);
                await Browser.EnsureCoreWebView2Async(environment);
                Browser.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                Browser.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                Browser.CoreWebView2.Navigate(_url);
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "The sign-in web view could not start");
                Browser.Visibility = Visibility.Collapsed;
                ErrorDetail.Text = ex.Message;
                ErrorPanel.Visibility = Visibility.Visible;
            }
        }

        private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!e.Uri.StartsWith("kobo://", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            e.Cancel = true;
            ReturnUrl = e.Uri;
            LogManager.Info("The Kobo sign-in page returned to the app");
            DialogResult = true;
            Close();
        }

        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Keep pop-ups (such as the password reset page) in this window.
            e.Handled = true;
            Browser.CoreWebView2.Navigate(e.Uri);
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
                LogManager.Warn($"Could not open the browser: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Browser.Dispose();
            base.OnClosed(e);
        }
    }
}
