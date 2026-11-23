using System.Windows;
using HoboLibris.ViewModels;

namespace HoboLibris.Windows
{
    /// <summary>
    /// The folders HoboLibris uses: the Kobo and Kindle book folders, the extra folders searched for
    /// Kindle books, and where the eReader's book list is copied before collections are saved.  Also
    /// the Assistant's AI model, instructions and tools.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            // Fit the screen; the sections scroll when they don't.
            MaxHeight = SystemParameters.WorkArea.Height - 40;
            // Whether Ollama is running, for the AI model section.
            Loaded += async (_, _) =>
            {
                if (Assistant != null)
                {
                    await Assistant.RefreshOllamaAsync();
                }
            };
        }

        private AssistantViewModel? Assistant => (DataContext as MainViewModel)?.Assistant;

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
