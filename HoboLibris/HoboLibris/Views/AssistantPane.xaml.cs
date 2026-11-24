using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HoboLibris.Assistant;
using HoboLibris.Resources;
using HoboLibris.ViewModels;

namespace HoboLibris.Views
{
    /// <summary>The Assistant pane on the right of the main window.</summary>
    public partial class AssistantPane : UserControl
    {
        private AssistantViewModel? _model;

        public AssistantPane()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => Attach(DataContext as AssistantViewModel);
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible)
                {
                    ScrollToEnd();
                    FocusInput();
                }
            };
        }

        /// <summary>The banner's Settings button: the window opens Settings.</summary>
        public event Action? SettingsRequested;

        public void FocusInput() => Dispatcher.BeginInvoke(() => InputBox.Focus(), DispatcherPriority.Input);

        private void Attach(AssistantViewModel? model)
        {
            if (_model != null)
            {
                _model.Items.CollectionChanged -= Items_CollectionChanged;
            }
            _model = model;
            if (_model != null)
            {
                _model.Items.CollectionChanged += Items_CollectionChanged;
                ScrollToEnd();
            }
        }

        private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

        private void ScrollToEnd() => Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);

        /// <summary>Enter sends; Shift+Enter starts a new line.</summary>
        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (_model is { IsBusy: false } && _model.SendOrStopCommand.CanExecute(null))
                {
                    _model.SendOrStopCommand.Execute(null);
                }
            }
        }

        /// <summary>The copy icon under a prompt or an answer: the answer is copied as plain text.</summary>
        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }
            var text = button.DataContext switch
            {
                AssistantMessage answer => ChatText.ToPlainText(answer.Text),
                UserMessage prompt => prompt.Text,
                _ => null,
            };
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The clipboard is busy; the person can try again.
                return;
            }
            // A tick for two seconds, then the copy icon again.
            var tip = button.ToolTip;
            button.Content = "\uE73E";
            button.ToolTip = Strings.Asst_Copied;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                button.ClearValue(ContentControl.ContentProperty);
                button.ToolTip = tip;
            };
            timer.Start();
        }

        private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    }
}
