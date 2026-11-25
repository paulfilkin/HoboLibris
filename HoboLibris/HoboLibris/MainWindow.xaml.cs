using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HoboLibris.Assistant;
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
            ViewModel.Articles.ConfirmRemove = rows => MessageBox.Show(ActiveOwner(), ArticlesViewModel.RemovePrompt(rows),
                Strings.Art_RemoveTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            ViewModel.Previews.ConfirmWishRemove = rows => MessageBox.Show(ActiveOwner(), PreviewsViewModel.RemoveWishPrompt(rows),
                Strings.Wish_RemoveTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            // A picture for checking the look keeps no conversation and leaves the saved one alone.
            var screenshot = Environment.GetCommandLineArgs().Contains("--screenshot");
            ViewModel.Assistant = new AssistantViewModel(ViewModel, screenshot ? null : ConversationStore.DefaultPath)
            {
                ConfirmClear = () => MessageBox.Show(this, Strings.Asst_ClearPrompt, Strings.Asst_Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,
                CopyText = text =>
                {
                    try
                    {
                        Clipboard.SetText(text);
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        // The clipboard is busy; the person can try again.
                    }
                },
            };
            ViewModel.Assistant.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AssistantViewModel.PaneVisible) or nameof(AssistantViewModel.PaneFolded))
                {
                    LayOutPane();
                }
            };
            _paneFold = new FoldColumn(PaneColumn, PaneSplitter, Pane, PaneStrip, PaneMinWidth, 900,
                ViewModel.Assistant.PaneWidth, ViewModel.Assistant.PaneFolded);
            _paneFold.WidthChanged += width => ViewModel.Assistant.PaneWidth = width;
            // The pane's setup banner opens Settings on the AI model page.
            Pane.SettingsRequested += () => OpenSettings(SettingsPages.Model);
            DataContext = ViewModel;
            ThemeManager.Start(ViewModel.Theme);
            BuildLanguageMenu();
            LayOutPane();
            Loaded += async (_, _) => await ViewModel.StartAsync();
            if (!screenshot)
            {
                // Half a minute after start-up, so the eReader scan comes first.
                var warmUp = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                warmUp.Tick += async (_, _) =>
                {
                    warmUp.Stop();
                    await ViewModel.Assistant.WarmUpAsync();
                };
                warmUp.Start();
            }
            Closing += (_, _) => ViewModel.Assistant.Shutdown();
        }

        private const double PaneMinWidth = 300;
        private const double ContentMinWidth = 520;
        private readonly FoldColumn _paneFold;

        /// <summary>The pane's column: its saved width when open, a strip when folded, nothing when closed.</summary>
        private void LayOutPane()
        {
            var assistant = ViewModel.Assistant!;
            _paneFold.SetHidden(!assistant.PaneVisible);
            _paneFold.SetFolded(assistant.PaneFolded);
            _paneFold.Fit(Layout.ActualWidth, ContentMinWidth);
            if (assistant.PaneVisible && !_paneFold.Folded)
            {
                Pane.FocusInput();
            }
        }

        /// <summary>A narrow window folds the pane to make room for the books; it opens again when there is room.</summary>
        private void Layout_SizeChanged(object sender, SizeChangedEventArgs e) => _paneFold.Fit(e.NewSize.Width, ContentMinWidth);

        private void PaneStrip_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.Assistant!.PaneFolded = false;
            _paneFold.SetFolded(false);
            Pane.FocusInput();
        }

        /// <summary>Save a picture of this window to a PNG after a few seconds, then close.</summary>
        public void PrepareScreenshot(string path, bool kindle, bool send, bool collections, bool assistant = false)
        {
            if (assistant)
            {
                // The pane with a sample exchange; the setting isn't saved, since nothing is kept in this mode.
                // Wider, so the book list still fits beside the pane.
                Width = Math.Max(Width, 1440);
                ViewModel.Assistant!.ShowPaneWithoutSaving();
                ViewModel.Assistant.Session.AddDemo();
            }
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

        private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

        /// <summary>Settings on the given page, or the page used last.</summary>
        private void OpenSettings(string? page = null)
        {
            new SettingsWindow(page) { Owner = this, DataContext = ViewModel }.ShowDialog();
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
