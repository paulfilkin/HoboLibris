using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HoboLibris.Models;
using HoboLibris.Resources;
using HoboLibris.Services;

namespace HoboLibris.ViewModels
{
    /// <summary>One row of the Send to eReader list.</summary>
    public partial class SendRow : ObservableObject
    {
        public SendItem Item { get; }
        public string Id => Item.Id;
        public string FileName => Item.FileName;
        public string Format => Item.Format;
        public string Path => Item.Path;
        public Verdict Verdict => Item.Verdict;
        public VerdictKind Kind => Item.Verdict.Kind;

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowProgress), nameof(ShowVerdict), nameof(ShowSendButton), nameof(IsSent),
            nameof(IsFailed), nameof(IsQueued), nameof(Progress), nameof(Error))]
        private RowState _state = RowState.Idle;

        /// <summary>Set while the eReader is connected and nothing is being sent.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowSendButton))]
        private bool _canSendNow;

        /// <summary>Set once this row's file was sent in this session.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowVerdict), nameof(ShowSendButton), nameof(IsSent))]
        private bool _sent;

        public SendRow(SendItem item)
        {
            Item = item;
        }

        public bool IsQueued => State.Status == RowStatus.Queued;
        public bool ShowProgress => State.Status == RowStatus.Working;
        public double Progress => State.Progress;
        public bool IsFailed => State.Status == RowStatus.Failed;
        public string? Error => State.Error;
        public bool IsSent => Sent && State.Status == RowStatus.Idle;
        public bool ShowVerdict => State.Status == RowStatus.Idle && !Sent;
        public bool ShowSendButton => ShowVerdict && Verdict.CanSend && CanSendNow;
        public string SendButtonText => Kind == VerdictKind.Ready ? Strings.Send_Send : Strings.Send_SendAnyway;
    }

    /// <summary>The Send to eReader screen.</summary>
    public partial class SendViewModel : ObservableObject
    {
        private readonly Dispatcher _dispatcher;
        public ObservableCollection<SendRow> Rows { get; } = new();
        public BatchProgress Progress { get; } = new(Strings.Progress_Sending);

        [ObservableProperty]
        private bool _sending;

        [ObservableProperty]
        private bool _scanned;

        /// <summary>Set when automatic sending has started for the connected eReader, so it runs once.</summary>
        public bool AutoDone { get; set; }

        public event Action<string>? StatusChanged;
        public event Action? SendFinished;

        public SendViewModel(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        /// <summary>Rows that are only in the Kobo folder: not store books, not on the connected eReader.</summary>
        public IEnumerable<SendRow> FolderOnly() =>
            Rows.Where(r => r.Kind is not (VerdictKind.StoreBook or VerdictKind.OnReader));

        public List<string> Ready() => Rows.Where(r => r.Kind == VerdictKind.Ready).Select(r => r.Id).ToList();

        public List<string> SelectedIds() => Rows.Where(r => r.IsSelected).Select(r => r.Id).ToList();

        public int SelectedCount => Rows.Count(r => r.IsSelected);
        public bool AllSelected => Rows.Count > 0 && Rows.All(r => r.IsSelected);

        public void SelectAll(bool on)
        {
            foreach (var r in Rows)
            {
                r.IsSelected = on;
            }
        }

        private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SendRow.IsSelected))
            {
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(AllSelected));
            }
        }

        /// <summary>Look through the Kobo folder for books to send.</summary>
        public async Task ScanAsync(string dir, IReadOnlyList<DeviceBook> reader, IReadOnlyList<(string Title, string? SavedPath)> store, bool readerConnected)
        {
            var items = await Task.Run(() => SendService.ScanFolder(dir, reader, store, readerConnected));
            var selected = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
            var kept = Rows.Where(r => r.State.Status != RowStatus.Idle || r.Sent).ToDictionary(r => r.Id, r => r);
            foreach (var r in Rows)
            {
                r.PropertyChanged -= Row_PropertyChanged;
            }
            Rows.Clear();
            foreach (var item in items)
            {
                var row = new SendRow(item) { IsSelected = selected.Contains(item.Id), CanSendNow = readerConnected && !Sending };
                if (kept.TryGetValue(item.Id, out var old))
                {
                    row.State = old.State;
                    row.Sent = old.Sent;
                }
                row.PropertyChanged += Row_PropertyChanged;
                Rows.Add(row);
            }
            Scanned = true;
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(AllSelected));
        }

        public void SetReaderConnected(bool connected)
        {
            foreach (var row in Rows)
            {
                row.CanSendNow = connected && !Sending;
            }
        }

        public async Task SendAsync(IReadOnlyList<string> ids, string readerRoot)
        {
            if (Sending)
            {
                return;
            }
            var rows = ids.Select(id => Rows.FirstOrDefault(r => r.Id == id)).Where(r => r != null).Select(r => r!).ToList();
            if (rows.Count == 0)
            {
                return;
            }
            foreach (var r in rows)
            {
                r.State = RowState.Queued;
            }
            Progress.Start(rows.Count);
            Sending = true;
            SetReaderConnected(true);
            StatusChanged?.Invoke(string.Format(Strings.Status_SendingBooks, rows.Count));
            await Task.Run(() =>
            {
                foreach (var row in rows)
                {
                    var last = -1.0;
                    var progress = new Progress<double>(p =>
                    {
                        if (p - last >= 0.01 || p >= 1.0)
                        {
                            last = p;
                            row.State = RowState.Working(p);
                            Progress.Partial = p;
                        }
                    });
                    try
                    {
                        SendService.Send(row.Item, readerRoot, progress);
                        _dispatcher.Invoke(() =>
                        {
                            row.Sent = true;
                            row.State = RowState.Idle;
                        });
                    }
                    catch (Exception ex)
                    {
                        LogManager.Error(ex, $"Sending {row.Path} failed");
                        var message = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Send_CouldNotWriteToReader, ex.Message);
                        _dispatcher.Invoke(() => row.State = RowState.Failed(message));
                    }
                    _dispatcher.Invoke(() =>
                    {
                        Progress.Done++;
                        Progress.Partial = 0;
                    });
                }
            });
            Sending = false;
            Progress.Finish();
            SetReaderConnected(true);
            var failed = rows.Count(r => r.IsFailed);
            var sent = rows.Count - failed;
            StatusChanged?.Invoke(failed == 0
                ? string.Format(Strings.Status_SentBooks, sent)
                : string.Format(Strings.Status_SentWithFailures, sent, failed));
            SendFinished?.Invoke();
        }
    }
}
