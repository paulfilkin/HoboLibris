using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HoboLibris.Resources;
using HoboLibris.ViewModels;

namespace HoboLibris.Views
{
    public partial class CollectionsView : UserControl
    {
        public CollectionsView()
        {
            InitializeComponent();
        }

        private CollectionsViewModel? Vm => (DataContext as MainViewModel)?.Collections;

        private void TickAll_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box)
            {
                Vm?.TickAllVisible(box.IsChecked == true);
            }
        }

        private void Tick_Click(object sender, RoutedEventArgs e) => Vm?.UpdateTicked();

        private void NewName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Vm?.CreateCommand.Execute(null);
            }
        }

        private void DeleteCollection_Click(object sender, RoutedEventArgs e)
        {
            ConfirmText.Text = string.Format(Strings.Coll_DeleteConfirm, Vm?.ShownCollection ?? "");
            DeleteButton.Visibility = Visibility.Collapsed;
            ConfirmDelete.Visibility = Visibility.Visible;
        }

        private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            Vm?.DeleteShown();
            KeepCollection_Click(sender, e);
        }

        private void KeepCollection_Click(object sender, RoutedEventArgs e)
        {
            ConfirmDelete.Visibility = Visibility.Collapsed;
            DeleteButton.Visibility = Visibility.Visible;
        }
    }
}
