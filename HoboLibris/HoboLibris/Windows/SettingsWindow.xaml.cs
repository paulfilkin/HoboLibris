using System.Windows;

namespace HoboLibris.Windows
{
    /// <summary>
    /// The folders HoboLibris uses: the Kobo and Kindle book folders, the extra folders searched for
    /// Kindle books, and where the eReader's book list is copied before collections are saved.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            // Fit the screen; the sections scroll when they don't.
            MaxHeight = SystemParameters.WorkArea.Height - 40;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
