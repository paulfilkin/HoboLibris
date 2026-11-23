using System.Windows;
using System.Windows.Controls;
using HoboLibris.Helpers;
using HoboLibris.Resources;

namespace HoboLibris.Windows
{
    public partial class LanguageWindow : Window
    {
        public bool LanguageChanged { get; private set; }

        public LanguageWindow()
        {
            InitializeComponent();
            var style = (Style)FindResource("LanguageButton");
            var current = LanguageManager.GetCurrentLanguage();
            foreach (var code in LanguageManager.Supported)
            {
                var button = new Button
                {
                    Style = style,
                    Tag = code,
                    Content = LanguageManager.GetLanguageDisplayName(code),
                    FontWeight = code.Equals(current, StringComparison.OrdinalIgnoreCase) ? FontWeights.SemiBold : FontWeights.Normal,
                };
                button.Click += SelectLanguage_Click;
                LanguageList.Children.Add(button);
            }
        }

        private void SelectLanguage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string code })
            {
                return;
            }
            if (code.Equals(LanguageManager.GetCurrentLanguage(), StringComparison.OrdinalIgnoreCase))
            {
                Close();
                return;
            }
            LanguageManager.SetLanguage(code);
            LanguageChanged = true;
            var result = MessageBox.Show(this, Strings.Language_RestartPrompt, Strings.Language_RestartTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(Environment.ProcessPath!);
                Application.Current.Shutdown();
            }
            else
            {
                Close();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
