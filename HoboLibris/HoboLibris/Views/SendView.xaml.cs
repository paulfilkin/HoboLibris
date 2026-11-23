using System.Windows;
using System.Windows.Controls;
using HoboLibris.ViewModels;

namespace HoboLibris.Views
{
    public partial class SendView : UserControl
    {
        public SendView()
        {
            InitializeComponent();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is CheckBox box)
            {
                vm.Send.SelectAll(box.IsChecked == true);
            }
        }
    }
}
