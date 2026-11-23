using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoboLibris.Helpers;
using HoboLibris.Models;
using HoboLibris.Resources;
using HoboLibris.Services;

namespace HoboLibris.ViewModels
{
    public enum MainTab
    {
        Kobo,
        Kindle,
    }

    public enum KoboMode
    {
        Backup,
        Collections,
        Send,
    }

    public enum SortBy
    {
        Title,
        Author,
    }

    /// <summary>
    /// The whole app: the Kobo tab (store books, eReader books and folder books in one list,
    /// plus the Collections and Send screens) and the Kindle tab.
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly Dispatcher _dispatcher;
        private readonly KoboStoreService _store = new();
        private readonly DispatcherTimer _timer;

        public AppSettings Settings { get; private set; }
        public List<StoreBook> StoreBooks { get; private set; }
        /// <summary>One row per store book, kept across rebuilds so selection and state survive.</summary>
        private readonly Dictionary<string, BookRow> _storeRows = new();

        /// <summary>The combined Kobo list, filtered and sorted.</summary>
        public ObservableCollection<BookRow> KoboRows { get; } = new();
        public DeviceTabViewModel Reader { get; }
        public DeviceTabViewModel Kindle { get; }
        public SendViewModel Send { get; }
        public CollectionsViewModel Collections { get; }
        public BatchProgress StoreProgress { get; } = new(Strings.Progress_BackingUp);

        /// <summary>Asks the person to confirm deleting the ticked books; returns (yes, also remove from eReader).</summary>
        public Func<DeleteRequest, (bool Confirmed, bool RemoveFromReader)>? ConfirmDelete { get; set; }
        /// <summary>Opens the sign-in window and returns the kobo:// address, or null when closed.</summary>
        public Func<string, string?>? ShowSignIn { get; set; }
        public Func<string, string?, string?>? PickFolder { get; set; }
        /// <summary>Asks whether to move the given number of book-list backups to a new folder.</summary>
        public Func<int, bool>? ConfirmMoveBackups { get; set; }
        /// <summary>Shows a problem in a message box over the window in front.</summary>
        public Action<string>? ShowError { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsKoboTab), nameof(IsKindleTab), nameof(Summary))]
        private MainTab _tab = MainTab.Kobo;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBackupMode), nameof(IsCollectionsMode), nameof(IsSendMode))]
        private KoboMode _koboMode = KoboMode.Backup;

        [ObservableProperty]
        private string _search = "";

        [ObservableProperty]
        private SortBy _sort = SortBy.Title;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusLine))]
        private string _status = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? _error;

        /// <summary>Signing in or loading the library.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBusy))]
        private bool _busy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBusy), nameof(KoboBusy))]
        private bool _downloading;

        [ObservableProperty]
        private string _pasteLink = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SignedIn), nameof(SignedInText), nameof(HasEmail), nameof(ShowSignedOutCard), nameof(ShowSignInBanner))]
        private bool _signedInFlag;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ReaderConnected), nameof(ShowSignedOutCard), nameof(ReaderNoteText), nameof(ShowReaderWaitingNote))]
        private bool _readerConnectedFlag;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowProgress), nameof(ProgressFraction), nameof(ProgressText))]
        private int _progressTick;

        public bool IsKoboTab => Tab == MainTab.Kobo;
        public bool IsKindleTab => Tab == MainTab.Kindle;
        public bool IsBackupMode => KoboMode == KoboMode.Backup;
        public bool IsCollectionsMode => KoboMode == KoboMode.Collections;
        public bool IsSendMode => KoboMode == KoboMode.Send;
        public bool HasError => !string.IsNullOrEmpty(Error);
        public bool SignedIn => SignedInFlag;
        public bool ReaderConnected => ReaderConnectedFlag;
        public bool IsBusy => Busy || Downloading || Send.Sending || Reader.Copying || Reader.Scanning || Kindle.Copying || Kindle.Scanning;
        /// <summary>Downloads or eReader copies are running on the Kobo tab.</summary>
        public bool KoboBusy => Downloading || Reader.Copying;
        public bool ShowSignedOutCard => !SignedIn && !ReaderConnected;
        public bool ShowSignInBanner => !SignedIn;
        /// <summary>
        /// The account's email address is hidden until the eye beside it is pressed: it needn't be on
        /// screen all the time, or in screenshots.  Hidden again at every start.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SignedInText), nameof(EmailToggleText))]
        private bool _showEmail;

        public bool HasEmail => !string.IsNullOrEmpty(Settings.Email);
        public string SignedInText => !HasEmail || !ShowEmail ? Strings.Main_SignedIn : string.Format(Strings.Main_SignedInAs, Settings.Email);
        public string EmailToggleText => ShowEmail ? Strings.Main_HideEmail : Strings.Main_ShowEmail;
        public string KoboFolder => Settings.DownloadDir;
        public string KindleFolder => Settings.KindleDir;
        public IReadOnlyList<string> KindleExtraFolders => Settings.KindleExtraDirs.ToList();
        public bool HasKindleExtraFolders => Settings.KindleExtraDirs.Count > 0;
        /// <summary>Light, dark or following Windows; a change shows at once.</summary>
        public AppTheme Theme
        {
            get => ThemeManager.Parse(Settings.Theme);
            set
            {
                var name = ThemeManager.ToSetting(value);
                if (Settings.Theme == name)
                {
                    return;
                }
                Settings.Theme = name;
                SettingsService.Save(Settings);
                OnPropertyChanged();
                ThemeManager.Choose(value);
            }
        }

        /// <summary>Where the eReader's book list is copied before collections are saved.</summary>
        public string BackupFolder => string.IsNullOrEmpty(Settings.BookListBackupDir)
            ? AppPaths.BookListBackupDir
            : Settings.BookListBackupDir;
        public bool AutoSend
        {
            get => Settings.KoboAutoSend;
            set
            {
                Settings.KoboAutoSend = value;
                SettingsService.Save(Settings);
                OnPropertyChanged();
            }
        }

        /// <summary>The grey line above the Kobo list when no eReader is connected.</summary>
        public string ReaderNoteText => Reader.Ejected ? Strings.Kobo_ReaderEjectedNote : Strings.Kobo_PlugInNote;
        public bool ShowReaderWaitingNote => !ReaderConnected;
        public int ReadyToSend => Send.Ready().Count;
        public string SendTabLabel => ReadyToSend > 0 ? string.Format(Strings.Kobo_SendTabCount, ReadyToSend) : Strings.Kobo_SendTab;
        public bool ShowSendWaitingNote => ReaderConnected && ReadyToSend > 0 && !Send.Sending;
        public string SendWaitingText => ReadyToSend == 1 ? Strings.Kobo_OneBookWaiting : string.Format(Strings.Kobo_BooksWaiting, ReadyToSend);

        public string BackUpAllText => string.Format(Strings.Kobo_BackUpAll, NotYetSavedCount);
        public string BackUpSelectedText => string.Format(Strings.Kobo_BackUpSelected, SelectedCount);
        public string DeleteSelectedText => string.Format(Strings.Kobo_DeleteSelected, DeleteCount);
        public int NotYetSavedCount => StoreNotYetSaved().Count + Reader.NotYetSaved().Count;
        public int SelectedCount => StoreSelected().Count + Reader.SelectedIds().Count;
        public int DeleteCount => SelectedCount + FolderSelected().Count;
        public bool CanBackUpAll => !KoboBusy && NotYetSavedCount > 0;
        public bool CanBackUpSelected => !KoboBusy && SelectedCount > 0;
        public bool CanDelete => !KoboBusy && DeleteCount > 0;
        public bool CanEjectReader => ReaderConnected && !Reader.Copying;
        public bool CanEjectForCollections => CanEjectReader && Collections.Pending == 0;
        public string EjectCollectionsHint => Collections.Pending > 0 ? Strings.Coll_EjectSaveFirst : Strings.Coll_EjectHint;
        public bool AllKoboSelected => KoboRows.Count > 0 && KoboRows.All(r => r.IsSelected);
        public string KoboEmptyText => Search.Length > 0 ? Strings.List_NoMatch : Strings.Kobo_NoBooksYet;

        /// <summary>One bar for the store downloads and the eReader copies together.</summary>
        public bool ShowProgress => StoreProgress.IsActive || Reader.Progress.IsActive;
        public double ProgressFraction
        {
            get
            {
                var (done, total, partial) = CombinedProgress();
                return total == 0 ? 0 : Math.Clamp((done + partial) / total, 0, 1);
            }
        }
        public string ProgressText
        {
            get
            {
                var (done, total, _) = CombinedProgress();
                return done >= total
                    ? string.Format(Strings.Progress_AllFinished, Strings.Progress_BackingUp, total)
                    : string.Format(Strings.Progress_BookOf, Strings.Progress_BackingUp, done + 1, total, Math.Round(ProgressFraction * 100));
            }
        }

        private (int Done, int Total, double Partial) CombinedProgress()
        {
            int done = 0, total = 0;
            double partial = 0;
            foreach (var p in new[] { StoreProgress, Reader.Progress })
            {
                if (p.IsActive)
                {
                    done += p.Done;
                    total += p.Total;
                    if (p.Done < p.Total)
                    {
                        partial += p.Partial;
                    }
                }
            }
            return (done, total, partial);
        }

        public string StatusLine => string.IsNullOrEmpty(Status) ? Summary : Status;

        /// <summary>How many books the current tab lists, and where they come from.</summary>
        public string Summary
        {
            get
            {
                string Books(int n) => n == 1 ? Strings.Summary_OneBook : string.Format(Strings.Summary_Books, n);
                if (Tab == MainTab.Kindle)
                {
                    var n = Kindle.Rows.Count;
                    return n == 1 ? string.Format(Strings.Summary_OneDeviceBook, Kindle.Name) : string.Format(Strings.Summary_DeviceBooks, n, Kindle.Name);
                }
                var store = StoreBooks.Count;
                var reader = Reader.Rows.Count;
                var folder = Send.FolderOnly().Count();
                var parts = new List<string>();
                if (store > 0)
                {
                    parts.Add(string.Format(Strings.Summary_FromStore, store));
                }
                if (reader > 0)
                {
                    parts.Add(string.Format(Strings.Summary_OnReader, reader));
                }
                if (folder > 0)
                {
                    parts.Add(string.Format(Strings.Summary_OnlyOnComputer, folder));
                }
                var total = store + reader + folder;
                return parts.Count > 1
                    ? string.Format(Strings.Summary_LibraryWithParts, Books(total), string.Join(", ", parts))
                    : string.Format(Strings.Summary_Library, Books(total));
            }
        }

        public MainViewModel(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            Settings = SettingsService.Load();
            StoreBooks = SettingsService.LoadLibrary();
            Reader = new DeviceTabViewModel(DeviceKind.KoboReader, dispatcher);
            Kindle = new DeviceTabViewModel(DeviceKind.Kindle, dispatcher);
            Send = new SendViewModel(dispatcher);
            Collections = new CollectionsViewModel { BackupFolder = () => BackupFolder };
            SignedInFlag = Settings.SignedIn;

            foreach (var tab in new[] { Reader, Kindle })
            {
                tab.StatusChanged += s => Status = s;
                tab.ErrorRaised += e => Error = e;
                tab.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(DeviceTabViewModel.Copying) or nameof(DeviceTabViewModel.Scanning))
                    {
                        OnPropertyChanged(nameof(IsBusy));
                        OnPropertyChanged(nameof(KoboBusy));
                        NotifyButtons();
                    }
                };
                tab.Progress.PropertyChanged += (_, _) => ProgressTick++;
            }
            StoreProgress.PropertyChanged += (_, _) => ProgressTick++;
            Reader.ScanFinished += quiet => _ = AfterReaderScanAsync(quiet);
            Reader.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DeviceTabViewModel.Ejected))
                {
                    OnPropertyChanged(nameof(ReaderNoteText));
                }
            };
            Send.StatusChanged += s => Status = s;
            Send.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SendViewModel.Sending))
                {
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(ShowSendWaitingNote));
                }
            };
            Send.SendFinished += () => _ = Reader.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.DownloadDir, quiet: true);
            Collections.StatusChanged += s => Status = s;
            Collections.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CollectionsViewModel.Pending))
                {
                    OnPropertyChanged(nameof(CanEjectForCollections));
                    OnPropertyChanged(nameof(EjectCollectionsHint));
                }
            };

            RebuildStoreRows();
            RescanSaved();
            if (Settings.SignedIn && StoreBooks.Count == 0)
            {
                Status = Strings.Status_SignedInRefresh;
            }

            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        /// <summary>Called by the window once it is shown: look for the eReader straight away.</summary>
        public async Task StartAsync()
        {
            await Reader.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.DownloadDir, quiet: true);
        }

        /// <summary>While no device is connected, look again every few seconds so its books appear by themselves.</summary>
        private void Tick()
        {
            var due4 = (DateTime? t) => t == null || (DateTime.UtcNow - t.Value).TotalSeconds >= 4;
            if (Reader.Scanned && Reader.Sources == 0 && !Reader.Scanning && due4(Reader.LastScan))
            {
                _ = Reader.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.DownloadDir, quiet: true);
            }
            if (Tab == MainTab.Kindle && Kindle.Scanned && Kindle.Sources == 0 && !Kindle.Scanning && due4(Kindle.LastScan))
            {
                _ = Kindle.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.KindleDir, quiet: true);
            }
        }

        partial void OnTabChanged(MainTab value)
        {
            if (value == MainTab.Kindle && !Kindle.Scanned)
            {
                _ = Kindle.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.KindleDir, quiet: false);
            }
        }

        partial void OnKoboModeChanged(KoboMode value)
        {
            if (value == KoboMode.Send)
            {
                _ = ScanSendFolderAsync();
            }
            if (value == KoboMode.Collections && !Collections.Tried)
            {
                _ = Collections.LoadAsync();
            }
        }

        partial void OnSearchChanged(string value)
        {
            RebuildKoboRows();
            OnPropertyChanged(nameof(KoboEmptyText));
        }

        partial void OnSortChanged(SortBy value) => RebuildKoboRows();

        private List<string> StoreTitles() => StoreBooks.Select(b => b.Title).ToList();
        private List<string> StoreNotYetSaved() => _storeRows.Values.Where(r => r.NeedsBackup).Select(r => r.Id).ToList();
        private List<string> StoreSelected() => _storeRows.Values.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        private List<string> FolderSelected() => KoboRows.Where(r => r.Source == RowSource.Folder && r.IsSelected).Select(r => r.Id).ToList();
        private HashSet<string> _folderSelected = new();

        private void RebuildStoreRows()
        {
            var old = _storeRows.ToDictionary(kv => kv.Key, kv => kv.Value);
            _storeRows.Clear();
            foreach (var b in StoreBooks)
            {
                var row = new BookRow(b);
                if (old.TryGetValue(b.Id, out var prev))
                {
                    row.IsSelected = prev.IsSelected;
                    row.State = prev.State;
                    row.SavedPath = prev.SavedPath;
                }
                _storeRows[b.Id] = row;
            }
            RebuildKoboRows();
        }

        /// <summary>Rebuild the combined Kobo list from the store, eReader and folder rows.</summary>
        public void RebuildKoboRows()
        {
            _folderSelected = KoboRows.Where(r => r.Source == RowSource.Folder && r.IsSelected).Select(r => r.Id).ToHashSet();
            foreach (var r in KoboRows)
            {
                r.PropertyChanged -= Row_PropertyChanged;
            }
            var q = Search.Trim();
            var rows = new List<BookRow>();
            rows.AddRange(_storeRows.Values.Where(r => r.Matches(q)));
            rows.AddRange(Reader.Rows.Where(r => r.Matches(q)));
            foreach (var item in Send.FolderOnly())
            {
                var row = new BookRow(item.Item) { IsSelected = _folderSelected.Contains(item.Id) };
                if (row.Matches(q))
                {
                    rows.Add(row);
                }
            }
            var cmp = StringComparer.CurrentCultureIgnoreCase;
            rows.Sort((a, b) => Sort == SortBy.Title
                ? cmp.Compare(a.Title, b.Title) is var t && t != 0 ? t : cmp.Compare(a.Authors, b.Authors)
                : cmp.Compare(a.Authors, b.Authors) is var u && u != 0 ? u : cmp.Compare(a.Title, b.Title));
            KoboRows.Clear();
            foreach (var r in rows)
            {
                r.PropertyChanged += Row_PropertyChanged;
                KoboRows.Add(r);
            }
            NotifyButtons();
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(KoboEmptyText));
        }

        private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(BookRow.IsSelected) or nameof(BookRow.SavedPath) or nameof(BookRow.State))
            {
                NotifyButtons();
            }
        }

        private void NotifyButtons()
        {
            OnPropertyChanged(nameof(BackUpAllText));
            OnPropertyChanged(nameof(BackUpSelectedText));
            OnPropertyChanged(nameof(DeleteSelectedText));
            OnPropertyChanged(nameof(CanBackUpAll));
            OnPropertyChanged(nameof(CanBackUpSelected));
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanEjectReader));
            OnPropertyChanged(nameof(CanEjectForCollections));
            OnPropertyChanged(nameof(AllKoboSelected));
        }

        public void SelectAllKobo(bool on)
        {
            foreach (var r in KoboRows)
            {
                r.IsSelected = on;
            }
        }

        private void RescanSaved()
        {
            foreach (var row in _storeRows.Values)
            {
                row.SavedPath = row.StoreBook!.SavedIn(Settings.DownloadDir);
            }
            NotifyButtons();
        }

        // --- Kobo account -------------------------------------------------------------

        [RelayCommand]
        private async Task SignInAsync()
        {
            if (Busy)
            {
                return;
            }
            Busy = true;
            Error = null;
            Status = Strings.Status_OpeningSignIn;
            var s = Settings.Clone();
            try
            {
                s.UserKey = "";
                s.AccessToken = "";
                s.RefreshToken = "";
                await _store.AuthDeviceAsync(s, false);
                var resources = await _store.InitializationAsync(s);
                var url = KoboStoreService.SignInUrl(s, resources);
                Status = Strings.Status_SignInInWindow;
                var link = ShowSignIn?.Invoke(url);
                if (string.IsNullOrEmpty(link))
                {
                    throw new HoboLibrisException(Strings.Status_SignInClosed);
                }
                var (id, key, email) = KoboStoreService.ParseReturnUrl(link);
                Status = Strings.Status_ConnectingAccount;
                await _store.CompleteSignInAsync(s, id, key, email);
                Settings = s;
                SignedInFlag = true;
                PasteLink = "";
                OnPropertyChanged(nameof(SignedInText));
                Busy = false;
                await RefreshLibraryAsync();
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Sign-in failed");
                Busy = false;
                Status = "";
                Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Error_Unexpected, ex.Message);
            }
        }

        [RelayCommand]
        private async Task SignInWithLinkAsync()
        {
            (string, string, string) parsed;
            try
            {
                parsed = KoboStoreService.ParseReturnUrl(PasteLink);
            }
            catch (HoboLibrisException ex)
            {
                Error = ex.Message;
                return;
            }
            Busy = true;
            Error = null;
            Status = Strings.Status_ConnectingAccount;
            var s = Settings.Clone();
            try
            {
                await _store.CompleteSignInAsync(s, parsed.Item1, parsed.Item2, parsed.Item3);
                Settings = s;
                SignedInFlag = true;
                PasteLink = "";
                OnPropertyChanged(nameof(SignedInText));
                Busy = false;
                await RefreshLibraryAsync();
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Sign-in with link failed");
                Busy = false;
                Status = "";
                Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Error_Unexpected, ex.Message);
            }
        }

        [RelayCommand]
        private void SignOut()
        {
            Settings = Settings.SignedOut();
            SettingsService.Save(Settings);
            SettingsService.SaveLibrary(Array.Empty<StoreBook>());
            try
            {
                if (Directory.Exists(AppPaths.SignInBrowserDir))
                {
                    Directory.Delete(AppPaths.SignInBrowserDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not remove the sign-in browser data: {ex.Message}");
            }
            StoreBooks = new List<StoreBook>();
            _storeRows.Clear();
            SignedInFlag = false;
            RebuildStoreRows();
            Status = Strings.Status_SignedOut;
        }

        [RelayCommand]
        private async Task RefreshListAsync()
        {
            if (SignedIn)
            {
                await RefreshLibraryAsync();
            }
            await Reader.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.DownloadDir, quiet: false);
        }

        private async Task RefreshLibraryAsync()
        {
            if (Busy)
            {
                return;
            }
            Busy = true;
            Error = null;
            Status = Strings.Status_LoadingLibrary;
            var s = Settings.Clone();
            try
            {
                var progress = new Progress<int>(n => Status = string.Format(Strings.Status_LoadingLibraryCount, n));
                var books = await _store.FetchLibraryAsync(s, progress);
                Settings = s;
                SettingsService.Save(Settings);
                SettingsService.SaveLibrary(books);
                StoreBooks = books;
                RebuildStoreRows();
                RescanSaved();
                Status = "";
                // eReader books may now match store books.
                if (Reader.Scanned && !Reader.Copying)
                {
                    _ = Reader.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.DownloadDir, quiet: true);
                }
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Loading the library failed");
                Status = "";
                Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Error_Unexpected, ex.Message);
            }
            Busy = false;
        }

        // --- Kobo downloads and copies -------------------------------------------------

        [RelayCommand]
        private void BackUpAll()
        {
            // Both run at once: downloads from the store, copies from the eReader.
            var store = StoreNotYetSaved();
            var reader = Reader.NotYetSaved();
            if (store.Count > 0)
            {
                _ = QueueDownloadsAsync(store);
            }
            if (reader.Count > 0)
            {
                _ = Reader.BackUpAsync(reader, Settings.DownloadDir);
            }
        }

        [RelayCommand]
        private void BackUpSelected()
        {
            var store = StoreSelected();
            var reader = Reader.SelectedIds();
            if (store.Count > 0)
            {
                _ = QueueDownloadsAsync(store);
            }
            if (reader.Count > 0)
            {
                _ = Reader.BackUpAsync(reader, Settings.DownloadDir);
            }
        }

        /// <summary>The row's own "Back up", "Again" or "Copy" button.</summary>
        [RelayCommand]
        private void BackUpRow(BookRow? row)
        {
            if (row == null)
            {
                return;
            }
            if (row.Source == RowSource.Store)
            {
                _ = QueueDownloadsAsync(new[] { row.Id });
            }
            else if (row.Source == RowSource.Reader)
            {
                _ = Reader.BackUpAsync(new[] { row.Id }, Settings.DownloadDir);
            }
            else if (row.Source == RowSource.Kindle)
            {
                _ = Kindle.BackUpAsync(new[] { row.Id }, Settings.KindleDir);
            }
        }

        private async Task QueueDownloadsAsync(IReadOnlyList<string> ids)
        {
            var rows = ids.Select(id => _storeRows.GetValueOrDefault(id))
                .Where(r => r != null && r.StoreBook!.CanDownload && r.State.Status is not (RowStatus.Queued or RowStatus.Working))
                .Select(r => r!)
                .ToList();
            if (Downloading)
            {
                Status = Strings.Status_WaitForDownloads;
                return;
            }
            if (rows.Count == 0)
            {
                return;
            }
            foreach (var r in rows)
            {
                r.State = RowState.Queued;
            }
            StoreProgress.Start(rows.Count);
            Downloading = true;
            Error = null;
            Status = string.Format(Strings.Status_DownloadingBooks, rows.Count);
            var dir = Settings.DownloadDir;
            var s = Settings.Clone();
            foreach (var row in rows)
            {
                var last = -1.0;
                var progress = new Progress<double>(p =>
                {
                    if (p - last >= 0.01 || p >= 1.0)
                    {
                        last = p;
                        row.State = RowState.Working(p);
                        StoreProgress.Partial = p;
                    }
                });
                try
                {
                    var path = await _store.DownloadAsync(s, row.StoreBook!, dir, progress);
                    row.SavedPath = path;
                    row.State = RowState.Idle;
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex, $"Downloading {row.Title} failed");
                    row.State = RowState.Failed(ex is HoboLibrisException ? ex.Message : string.Format(Strings.Error_Unexpected, ex.Message));
                }
                StoreProgress.Done++;
                StoreProgress.Partial = 0;
            }
            Downloading = false;
            StoreProgress.Finish();
            Settings.AccessToken = s.AccessToken;
            Settings.RefreshToken = s.RefreshToken;
            SettingsService.Save(Settings);
            var failed = _storeRows.Values.Count(r => r.IsFailed);
            var saved = _storeRows.Values.Count(r => r.IsSaved);
            Status = failed == 0
                ? string.Format(Strings.Status_DoneStoreSaved, saved, StoreBooks.Count)
                : string.Format(Strings.Status_FinishedWithFailedDownloads, failed);
            // Store books just saved may now match books on the eReader.
            if (Reader.Scanned && !Reader.Copying)
            {
                _ = Reader.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.DownloadDir, quiet: true);
            }
        }

        [RelayCommand]
        private void ChooseKoboFolder()
        {
            var dir = PickFolder?.Invoke(Strings.Dialog_ChooseKoboFolder, Settings.DownloadDir);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }
            Settings.DownloadDir = dir;
            Settings.KoboReaderDir = dir;
            SettingsService.Save(Settings);
            OnPropertyChanged(nameof(KoboFolder));
            RescanSaved();
            Reader.RescanSaved(dir);
            _ = ScanSendFolderAsync();
        }

        [RelayCommand]
        private void OpenKoboFolder() => Dialogs.OpenFolder(Settings.DownloadDir);

        [RelayCommand]
        private async Task EjectReaderAsync()
        {
            if (!await Reader.EjectAsync())
            {
                return;
            }
            if (Collections.Coll != null)
            {
                Collections.Unload(Strings.Coll_ReaderEjected);
            }
            _ = Reader.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.DownloadDir, quiet: true);
        }

        [RelayCommand]
        private void DismissError() => Error = null;

        // --- Delete selected ----------------------------------------------------------------

        public record DeleteRequest(List<string> Store, List<string> Reader, List<string> Folder, List<string> Copies);

        /// <summary>Backup copies of the ticked books in the Kobo folder.</summary>
        private List<string> FolderCopies(IEnumerable<string> store, IEnumerable<string> reader, IEnumerable<string> folder)
        {
            var copies = new List<string>();
            copies.AddRange(store.Select(id => _storeRows.GetValueOrDefault(id)?.SavedPath).Where(p => p != null).Select(p => p!));
            copies.AddRange(reader.Select(id => Reader.Rows.FirstOrDefault(r => r.Id == id)?.SavedPath).Where(p => p != null).Select(p => p!));
            copies.AddRange(folder.Where(File.Exists));
            return copies.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        }

        [RelayCommand]
        private async Task DeleteSelectedAsync()
        {
            var store = StoreSelected();
            var reader = Reader.SelectedIds();
            var folder = FolderSelected();
            var request = new DeleteRequest(store, reader, folder, FolderCopies(store, reader, folder));
            var answer = ConfirmDelete?.Invoke(request);
            if (answer is not { Confirmed: true })
            {
                return;
            }
            await DeleteBooksAsync(request, answer.Value.RemoveFromReader);
        }

        private async Task DeleteBooksAsync(DeleteRequest p, bool removeFromReader)
        {
            var binned = 0;
            var problems = new List<string>();
            foreach (var path in p.Copies)
            {
                try
                {
                    await Task.Run(() => RecycleBin.Delete(path));
                    binned++;
                }
                catch (Exception ex)
                {
                    problems.Add($"{path}: {ex.Message}");
                }
            }
            var removed = 0;
            var later = 0;
            if (removeFromReader)
            {
                var root = KoboDeviceService.FirstRoot();
                foreach (var id in p.Reader)
                {
                    var book = Reader.Rows.FirstOrDefault(r => r.Id == id)?.DeviceBook;
                    if (book == null)
                    {
                        continue;
                    }
                    try
                    {
                        File.Delete(book.Source);
                        if (File.Exists(book.Source))
                        {
                            throw new IOException("still there");
                        }
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        LogManager.Info($"Could not remove {book.Source} from the eReader now: {ex.Message}");
                        // The eReader went away: remove it the next time it is plugged in.
                        var rel = root != null && book.Source.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                            ? book.Source[root.Length..]
                            : book.Source;
                        rel = rel.Replace('\\', '/');
                        var colon = rel.IndexOf(":/", StringComparison.Ordinal);
                        if (colon >= 0)
                        {
                            rel = rel[(colon + 2)..];
                        }
                        if (!Settings.KoboPendingRemovals.Contains(rel))
                        {
                            Settings.KoboPendingRemovals.Add(rel);
                        }
                        later++;
                    }
                }
                if (later > 0)
                {
                    SettingsService.Save(Settings);
                }
            }
            foreach (var r in _storeRows.Values)
            {
                r.IsSelected = false;
            }
            foreach (var r in Reader.Rows)
            {
                r.IsSelected = false;
            }
            foreach (var r in KoboRows)
            {
                r.IsSelected = false;
            }
            RescanSaved();
            Reader.RescanSaved(Settings.DownloadDir);
            await ScanSendFolderAsync();
            if (removed > 0)
            {
                _ = Reader.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.DownloadDir, quiet: true);
            }
            var parts = new List<string>();
            if (binned > 0)
            {
                parts.Add(binned == 1 ? Strings.Delete_OneBinned : string.Format(Strings.Delete_Binned, binned));
            }
            if (removed > 0)
            {
                parts.Add(string.Format(Strings.Delete_RemovedFromReader, removed));
            }
            if (later > 0)
            {
                parts.Add(string.Format(Strings.Delete_RemovedLater, later));
            }
            Status = parts.Count == 0 ? Strings.Delete_Nothing : string.Join("; ", parts) + ".";
            if (problems.Count > 0)
            {
                Error = string.Format(Strings.Delete_CouldNot, string.Join("; ", problems));
            }
        }

        /// <summary>Remove books that couldn't be removed earlier, now that the eReader is back.</summary>
        private void ApplyPendingRemovals()
        {
            if (Settings.KoboPendingRemovals.Count == 0)
            {
                return;
            }
            var root = KoboDeviceService.FirstRoot();
            if (root == null)
            {
                return;
            }
            var removed = 0;
            var pending = Settings.KoboPendingRemovals.ToList();
            Settings.KoboPendingRemovals.Clear();
            foreach (var rel in pending)
            {
                var path = Path.Combine(root, rel.Replace('/', '\\'));
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn($"Could not remove {path}: {ex.Message}");
                }
            }
            SettingsService.Save(Settings);
            if (removed > 0)
            {
                Status = removed == 1 ? Strings.Delete_PendingOneDone : string.Format(Strings.Delete_PendingDone, removed);
                _ = Reader.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.DownloadDir, quiet: true);
            }
        }

        // --- Send to eReader ---------------------------------------------------------------

        private IReadOnlyList<(string Title, string? SavedPath)> StoreForSend() =>
            StoreBooks.Select(b => (b.Title, _storeRows.GetValueOrDefault(b.Id)?.SavedPath)).ToList();

        public async Task ScanSendFolderAsync()
        {
            var readerBooks = Reader.Rows.Select(r => r.DeviceBook!).ToList();
            await Send.ScanAsync(Settings.DownloadDir, readerBooks, StoreForSend(), Reader.Sources > 0);
            RebuildKoboRows();
            OnPropertyChanged(nameof(ReadyToSend));
            OnPropertyChanged(nameof(SendTabLabel));
            OnPropertyChanged(nameof(ShowSendWaitingNote));
            OnPropertyChanged(nameof(SendWaitingText));
        }

        /// <summary>After the eReader was looked at: refresh what could be sent, and send new books by themselves if asked.</summary>
        private async Task AfterReaderScanAsync(bool quiet)
        {
            var wasConnected = ReaderConnectedFlag;
            ReaderConnectedFlag = Reader.Sources > 0;
            Send.SetReaderConnected(ReaderConnectedFlag && !Send.Sending);
            NotifyButtons();
            if (Send.Sending)
            {
                RebuildKoboRows();
                return;
            }
            if (Reader.Sources > 0)
            {
                ApplyPendingRemovals();
            }
            // The automatic re-check while nothing is plugged in changes nothing in the folder:
            // don't open every EPUB in it again every few seconds.
            if (quiet && !ReaderConnectedFlag && !wasConnected && Send.Scanned)
            {
                return;
            }
            await ScanSendFolderAsync();
            if (Reader.Sources == 0)
            {
                // Unplugged: send again automatically next time it's plugged in.
                Send.AutoDone = false;
                return;
            }
            if (Settings.KoboAutoSend && !Send.AutoDone)
            {
                Send.AutoDone = true;
                var ready = Send.Ready();
                if (ready.Count > 0)
                {
                    await StartSendAsync(ready);
                }
            }
        }

        public async Task StartSendAsync(IReadOnlyList<string> ids)
        {
            if (Send.Sending)
            {
                return;
            }
            var root = KoboDeviceService.FirstRoot();
            if (root == null)
            {
                Error = Strings.Send_NoReader;
                return;
            }
            Error = null;
            await Send.SendAsync(ids, root);
        }

        [RelayCommand]
        private Task SendAllReadyAsync() => StartSendAsync(Send.Ready());

        [RelayCommand]
        private Task SendSelectedAsync() => StartSendAsync(Send.SelectedIds());

        [RelayCommand]
        private Task SendRowAsync(SendRow? row) => row == null ? Task.CompletedTask : StartSendAsync(new[] { row.Id });

        [RelayCommand]
        private Task LookAgainSendAsync() => ScanSendFolderAsync();

        [RelayCommand]
        private void ShowSendScreen() => KoboMode = KoboMode.Send;

        // --- Collections -------------------------------------------------------------------

        [RelayCommand]
        private Task ReloadCollectionsAsync() => Collections.LoadAsync();

        [RelayCommand]
        private Task SaveCollectionsAsync() => Collections.SaveAsync();

        // --- Kindle ------------------------------------------------------------------------

        [RelayCommand]
        private Task KindleLookAgainAsync() => Kindle.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.KindleDir, quiet: false);

        [RelayCommand]
        private Task KindleBackUpAllAsync() => Kindle.BackUpAsync(Kindle.NotYetSaved(), Settings.KindleDir);

        [RelayCommand]
        private Task KindleBackUpSelectedAsync() => Kindle.BackUpAsync(Kindle.SelectedIds(), Settings.KindleDir);

        [RelayCommand]
        private async Task KindleEjectAsync()
        {
            if (await Kindle.EjectAsync())
            {
                _ = Kindle.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.KindleDir, quiet: true);
            }
        }

        [RelayCommand]
        private void ChooseKindleFolder()
        {
            var dir = PickFolder?.Invoke(Strings.Dialog_ChooseKindleFolder, Settings.KindleDir);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }
            Settings.KindleDir = dir;
            SettingsService.Save(Settings);
            OnPropertyChanged(nameof(KindleFolder));
            Kindle.RescanSaved(dir);
        }

        [RelayCommand]
        private void OpenKindleFolder() => Dialogs.OpenFolder(Settings.KindleDir);

        [RelayCommand]
        private async Task KindleAddFolderAsync()
        {
            var dir = PickFolder?.Invoke(Strings.Dialog_ChooseKindleSearchFolder, null);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }
            if (!Settings.KindleExtraDirs.Contains(dir))
            {
                Settings.KindleExtraDirs.Add(dir);
                SettingsService.Save(Settings);
                OnPropertyChanged(nameof(KindleExtraFolders));
                OnPropertyChanged(nameof(HasKindleExtraFolders));
            }
            await Kindle.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.KindleDir, quiet: false);
        }

        [RelayCommand]
        private async Task RemoveKindleFolderAsync(string? dir)
        {
            if (string.IsNullOrEmpty(dir) || !Settings.KindleExtraDirs.Remove(dir))
            {
                return;
            }
            SettingsService.Save(Settings);
            OnPropertyChanged(nameof(KindleExtraFolders));
            OnPropertyChanged(nameof(HasKindleExtraFolders));
            await Kindle.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.KindleDir, quiet: true);
        }

        // --- eReader book-list backups ------------------------------------------------------

        [RelayCommand]
        private void ChooseBackupFolder()
        {
            var old = BackupFolder;
            var dir = PickFolder?.Invoke(Strings.Dialog_ChooseBackupFolder, old);
            if (string.IsNullOrEmpty(dir) || BookListBackups.SameFolder(dir, old))
            {
                return;
            }
            var count = BookListBackups.Count(old);
            if (count > 0 && ConfirmMoveBackups?.Invoke(count) == true)
            {
                try
                {
                    BookListBackups.Move(old, dir);
                }
                catch (Exception ex)
                {
                    // Whatever moved is in the new folder; the rest stays in the old one.
                    LogManager.Error(ex, "Moving the book-list backups failed");
                    ShowError?.Invoke(string.Format(Strings.Settings_MoveBackupsFailed, ex.Message));
                }
            }
            Settings.BookListBackupDir = dir;
            SettingsService.Save(Settings);
            OnPropertyChanged(nameof(BackupFolder));
        }

        [RelayCommand]
        private void OpenBackupFolder() => Dialogs.OpenFolder(BackupFolder);

        // --- For the Assistant's tools (Assistant/HoboTools.cs) -----------------------------

        /// <summary>The Assistant pane; set by the window.</summary>
        public AssistantViewModel? Assistant { get; set; }

        /// <summary>Every row of the Kobo list, whatever the search box holds.</summary>
        internal IEnumerable<BookRow> AllKoboRows()
        {
            var folderRows = KoboRows.Where(r => r.Source == RowSource.Folder).ToDictionary(r => r.Id, r => r);
            return _storeRows.Values
                .Concat(Reader.Rows)
                .Concat(Send.FolderOnly().Select(f => folderRows.GetValueOrDefault(f.Id) ?? new BookRow(f.Item)))
                .ToList();
        }

        internal Task RefreshForAssistantAsync() => RefreshListAsync();

        /// <summary>Back up store and eReader books, and wait until both are done.</summary>
        internal Task BackUpForAssistantAsync(IReadOnlyList<string> store, IReadOnlyList<string> reader) =>
            Task.WhenAll(
                store.Count > 0 ? QueueDownloadsAsync(store) : Task.CompletedTask,
                reader.Count > 0 ? Reader.BackUpAsync(reader, Settings.DownloadDir) : Task.CompletedTask);

        /// <summary>Move the backup copies of these books to the Recycle Bin; nothing is removed from the eReader.</summary>
        internal Task DeleteBackupsForAssistantAsync(List<string> store, List<string> reader, List<string> folder)
        {
            Error = null;
            return DeleteBooksAsync(new DeleteRequest(store, reader, folder, FolderCopies(store, reader, folder)), removeFromReader: false);
        }

        internal Task EjectForAssistantAsync() => EjectReaderAsync();

        internal Task ScanKindleForAssistantAsync() => Kindle.ScanAsync(StoreTitles(), Settings.KindleExtraDirs, Settings.KindleDir, quiet: false);
    }
}
