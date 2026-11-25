using System.Windows;
using System.Windows.Controls;
using HoboLibris.ViewModels;

namespace HoboLibris.Views
{
    public partial class ArticlesView : UserControl
    {
        public ArticlesView()
        {
            InitializeComponent();
        }

        private ArticlesViewModel? Vm => (DataContext as MainViewModel)?.Articles;

        private void TickAll_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box)
            {
                Vm?.TickAllVisible(box.IsChecked == true);
            }
        }
    }
}
