using System.Windows;
using HoboLibris.Resources;
using HoboLibris.ViewModels;

namespace HoboLibris.Windows
{
    /// <summary>The "Delete selected" question: what goes to the Recycle Bin, and whether to remove eReader books too.</summary>
    public partial class DeleteBooksWindow : Window
    {
        private readonly MainViewModel.DeleteRequest _request;

        public bool RemoveFromReader => RemoveBox.IsChecked == true;

        public DeleteBooksWindow(MainViewModel.DeleteRequest request)
        {
            _request = request;
            InitializeComponent();
            var total = request.Store.Count + request.Reader.Count + request.Folder.Count;
            Heading.Text = total == 1 ? Strings.Delete_TitleOne : string.Format(Strings.Delete_TitleMany, total);
            CopiesText.Text = request.Copies.Count switch
            {
                0 => Strings.Delete_NoCopies,
                1 => Strings.Delete_OneCopy,
                _ => string.Format(Strings.Delete_Copies, request.Copies.Count),
            };
            if (request.Reader.Count > 0)
            {
                RemoveBox.Content = request.Reader.Count == 1
                    ? Strings.Delete_AlsoRemoveOne
                    : string.Format(Strings.Delete_AlsoRemoveMany, request.Reader.Count);
                UpdateRemoveNote();
            }
            else
            {
                ReaderPanel.Visibility = Visibility.Collapsed;
            }
            if (request.Store.Count > 0)
            {
                StoreNote.Text = request.Store.Count == 1
                    ? Strings.Delete_StoreNoteOne
                    : string.Format(Strings.Delete_StoreNoteMany, request.Store.Count);
            }
            else
            {
                StoreNote.Visibility = Visibility.Collapsed;
            }
            UpdateButton();
        }

        private void UpdateRemoveNote()
        {
            RemoveNote.Text = RemoveFromReader ? Strings.Delete_RemoveNoteOn : Strings.Delete_RemoveNoteOff;
        }

        private void UpdateButton()
        {
            DeleteButton.IsEnabled = _request.Copies.Count > 0 || (RemoveFromReader && _request.Reader.Count > 0);
        }

        private void RemoveBox_Click(object sender, RoutedEventArgs e)
        {
            UpdateRemoveNote();
            UpdateButton();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
