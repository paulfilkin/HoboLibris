using System.Windows;
using System.Windows.Controls;
using HoboLibris.ViewModels;

namespace HoboLibris.Views
{
    public partial class KindleView : UserControl
    {
        public KindleView()
        {
            InitializeComponent();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is CheckBox box)
            {
                vm.Kindle.SelectAllVisible(box.IsChecked == true);
            }
        }
    }
}
