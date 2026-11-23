using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HoboLibris.Models;
using HoboLibris.Resources;
using HoboLibris.Services;

namespace HoboLibris.ViewModels
{
    public enum DeviceKind
    {
        Kindle,
        KoboReader,
    }

    /// <summary>
    /// The books found on one kind of device (Kindle or Kobo eReader), their backup state and
    /// the running copies. The Kindle tab shows one of these on its own; the Kobo tab folds the
    /// eReader's rows into its combined list.
    /// </summary>
    public partial class DeviceTabViewModel : ObservableObject
    {
        public DeviceKind Kind { get; }
        public string Name => Kind == DeviceKind.Kindle ? Strings.Device_Kindle : Strings.Device_KoboReader;
        private readonly RowSource _rowSource;
        private readonly Dispatcher _dispatcher;

        public ObservableCollection<BookRow> Rows { get; } = new();
        public BatchProgress Progress { get; } = new(Strings.Progress_BackingUp);

        /// <summary>Places books were looked for and found (devices, folders).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsConnected))]
        private int _sources;

        [ObservableProperty]
        private bool _scanning;

        [ObservableProperty]
        private bool _copying;

        [ObservableProperty]
        private bool _scanned;

        /// <summary>Why no device could be read, in plain words.</summary>
        [ObservableProperty]
        private string? _note;

        /// <summary>Set after the app ejected the device, until one is found again.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsConnected))]
        private bool _ejected;

        public DateTime? LastScan { get; private set; }

        /// <summary>Drive letters ("D:") of the devices whose books are listed.</summary>
        public List<string> Drives => Rows
            .Select(r => r.DeviceBook?.Drive)
            .Where(d => d != null)
            .Select(d => d!)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        public bool IsConnected => Sources > 0;

        // For the Kindle tab, which shows this view model on its own.
        public int NotYetSavedCount => Rows.Count(r => r.NeedsBackup);
        public int SelectedCount => Rows.Count(r => r.IsSelected);
        public string BackUpAllText => string.Format(Strings.Kobo_BackUpAll, NotYetSavedCount);
        public string BackUpSelectedText => string.Format(Strings.Kobo_BackUpSelected, SelectedCount);
        public bool CanBackUpAll => !Copying && NotYetSavedCount > 0;
        public bool CanBackUpSelected => !Copying && SelectedCount > 0;
        public bool CanEject => IsConnected && Drives.Count > 0 && !Copying;
        public string EjectText => string.Format(Strings.Device_Eject, Name);
        public string EjectHint => Copying ? Strings.Device_EjectWait : string.Format(Strings.Device_EjectHint, Name);
        public bool AllSelected => Rows.Count > 0 && Rows.All(r => r.IsSelected);
        public bool HasBooks => Rows.Count > 0;
        public bool ShowEmpty => Rows.Count == 0;
        public bool ShowWaiting => Rows.Count == 0 && Sources == 0;
        public string EmptyHeading => Ejected ? string.Format(Strings.Device_EjectedHeading, Name) : string.Format(Strings.Device_NoneFoundYet, Name);
        public string EmptyText => Ejected
            ? string.Format(Strings.Device_EjectedText, Name)
            : Note ?? string.Format(Strings.Device_PlugIn, Name);
        public string NoBooksText => Scanning ? string.Format(Strings.Device_Looking, Name) : string.Format(Strings.Device_NoBooks, Name);

        [ObservableProperty]
        private string _search = "";

        public ObservableCollection<BookRow> VisibleRows { get; } = new();

        partial void OnSearchChanged(string value) => Refilter();

        private void Refilter()
        {
            VisibleRows.Clear();
            var q = Search.Trim();
            foreach (var r in Rows.Where(r => r.Matches(q)))
            {
                VisibleRows.Add(r);
            }
            OnPropertyChanged(nameof(AllSelected));
        }

        public void SelectAllVisible(bool on)
        {
            foreach (var r in VisibleRows)
            {
                r.IsSelected = on;
            }
        }

        private void NotifyCounts()
        {
            OnPropertyChanged(nameof(NotYetSavedCount));
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(BackUpAllText));
            OnPropertyChanged(nameof(BackUpSelectedText));
            OnPropertyChanged(nameof(CanBackUpAll));
            OnPropertyChanged(nameof(CanBackUpSelected));
            OnPropertyChanged(nameof(CanEject));
            OnPropertyChanged(nameof(EjectHint));
            OnPropertyChanged(nameof(AllSelected));
            OnPropertyChanged(nameof(HasBooks));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ShowWaiting));
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(NoBooksText));
        }

        private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(BookRow.IsSelected) or nameof(BookRow.SavedPath) or nameof(BookRow.State))
            {
                NotifyCounts();
            }
        }

        /// <summary>Raised after every scan; true when it was the automatic re-check.</summary>
        public event Action<bool>? ScanFinished;
        public event Action<string>? StatusChanged;
        public event Action<string>? ErrorRaised;

        public DeviceTabViewModel(DeviceKind kind, Dispatcher dispatcher)
        {
            Kind = kind;
            _rowSource = kind == DeviceKind.Kindle ? RowSource.Kindle : RowSource.Reader;
            _dispatcher = dispatcher;
        }

        public void RescanSaved(string dir)
        {
            foreach (var row in Rows)
            {
                row.SavedPath = row.DeviceBook?.SavedIn(dir);
            }
        }

        /// <summary>
        /// Look for the device and its books. quiet is the automatic re-check while waiting for a
        /// device: it leaves the status line alone.
        /// </summary>
        public async Task ScanAsync(IReadOnlyList<string> savedStoreTitles, IReadOnlyList<string> extraDirs, string backupDir, bool quiet)
        {
            if (Scanning)
            {
                return;
            }
            Scanning = true;
            Scanned = true;
            LastScan = DateTime.UtcNow;
            if (!quiet)
            {
                StatusChanged?.Invoke(string.Format(Strings.Status_LookingFor, Name));
            }
            List<DeviceBook> books;
            int sources;
            string? note;
            try
            {
                (books, sources, note) = await Task.Run(() =>
                {
                    if (Kind == DeviceKind.Kindle)
                    {
                        var (b, s) = KindleService.Scan(extraDirs);
                        return (b, s, s == 0 ? KindleService.ConnectionNote() : null);
                    }
                    var scan = KoboDeviceService.Scan(savedStoreTitles);
                    return (scan.Books, scan.Readers, scan.Readers == 0 ? KoboDeviceService.ConnectionNote() : null);
                });
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, $"Scanning for {Kind} failed");
                books = new List<DeviceBook>();
                sources = 0;
                note = null;
                ErrorRaised?.Invoke(ex.Message);
            }
            Scanning = false;
            Note = note;
            if (books.Any(b => b.Drive != null))
            {
                Ejected = false;
            }
            var selected = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
            var states = Rows.Where(r => r.State.Status != RowStatus.Idle).ToDictionary(r => r.Id, r => r.State);
            foreach (var r in Rows)
            {
                r.PropertyChanged -= Row_PropertyChanged;
            }
            Rows.Clear();
            foreach (var b in books)
            {
                var row = new BookRow(b, _rowSource) { IsSelected = selected.Contains(b.Id) };
                if (states.TryGetValue(b.Id, out var state))
                {
                    row.State = state;
                }
                row.PropertyChanged += Row_PropertyChanged;
                Rows.Add(row);
            }
            Sources = sources;
            RescanSaved(backupDir);
            Refilter();
            NotifyCounts();
            if (!quiet || sources > 0)
            {
                if (Kind == DeviceKind.Kindle)
                {
                    StatusChanged?.Invoke(string.Format(Strings.Status_BooksFound, Rows.Count, Name));
                }
                else
                {
                    StatusChanged?.Invoke("");
                }
            }
            ScanFinished?.Invoke(quiet);
        }

        /// <summary>Copy the given books into the backup folder, one after another, in the background.</summary>
        public async Task BackUpAsync(IReadOnlyList<string> ids, string dir)
        {
            if (Copying)
            {
                StatusChanged?.Invoke(Strings.Status_WaitForCopies);
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
            Copying = true;
            NotifyCounts();
            StatusChanged?.Invoke(string.Format(Strings.Status_BackingUpBooks, rows.Count, Name));
            await Task.Run(() =>
            {
                foreach (var row in rows)
                {
                    var book = row.DeviceBook!;
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
                        var saved = KindleService.BackUp(book, dir, progress);
                        _dispatcher.Invoke(() =>
                        {
                            row.SavedPath = saved;
                            row.State = RowState.Idle;
                        });
                    }
                    catch (Exception ex)
                    {
                        LogManager.Error(ex, $"Backing up {book.Source} failed");
                        var message = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Kindle_CouldNotCopyBook, ex.Message);
                        _dispatcher.Invoke(() => row.State = RowState.Failed(message));
                    }
                    _dispatcher.Invoke(() =>
                    {
                        Progress.Done++;
                        Progress.Partial = 0;
                    });
                }
            });
            Copying = false;
            Progress.Finish();
            NotifyCounts();
            var failed = Rows.Count(r => r.IsFailed);
            var saved = Rows.Count(r => r.IsSaved);
            StatusChanged?.Invoke(failed == 0
                ? string.Format(Strings.Status_DoneBackedUp, saved, Rows.Count, Name)
                : string.Format(Strings.Status_FinishedWithFailures, failed));
        }

        /// <summary>Safely disconnect the device's drives.</summary>
        public async Task<bool> EjectAsync()
        {
            var drives = Drives;
            if (drives.Count == 0 || Copying)
            {
                return false;
            }
            StatusChanged?.Invoke(string.Format(Strings.Status_Ejecting, Name));
            try
            {
                await Task.Run(() =>
                {
                    foreach (var d in drives)
                    {
                        KindleService.Eject(d);
                    }
                });
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Eject failed");
                ErrorRaised?.Invoke(string.Format(Strings.Status_CouldNotEject, Name, ex.Message));
                return false;
            }
            Ejected = true;
            StatusChanged?.Invoke(string.Format(Strings.Status_Ejected, Name));
            return true;
        }

        /// <summary>Ids of the books "Back up all" would copy.</summary>
        public List<string> NotYetSaved() => Rows.Where(r => r.NeedsBackup).Select(r => r.Id).ToList();

        public List<string> SelectedIds() => Rows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
    }
}
