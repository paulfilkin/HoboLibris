using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoboLibris.Resources;
using HoboLibris.Services;

namespace HoboLibris.ViewModels
{
    /// <summary>One preview on the Previews screen.</summary>
    public partial class PreviewRow : ObservableObject
    {
        public Preview Preview { get; }
        public string Id => Preview.Id;
        public string Title => Preview.Title;
        public string Author => Preview.Author;
        public string ArrivedText => Preview.Arrived?.ToLocalTime().ToString("d") ?? "";
        public string SizeText => Preview.Size > 0 ? string.Format(Strings.Prev_SizeMB, Preview.Size / 1048576.0) : "";

        /// <summary>Where the preview came from, or that its removal waits for the sync (#61).</summary>
        public string KindText => Preview.Removing ? Strings.Prev_RemovingSaved : Preview.Kind switch
        {
            PreviewKind.Saved => Strings.Prev_KindSaved,
            PreviewKind.SearchResult => Strings.Prev_KindSearch,
            _ => Strings.Prev_KindNotSaved,
        };

        public string KindTip => Preview.Kind switch
        {
            PreviewKind.Saved => Strings.Prev_KindSavedTip,
            PreviewKind.SearchResult => Strings.Prev_KindSearchTip,
            _ => Strings.Prev_KindNotSavedTip,
        };

        /// <summary>What a screen reader reads for the row.</summary>
        public override string ToString() => Title;

        [ObservableProperty]
        private bool _isSelected;

        /// <summary>On the wishlist in the account.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WishText), nameof(CanWish))]
        private bool _onWishlist;

        /// <summary>Added on the eReader, waiting for its next sync.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WishText), nameof(CanWish))]
        private bool _adding;

        public string WishText => Adding ? Strings.Prev_Adding : OnWishlist ? Strings.Prev_OnWishlist : "";

        /// <summary>Can be added to the wishlist.</summary>
        public bool CanWish => !OnWishlist && !Adding && Preview.CrossRevisionId.Length > 0;

        public event Action? SelectionChanged;

        partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

        public PreviewRow(Preview preview)
        {
            Preview = preview;
        }
    }

    /// <summary>One book on the wishlist on the Previews screen.</summary>
    public partial class WishRow : ObservableObject
    {
        public WishlistItem Item { get; }
        public string Title => Item.Title;
        public string Author => Item.Author;
        public string PriceText => Item.PreOrder && Item.PriceText.Length > 0
            ? string.Format(Strings.Wish_PreOrderPrice, Item.PriceText)
            : Item.PriceText;
        public string AddedText => Item.Added?.ToLocalTime().ToString("d") ?? "";

        /// <summary>What a screen reader reads for the row.</summary>
        public override string ToString() => Title;

        [ObservableProperty]
        private bool _isSelected;

        /// <summary>Removed on the eReader, waiting for its next sync.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanTick), nameof(RemovingText))]
        private bool _removing;

        public string RemovingText => Removing ? Strings.Wish_Removing : "";
        public bool CanTick => !Removing && Item.CrossRevisionId.Length > 0;

        public event Action? SelectionChanged;

        partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

        public WishRow(WishlistItem item)
        {
            Item = item;
        }
    }

    /// <summary>
    /// The Previews screen (#59): the store previews on the Kobo eReader, which can be opened in the
    /// store or added to the wishlist, and the wishlist in the Kobo account, whose books can be removed.  The wishlist is changed through the eReader, which sends the changes when it next syncs.
    /// Saved previews and previews from a search can be removed (#61).
    /// </summary>
    public partial class PreviewsViewModel : ObservableObject
    {
        /// <summary>The connected eReader's drive root, or null.</summary>
        public Func<string?> FindReader { get; set; } = KoboDeviceService.FirstRoot;

        /// <summary>Where the eReader's book list is copied before it is changed; set from the settings.</summary>
        public Func<string> BackupFolder { get; set; } = () => AppPaths.BookListBackupDir;

        /// <summary>Reads the wishlist from the Kobo account; null when signed out.  Set by the main view model.</summary>
        public Func<CancellationToken, Task<List<WishlistItem>>?>? FetchWishlist { get; set; }

        /// <summary>Asks the person before the ticked books are removed from the wishlist; set by the window.</summary>
        public Func<IReadOnlyList<WishRow>, bool>? ConfirmWishRemove { get; set; }

        /// <summary>Asks the person before the ticked previews are removed; set by the window.</summary>
        public Func<IReadOnlyList<PreviewRow>, bool>? ConfirmRemove { get; set; }

        /// <summary>Opens a web address; replaced in tests.</summary>
        public Action<string> OpenUrl { get; set; } = Start;

        public ObservableCollection<PreviewRow> Rows { get; } = new();
        public ObservableCollection<WishRow> WishRows { get; } = new();

        /// <summary>The wishlist as last read from the account.</summary>
        private List<WishlistItem> _wishItems = new();

        /// <summary>The wishlist changes waiting on the eReader for its next sync (true for an add).</summary>
        private Dictionary<string, bool> _waiting = new(StringComparer.OrdinalIgnoreCase);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowNoReader), nameof(ShowWishNeedsReader))]
        private bool _tried;

        /// <summary>The eReader was found and its previews read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowNoReader), nameof(CanAddWish), nameof(CanRemoveWish), nameof(CanRemove), nameof(EmptyText), nameof(ShowWishNeedsReader))]
        private bool _readerFound;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAddWish), nameof(CanRemoveWish), nameof(CanRemove))]
        private bool _busy;

        /// <summary>Ticked previews that can be removed (#61).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanRemove), nameof(RemoveText))]
        private int _tickedForRemove;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? _error;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDone))]
        private string? _done;

        /// <summary>Ticked previews that can be added to the wishlist.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAddWish), nameof(AddWishText))]
        private int _tickedForWish;

        /// <summary>Ticked books on the wishlist.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanRemoveWish), nameof(RemoveWishText))]
        private int _wishTicked;

        /// <summary>The wishlist is being read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WishEmptyText))]
        private bool _wishBusy;

        /// <summary>Signed out of the Kobo account, so the wishlist can't be read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WishEmptyText))]
        private bool _wishSignedOut;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWishError), nameof(WishEmptyText))]
        private string? _wishError;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWishDone))]
        private string? _wishDone;

        public bool HasError => !string.IsNullOrEmpty(Error);
        public bool HasDone => !string.IsNullOrEmpty(Done);
        public bool HasWishError => !string.IsNullOrEmpty(WishError);
        public bool HasWishDone => !string.IsNullOrEmpty(WishDone);
        public bool ShowNoReader => Tried && !ReaderFound;
        public bool ShowWishNeedsReader => Tried && !ReaderFound && WishRows.Count > 0;
        public bool CanAddWish => ReaderFound && !Busy && TickedForWish > 0;
        public bool CanRemoveWish => ReaderFound && !Busy && WishTicked > 0;
        public bool CanRemove => ReaderFound && !Busy && TickedForRemove > 0;
        public string AddWishText => string.Format(Strings.Prev_AddToWishlist, TickedForWish);
        public string RemoveText => string.Format(Strings.Prev_Remove, TickedForRemove);
        public string RemoveWishText => string.Format(Strings.Wish_Remove, WishTicked);
        public string EmptyText => ReaderFound ? Strings.Prev_None : "";
        public string WishEmptyText => WishBusy ? Strings.Wish_Reading
            : WishSignedOut ? Strings.Wish_SignedOut
            : HasWishError ? ""
            : Strings.Wish_None;

        public event Action<string>? StatusChanged;

        /// <summary>Read the previews and waiting wishlist changes on the eReader (when it is connected), and the wishlist when asked.</summary>
        public async Task LoadAsync(bool wishlist = true)
        {
            Tried = true;
            Error = null;
            Done = null;
            var reading = wishlist ? LoadWishlistAsync() : Task.CompletedTask;
            var root = FindReader();
            List<Preview> found = new();
            var waiting = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var ok = false;
            if (root != null)
            {
                try
                {
                    (found, waiting) = await Task.Run(() => (Previews.Read(root), Wishlist.Waiting(KoboDeviceService.DatabasePath(root))));
                    ok = true;
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex, "Reading the previews failed");
                    Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Coll_CouldNotRead, ex.Message);
                }
            }
            ReaderFound = ok;
            _waiting = waiting;
            Rebuild(found);
            if (ok)
            {
                StatusChanged?.Invoke(string.Format(Strings.Status_Previews, found.Count));
            }
            await reading;
            ApplyWishlist();
        }

        /// <summary>Read the wishlist from the Kobo account.</summary>
        public async Task LoadWishlistAsync()
        {
            WishError = null;
            WishDone = null;
            var fetch = FetchWishlist?.Invoke(CancellationToken.None);
            WishSignedOut = fetch == null;
            if (fetch == null)
            {
                _wishItems = new();
                ApplyWishlist();
                return;
            }
            WishBusy = true;
            try
            {
                _wishItems = (await fetch)
                    .OrderByDescending(i => i.Added ?? DateTime.MinValue)
                    .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Reading the wishlist failed");
                _wishItems = new();
                WishError = string.Format(Strings.Wish_CouldNotRead, ex.Message);
            }
            finally
            {
                WishBusy = false;
            }
            ApplyWishlist();
        }

        private void Rebuild(List<Preview> found)
        {
            var selected = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
            foreach (var row in Rows)
            {
                row.SelectionChanged -= UpdateTicked;
            }
            Rows.Clear();
            foreach (var preview in found)
            {
                var row = new PreviewRow(preview) { IsSelected = selected.Contains(preview.Id) };
                row.SelectionChanged += UpdateTicked;
                Rows.Add(row);
            }
            ApplyWishlist();
            OnPropertyChanged(nameof(EmptyText));
        }

        /// <summary>
        /// Show the wishlist, with the eReader's waiting changes: a book being removed stays listed until
        /// the eReader's sync takes it off, and a preview being added says so.
        /// </summary>
        private void ApplyWishlist()
        {
            var selected = WishRows.Where(r => r.IsSelected).Select(r => r.Item.CrossRevisionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in WishRows)
            {
                row.SelectionChanged -= UpdateWishTicked;
            }
            WishRows.Clear();
            foreach (var item in _wishItems)
            {
                var removing = _waiting.TryGetValue(item.CrossRevisionId, out var add) && !add;
                var row = new WishRow(item) { Removing = removing };
                row.IsSelected = row.CanTick && selected.Contains(item.CrossRevisionId);
                row.SelectionChanged += UpdateWishTicked;
                WishRows.Add(row);
            }
            var onList = _wishItems.Select(i => i.CrossRevisionId).Where(c => c.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Rows)
            {
                var cross = row.Preview.CrossRevisionId;
                row.Adding = _waiting.TryGetValue(cross, out var add) && add;
                row.OnWishlist = onList.Contains(cross) && !(_waiting.TryGetValue(cross, out var a) && !a);
            }
            UpdateTicked();
            UpdateWishTicked();
            OnPropertyChanged(nameof(ShowWishNeedsReader));
        }

        public void UpdateTicked()
        {
            TickedForWish = Rows.Count(r => r.IsSelected && r.CanWish);
            TickedForRemove = Rows.Count(r => r.IsSelected && r.Preview.CanRemove);
        }

        public void UpdateWishTicked() => WishTicked = WishRows.Count(r => r.IsSelected && r.CanTick);

        public void TickAll(bool on)
        {
            foreach (var row in Rows)
            {
                row.IsSelected = on;
            }
            UpdateTicked();
        }

        public void TickAllWish(bool on)
        {
            foreach (var row in WishRows)
            {
                row.IsSelected = on && row.CanTick;
            }
            UpdateWishTicked();
        }

        [RelayCommand]
        private Task AddTickedToWishlist() => AddToWishlistAsync(Rows.Where(r => r.IsSelected && r.CanWish).ToList());

        /// <summary>Add these previews to the wishlist through the eReader.  Returns those added; Error says why when it failed.</summary>
        public async Task<List<PreviewRow>> AddToWishlistAsync(IReadOnlyList<PreviewRow> rows)
        {
            var targets = rows.Where(r => r.CanWish).ToList();
            if (!await ChangeWishlistAsync(targets.Select(r => (r.Preview.CrossRevisionId, true)).ToList(), forPreviews: true))
            {
                return new List<PreviewRow>();
            }
            foreach (var row in targets)
            {
                row.IsSelected = false;
            }
            UpdateTicked();
            Done = string.Format(Strings.Prev_Added, targets.Count);
            StatusChanged?.Invoke(Done);
            return targets;
        }

        [RelayCommand]
        private async Task RemoveTicked()
        {
            var rows = Rows.Where(r => r.IsSelected && r.Preview.CanRemove).ToList();
            if (rows.Count == 0 || ConfirmRemove?.Invoke(rows) != true)
            {
                return;
            }
            await RemoveAsync(rows);
        }

        /// <summary>The confirmation for removing these previews: how many, their titles, and what happens to each kind.</summary>
        public static string RemovePrompt(IReadOnlyList<PreviewRow> rows) =>
            string.Format(Strings.Prev_RemoveConfirm, rows.Count) + "\n\n"
            + string.Join("\n", rows.Take(10).Select(r => "• " + r.Title)) + (rows.Count > 10 ? "\n…" : "")
            + "\n\n" + Strings.Prev_RemoveNote;

        /// <summary>
        /// Remove these previews from the eReader (#61): saved ones leave the account at the next sync, and
        /// ones from a search are removed now.  Others are left alone.  Returns those removed; Error says
        /// why when it failed.
        /// </summary>
        public async Task<List<PreviewRow>> RemoveAsync(IReadOnlyList<PreviewRow> rows)
        {
            var targets = rows.Where(r => r.Preview.CanRemove).ToList();
            var root = FindReader();
            if (root == null)
            {
                Error = Strings.Prev_NoReader;
                return new List<PreviewRow>();
            }
            if (targets.Count == 0)
            {
                return targets;
            }
            Busy = true;
            Done = null;
            Error = null;
            try
            {
                var backup = await Task.Run(() => Previews.Remove(root, targets.Select(r => r.Preview).ToList(), BackupFolder()));
                LogManager.Info($"Removed {targets.Count} previews from the eReader; book list copied to {backup}");
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Removing previews failed");
                Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Prev_CouldNotRemove, ex.Message);
                return new List<PreviewRow>();
            }
            finally
            {
                Busy = false;
            }
            foreach (var row in targets)
            {
                row.IsSelected = false;
            }
            await LoadAsync(wishlist: false);
            Done = string.Format(Strings.Prev_Removed, targets.Count);
            StatusChanged?.Invoke(Done);
            return targets;
        }

        [RelayCommand]
        private async Task RemoveTickedFromWishlist()
        {
            var rows = WishRows.Where(r => r.IsSelected && r.CanTick).ToList();
            if (rows.Count == 0 || ConfirmWishRemove?.Invoke(rows) != true)
            {
                return;
            }
            await RemoveFromWishlistAsync(rows);
        }

        /// <summary>The confirmation for removing these books from the wishlist: how many, their titles, and when it happens.</summary>
        public static string RemoveWishPrompt(IReadOnlyList<WishRow> rows) =>
            string.Format(Strings.Wish_RemoveConfirm, rows.Count) + "\n\n"
            + string.Join("\n", rows.Take(10).Select(r => "• " + r.Title)) + (rows.Count > 10 ? "\n…" : "")
            + "\n\n" + Strings.Wish_RemoveNote;

        /// <summary>Remove these books from the wishlist through the eReader.  Returns those removed; WishError says why when it failed.</summary>
        public async Task<List<WishRow>> RemoveFromWishlistAsync(IReadOnlyList<WishRow> rows)
        {
            var targets = rows.Where(r => r.CanTick).ToList();
            if (!await ChangeWishlistAsync(targets.Select(r => (r.Item.CrossRevisionId, false)).ToList(), forPreviews: false))
            {
                return new List<WishRow>();
            }
            WishDone = string.Format(Strings.Wish_Removed, targets.Count);
            StatusChanged?.Invoke(WishDone);
            return targets;
        }

        /// <summary>Leave wishlist changes on the eReader for its next sync, then show them.</summary>
        private async Task<bool> ChangeWishlistAsync(List<(string, bool)> changes, bool forPreviews)
        {
            void Fail(string message)
            {
                if (forPreviews)
                {
                    Error = message;
                }
                else
                {
                    WishError = message;
                }
            }

            var root = FindReader();
            if (root == null)
            {
                Fail(Strings.Wish_NeedsReader);
                return false;
            }
            if (changes.Count == 0)
            {
                return false;
            }
            Busy = true;
            Done = null;
            Error = null;
            WishDone = null;
            WishError = null;
            try
            {
                var backup = await Task.Run(() => Wishlist.Change(root, changes, BackupFolder()));
                LogManager.Info($"Left {changes.Count} wishlist changes on the eReader for its next sync; book list copied to {backup}");
                _waiting = await Task.Run(() => Wishlist.Waiting(KoboDeviceService.DatabasePath(root)));
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Changing the wishlist failed");
                Fail(ex is HoboLibrisException ? ex.Message : string.Format(Strings.Wish_CouldNotChange, ex.Message));
                return false;
            }
            finally
            {
                Busy = false;
            }
            ApplyWishlist();
            return true;
        }

        [RelayCommand]
        private Task LookAgain() => LoadAsync();

        [RelayCommand]
        private void OpenStore(PreviewRow? row)
        {
            if (row != null)
            {
                OpenUrl(row.Preview.StoreUrl);
            }
        }

        [RelayCommand]
        private void OpenWishPage(WishRow? row)
        {
            if (row != null)
            {
                OpenUrl(row.Item.StoreUrl);
            }
        }

        private static void Start(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not open {target}: {ex.Message}");
            }
        }
    }
}
