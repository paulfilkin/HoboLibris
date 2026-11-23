using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HoboLibris.Helpers;
using HoboLibris.Resources;
using HoboLibris.Services;
using HoboLibris.ViewModels;

namespace HoboLibris.Views
{
    public partial class CollectionsView : UserControl
    {
        private const double BooksMinWidth = 460;
        private FoldColumn? _listFold;

        public CollectionsView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => SetUpList();
        }

        private MainViewModel? Main => DataContext as MainViewModel;

        /// <summary>The list of collections: its saved width and fold, kept in settings.json.</summary>
        private void SetUpList()
        {
            if (Main is not { } main || _listFold != null)
            {
                return;
            }
            _listFold = new FoldColumn(ListColumn, ListSplitter, ListCard, ListStrip, 220, 520,
                main.Settings.CollectionsListWidth, main.Settings.CollectionsListFolded);
            _listFold.WidthChanged += width =>
            {
                main.Settings.CollectionsListWidth = width;
                SettingsService.Save(main.Settings);
            };
            _listFold.FoldedChanged += folded =>
            {
                main.Settings.CollectionsListFolded = folded;
                SettingsService.Save(main.Settings);
            };
        }

        private void ListLayout_SizeChanged(object sender, SizeChangedEventArgs e) => _listFold?.Fit(e.NewSize.Width, BooksMinWidth);

        private void FoldList_Click(object sender, RoutedEventArgs e)
        {
            _listFold?.SetFolded(true);
            ListStrip.Focus();
        }

        private void ListStrip_Click(object sender, RoutedEventArgs e) => _listFold?.SetFolded(false);

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
