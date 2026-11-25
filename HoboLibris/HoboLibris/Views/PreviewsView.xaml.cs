using System.Windows;
using System.Windows.Controls;
using HoboLibris.ViewModels;

namespace HoboLibris.Views
{
    public partial class PreviewsView : UserControl
    {
        public PreviewsView()
        {
            InitializeComponent();
        }

        private PreviewsViewModel? Vm => (DataContext as MainViewModel)?.Previews;

        private void TickAll_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box)
            {
                Vm?.TickAll(box.IsChecked == true);
            }
        }

        private void TickAllWish_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box)
            {
                Vm?.TickAllWish(box.IsChecked == true);
            }
        }
    }
}
