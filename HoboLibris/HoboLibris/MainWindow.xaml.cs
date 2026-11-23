using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HoboLibris.Helpers;
using HoboLibris.Resources;
using HoboLibris.Services;
using HoboLibris.ViewModels;
using HoboLibris.Windows;

namespace HoboLibris
{
    public partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            InitializeComponent();
            ViewModel = new MainViewModel(Dispatcher)
            {
                ShowSignIn = url =>
                {
                    var window = new SignInWindow(url) { Owner = this };
                    return window.ShowDialog() == true ? window.ReturnUrl : null;
                },
                ConfirmDelete = request =>
                {
                    var window = new DeleteBooksWindow(request) { Owner = this };
                    return window.ShowDialog() == true ? (true, window.RemoveFromReader) : (false, false);
                },
                PickFolder = (title, initial) => Dialogs.PickFolder(ActiveOwner(), title, initial),
                ConfirmMoveBackups = count => MessageBox.Show(ActiveOwner(),
                    count == 1 ? Strings.Settings_MoveBackupsOne : string.Format(Strings.Settings_MoveBackupsMany, count),
                    Strings.Settings_MoveBackupsTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,
                ShowError = message => MessageBox.Show(ActiveOwner(), message, Strings.Common_Error,
                    MessageBoxButton.OK, MessageBoxImage.Error),
            };
            DataContext = ViewModel;
            ThemeManager.Start(ViewModel.Theme);
            BuildLanguageMenu();
            Loaded += async (_, _) => await ViewModel.StartAsync();
        }

        /// <summary>Save a picture of this window to a PNG after a few seconds, then close.</summary>
        public void PrepareScreenshot(string path, bool kindle, bool send, bool collections)
        {
            if (kindle)
            {
                ViewModel.Tab = MainTab.Kindle;
            }
            if (send)
            {
                ViewModel.KoboMode = KoboMode.Send;
            }
            if (collections)
            {
                ViewModel.KoboMode = KoboMode.Collections;
            }
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    var root = (FrameworkElement)Content;
                    var bitmap = new RenderTargetBitmap(
                        (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
                        (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                        dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    // The window's own background isn't part of the content tree: paint it first.
                    // A right-to-left window mirrors its content itself, so the picture is mirrored
                    // the same way to show what the person sees.
                    var area = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                    var picture = new DrawingVisual();
                    using (var dc = picture.RenderOpen())
                    {
                        dc.DrawRectangle(Background, null, area);
                        if (FlowDirection == FlowDirection.RightToLeft)
                        {
                            dc.PushTransform(new MatrixTransform(-1, 0, 0, 1, root.ActualWidth, 0));
                        }
                        dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, area);
                    }
                    bitmap.Render(picture);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(path);
                    encoder.Save(file);
                    LogManager.Info($"Screenshot saved to {path}");
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex, "Screenshot failed");
                }
                Close();
            };
            timer.Start();
        }

        /// <summary>The window a dialog should belong to: Settings when it is open, otherwise this one.</summary>
        private Window ActiveOwner() =>
            Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow { Owner = this, DataContext = ViewModel }.ShowDialog();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }

        /// <summary>Fill the language list under the globe icon, each language in its own name.</summary>
        private void BuildLanguageMenu()
        {
            var style = (Style)FindResource("LanguageChoiceItem");
            foreach (var code in LanguageManager.Supported)
            {
                var item = new MenuItem
                {
                    Style = style,
                    Header = LanguageManager.GetLanguageDisplayName(code),
                    Tag = code,
                    IsCheckable = true,
                    // So Chinese and Japanese names use the right font.
                    Language = XmlLanguage.GetLanguage(code),
                };
                item.Click += LanguageChoice_Click;
                LanguageMenu.Items.Add(item);
            }
            MarkCurrentLanguage();
        }

        /// <summary>Tick the language that is in use, or chosen for the next start.</summary>
        private void MarkCurrentLanguage()
        {
            var current = LanguageManager.GetCurrentLanguage();
            foreach (var item in LanguageMenu.Items.OfType<MenuItem>())
            {
                item.IsChecked = item.Tag is string code && code.Equals(current, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void LanguageChoice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string code })
            {
                return;
            }
            if (code.Equals(LanguageManager.GetCurrentLanguage(), StringComparison.OrdinalIgnoreCase))
            {
                // Clicking a checkable item toggles it; keep the tick on the current language.
                MarkCurrentLanguage();
                return;
            }
            LanguageManager.SetLanguage(code);
            MarkCurrentLanguage();
            // The text on screen is read when each window loads, so the new language needs a restart.
            var result = MessageBox.Show(this, Strings.Language_RestartPrompt, Strings.Language_RestartTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                Process.Start(Environment.ProcessPath!);
                Application.Current.Shutdown();
            }
        }
    }
}
