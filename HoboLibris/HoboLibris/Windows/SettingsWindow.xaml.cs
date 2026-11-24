using System.Windows;
using System.Windows.Controls;
using HoboLibris.Services;
using HoboLibris.ViewModels;

namespace HoboLibris.Windows
{
    /// <summary>The Settings pages, by the name kept in settings.json ("settings_page"), in menu order.</summary>
    public static class SettingsPages
    {
        public const string General = "general";
        public const string Kobo = "kobo";
        public const string Kindle = "kindle";
        public const string Model = "ai";
        public const string Assistant = "assistant";
        public const string Sources = "sources";
        public const string Tools = "tools";

        public static readonly IReadOnlyList<string> All = new[] { General, Kobo, Kindle, Model, Assistant, Sources, Tools };

        /// <summary>The page's place in the menu; the first page for a name that isn't one.</summary>
        public static int IndexOf(string? name)
        {
            var index = All.ToList().IndexOf((name ?? "").Trim().ToLowerInvariant());
            return index < 0 ? 0 : index;
        }
    }

    /// <summary>
    /// Settings, one page at a time from a menu: appearance; the Kobo and Kindle book folders, the
    /// extra folders searched for Kindle books, and where the eReader's book list is copied before
    /// collections are saved; and the Assistant's AI model, instructions, e-book sources and tools.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly string? _openOn;
        private bool _modelPageShown;

        // The tab control selects its first page by itself while the window loads: that isn't the
        // person's choice, so nothing is kept until the window has opened on the right page.
        private bool _opened;

        /// <param name="page">The page to open on (SettingsPages); the one used last when null.</param>
        public SettingsWindow(string? page = null)
        {
            _openOn = page;
            InitializeComponent();
            // Fit the screen; a page longer than the window scrolls.
            MaxHeight = SystemParameters.WorkArea.Height - 40;
            Height = Math.Min(Height, MaxHeight);
            Loaded += (_, _) =>
            {
                Pages.SelectedIndex = SettingsPages.IndexOf(_openOn ?? Main?.Settings.SettingsPage);
                _opened = true;
                Remember();
            };
        }

        private MainViewModel? Main => DataContext as MainViewModel;
        private AssistantViewModel? Assistant => Main?.Assistant;

        /// <summary>
        /// The page chosen is kept for next time.  The AI model page asks whether Ollama is running
        /// and reads the service's models the first time it is shown.
        /// </summary>
        private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, Pages) && _opened)
            {
                Remember();
            }
        }

        private async void Remember()
        {
            if (Pages.SelectedIndex < 0 || Main is not { } main)
            {
                return;
            }
            var name = SettingsPages.All[Pages.SelectedIndex];
            if (main.Settings.SettingsPage != name)
            {
                main.Settings.SettingsPage = name;
                SettingsService.Save(main.Settings);
            }
            if (name == SettingsPages.Model && !_modelPageShown && Assistant != null)
            {
                _modelPageShown = true;
                // Both wait on a port that may not answer; ask at the same time.
                await Task.WhenAll(Assistant.RefreshOllamaAsync(), Assistant.RefreshModelsAsync());
            }
        }

        /// <summary>
        /// A key typed in the box is encrypted and saved when the box loses focus, and the box is
        /// emptied: a saved key is never shown again, only "A key is saved".
        /// </summary>
        private void KeyBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => SaveKey();

        private void SaveKey()
        {
            if (KeyBox.Password.Trim().Length > 0)
            {
                Assistant?.SetApiKey(KeyBox.Password);
                KeyBox.Clear();
            }
            if (GoogleKeyBox.Password.Trim().Length > 0)
            {
                Assistant?.SetGoogleBooksKey(GoogleKeyBox.Password);
                GoogleKeyBox.Clear();
            }
        }

        private void GoogleKeyBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => SaveKey();

        /// <summary>Adds the catalogue typed in; the password box is emptied once it is saved.</summary>
        private void AddCatalogue_Click(object sender, RoutedEventArgs e)
        {
            if (Assistant?.AddCatalogue(CataloguePasswordBox.Password) == true)
            {
                CataloguePasswordBox.Clear();
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveKey();
            base.OnClosing(e);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
