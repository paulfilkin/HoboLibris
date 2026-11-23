using System.Windows;
using System.Windows.Controls;
using HoboLibris.ViewModels;

namespace HoboLibris.Views
{
    public partial class KoboBackupView : UserControl
    {
        public KoboBackupView()
        {
            InitializeComponent();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is CheckBox box)
            {
                vm.SelectAllKobo(box.IsChecked == true);
            }
        }
    }
}
