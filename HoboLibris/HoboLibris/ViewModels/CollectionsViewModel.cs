using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoboLibris.Models;
using HoboLibris.Resources;
using HoboLibris.Services;

namespace HoboLibris.ViewModels
{
    public enum CollectionFilter
    {
        All,
        NotInAny,
        Several,
        Collection,
    }

    /// <summary>One entry in the "Show" list on the left: a filter or a collection.</summary>
    public partial class ShowEntry : ObservableObject
    {
        public CollectionFilter Filter { get; }
        public string? Collection { get; }

        [ObservableProperty]
        private string _label = "";

        [ObservableProperty]
        private int _count;

        public bool IsBold => Filter == CollectionFilter.NotInAny;

        /// <summary>What a screen reader reads for the entry.</summary>
        public override string ToString() => Label;

        public ShowEntry(CollectionFilter filter, string? collection)
        {
            Filter = filter;
            Collection = collection;
        }
    }

    /// <summary>One book row on the Collections screen.</summary>
    public partial class CollectionRow : ObservableObject
    {
        public CollectionBook Book { get; }
        public string Id => Book.Id;
        public string Title => Book.Title;
        public string Authors => Book.Authors;
        public string FromText => Book.Store ? Strings.Coll_From_Store : Strings.Coll_From_Added;

        /// <summary>What a screen reader reads for the row.</summary>
        public override string ToString() => Title;

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasShelves))]
        private string _shelves = "";

        public bool HasShelves => Shelves.Length > 0;

        public CollectionRow(CollectionBook book)
        {
            Book = book;
        }
    }

    /// <summary>
    /// The Collections screen: see which collection each book is in, show only the books in no
    /// collection yet, add and remove books, create, rename and delete collections, then save it
    /// all back to the eReader in one go.
    /// </summary>
    public partial class CollectionsViewModel : ObservableObject
    {
        public Collections? Coll { get; private set; }

        public ObservableCollection<ShowEntry> ShowEntries { get; } = new();
        public ObservableCollection<CollectionRow> Rows { get; } = new();
        public ObservableCollection<CollectionRow> VisibleRows { get; } = new();
        public ObservableCollection<string> CollectionNames { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLoaded), nameof(IsNotLoaded))]
        private bool _loaded;

        /// <summary>Why the collections couldn't be read or saved.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? _error;

        /// <summary>What the last save did.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDone))]
        private string? _done;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCollectionShown), nameof(ShownCollection), nameof(RemoveFromText), nameof(ShowHideShelved))]
        private ShowEntry? _selectedShow;

        [ObservableProperty]
        private bool _hideShelved;

        [ObservableProperty]
        private string _search = "";

        [ObservableProperty]
        private string _addTo = "";

        [ObservableProperty]
        private string _newName = "";

        [ObservableProperty]
        private string _renameTo = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PendingText), nameof(HasPending))]
        private int _pending;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TickedText), nameof(CanAct))]
        private int _ticked;

        [ObservableProperty]
        private bool _tried;

        public bool IsLoaded => Loaded;
        public bool IsNotLoaded => !Loaded;
        public bool HasError => !string.IsNullOrEmpty(Error);
        public bool HasDone => !string.IsNullOrEmpty(Done);
        public bool HasPending => Pending > 0;
        public string PendingText => Pending == 1 ? Strings.Coll_SaveButtonOne : string.Format(Strings.Coll_SaveButton, Pending);
        public string TickedText => string.Format(Strings.Coll_Ticked, Ticked);
        public bool CanAct => Ticked > 0 && Coll != null && Coll.Current.ContainsKey(AddTo);
        public bool IsCollectionShown => SelectedShow?.Filter == CollectionFilter.Collection;
        public string? ShownCollection => SelectedShow?.Collection;
        public string RemoveFromText => string.Format(Strings.Coll_RemoveFrom, ShownCollection ?? "");
        public bool ShowHideShelved => SelectedShow?.Filter == CollectionFilter.All;
        public string EmptyText
        {
            get
            {
                if (Search.Length > 0)
                {
                    return Strings.Coll_NoMatch;
                }
                return SelectedShow?.Filter switch
                {
                    CollectionFilter.NotInAny => Strings.Coll_EveryBookShelved,
                    CollectionFilter.Several => Strings.Coll_NoneInSeveral,
                    CollectionFilter.Collection => Strings.Coll_EmptyCollection,
                    _ => Strings.Coll_NoMatch,
                };
            }
        }

        public event Action<string>? StatusChanged;

        public CollectionsViewModel()
        {
        }

        partial void OnSelectedShowChanged(ShowEntry? value)
        {
            if (value?.Filter == CollectionFilter.Collection && value.Collection != null)
            {
                RenameTo = value.Collection;
                if (Coll != null && Coll.Current.ContainsKey(value.Collection))
                {
                    AddTo = value.Collection;
                }
            }
            Refilter();
        }

        partial void OnHideShelvedChanged(bool value) => Refilter();
        partial void OnSearchChanged(string value) => Refilter();
        partial void OnAddToChanged(string value) => OnPropertyChanged(nameof(CanAct));

        /// <summary>Read the collections from the connected Kobo eReader.</summary>
        public async Task LoadAsync()
        {
            Tried = true;
            Done = null;
            var root = KoboDeviceService.FirstRoot();
            if (root == null)
            {
                Coll = null;
                Loaded = false;
                Error = Strings.Coll_NoReader;
                Rebuild();
                return;
            }
            try
            {
                Coll = await Task.Run(() => Collections.Load(root));
                Error = null;
                Loaded = true;
                StatusChanged?.Invoke(string.Format(Strings.Status_BooksAndCollections, Coll.Books.Count, Coll.Current.Count));
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Loading collections failed");
                Coll = null;
                Loaded = false;
                Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Coll_CouldNotRead, ex.Message);
            }
            Rebuild();
        }

        /// <summary>Forget the loaded collections (after the eReader was ejected).</summary>
        public void Unload(string? message)
        {
            Coll = null;
            Loaded = false;
            Done = null;
            Error = message;
            Rebuild();
        }

        /// <summary>Write the changes to the eReader.</summary>
        public async Task SaveAsync()
        {
            if (Coll == null)
            {
                return;
            }
            var coll = Coll;
            try
            {
                var backup = await Task.Run(() => coll.Save(AppPaths.BookListBackupDir));
                Error = null;
                Done = Strings.Coll_Saved;
                StatusChanged?.Invoke(string.Format(Strings.Status_CollectionsSaved, backup));
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Saving collections failed");
                Done = null;
                Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Coll_CouldNotSave, ex.Message);
            }
            Rebuild();
        }

        [RelayCommand]
        private void UndoAll()
        {
            Coll?.Discard();
            Done = null;
            Rebuild();
        }

        [RelayCommand]
        private void Create()
        {
            if (Coll == null)
            {
                return;
            }
            try
            {
                Coll.Create(NewName);
                var name = NewName.Trim();
                NewName = "";
                Error = null;
                Rebuild();
                SelectedShow = ShowEntries.FirstOrDefault(e => e.Collection == name);
            }
            catch (HoboLibrisException ex)
            {
                Error = ex.Message;
            }
        }

        [RelayCommand]
        private void Rename()
        {
            if (Coll == null || ShownCollection == null)
            {
                return;
            }
            var old = ShownCollection;
            try
            {
                Coll.Rename(old, RenameTo);
                var newName = RenameTo.Trim();
                if (AddTo == old)
                {
                    AddTo = newName;
                }
                Error = null;
                Rebuild();
                SelectedShow = ShowEntries.FirstOrDefault(e => e.Collection == newName);
            }
            catch (HoboLibrisException ex)
            {
                Error = ex.Message;
            }
        }

        /// <summary>Delete the shown collection. Its books stay on the eReader.</summary>
        public void DeleteShown()
        {
            if (Coll == null || ShownCollection == null)
            {
                return;
            }
            Coll.Delete(ShownCollection);
            Rebuild();
            SelectedShow = ShowEntries.FirstOrDefault(e => e.Filter == CollectionFilter.All);
        }

        private List<string> ChosenIds() => VisibleRows.Where(r => r.IsSelected).Select(r => r.Id).ToList();

        [RelayCommand]
        private void Move()
        {
            if (Coll == null)
            {
                return;
            }
            var chosen = ChosenIds();
            Coll.MoveTo(AddTo, chosen);
            AfterChange(chosen);
        }

        [RelayCommand]
        private void AlsoAdd()
        {
            if (Coll == null)
            {
                return;
            }
            var chosen = ChosenIds();
            Coll.Add(AddTo, chosen);
            AfterChange(chosen);
        }

        [RelayCommand]
        private void RemoveFromShown()
        {
            if (Coll == null || ShownCollection == null)
            {
                return;
            }
            var chosen = ChosenIds();
            Coll.Remove(ShownCollection, chosen);
            AfterChange(chosen);
        }

        private void AfterChange(IEnumerable<string> ids)
        {
            Done = null;
            var set = ids.ToHashSet();
            foreach (var row in Rows.Where(r => set.Contains(r.Id)))
            {
                row.IsSelected = false;
            }
            Rebuild();
        }

        public void TickAllVisible(bool on)
        {
            foreach (var row in VisibleRows)
            {
                row.IsSelected = on;
            }
            UpdateTicked();
        }

        public void UpdateTicked() => Ticked = VisibleRows.Count(r => r.IsSelected);

        /// <summary>Rebuild the Show list, the rows' collection text and the counts after any change.</summary>
        public void Rebuild()
        {
            var keepShow = SelectedShow;
            var selected = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
            ShowEntries.Clear();
            CollectionNames.Clear();
            Rows.Clear();
            if (Coll == null)
            {
                Pending = 0;
                Refilter();
                return;
            }
            var total = Coll.Books.Count;
            var notInAny = Coll.Books.Count(b => !Coll.IsShelved(b.Id));
            var several = Coll.Books.Count(b => Coll.ShelfCount(b.Id) > 1);
            ShowEntries.Add(new ShowEntry(CollectionFilter.All, null) { Label = string.Format(Strings.Coll_Show_All, total), Count = total });
            ShowEntries.Add(new ShowEntry(CollectionFilter.NotInAny, null) { Label = string.Format(Strings.Coll_Show_NotInAny, notInAny), Count = notInAny });
            ShowEntries.Add(new ShowEntry(CollectionFilter.Several, null) { Label = string.Format(Strings.Coll_Show_Several, several), Count = several });
            foreach (var (name, ids) in Coll.Current)
            {
                ShowEntries.Add(new ShowEntry(CollectionFilter.Collection, name) { Label = $"{name} ({ids.Count})", Count = ids.Count });
                CollectionNames.Add(name);
            }
            foreach (var b in Coll.Books)
            {
                Rows.Add(new CollectionRow(b)
                {
                    IsSelected = selected.Contains(b.Id),
                    Shelves = string.Join(", ", Coll.ShelvesOf(b.Id)),
                });
            }
            if (!Coll.Current.ContainsKey(AddTo))
            {
                AddTo = keepShow?.Filter == CollectionFilter.Collection && keepShow.Collection != null && Coll.Current.ContainsKey(keepShow.Collection)
                    ? keepShow.Collection
                    : Coll.Current.Keys.FirstOrDefault() ?? "";
            }
            Pending = Coll.Changes().Count;
            // Keep the same entry selected, by filter and name.
            var again = ShowEntries.FirstOrDefault(e => e.Filter == keepShow?.Filter && e.Collection == keepShow?.Collection)
                        ?? ShowEntries.FirstOrDefault(e => e.Filter == CollectionFilter.NotInAny);
            if (ReferenceEquals(again, SelectedShow))
            {
                Refilter();
            }
            else
            {
                SelectedShow = again;
            }
        }

        private void Refilter()
        {
            VisibleRows.Clear();
            if (Coll != null)
            {
                var q = Search.Trim();
                foreach (var row in Rows)
                {
                    var b = row.Book;
                    var shown = SelectedShow?.Filter switch
                    {
                        CollectionFilter.NotInAny => !Coll.IsShelved(b.Id),
                        CollectionFilter.Several => Coll.ShelfCount(b.Id) > 1,
                        CollectionFilter.Collection => SelectedShow.Collection != null && Coll.Current.TryGetValue(SelectedShow.Collection, out var s) && s.Contains(b.Id),
                        _ => !(HideShelved && Coll.IsShelved(b.Id)),
                    };
                    if (shown && (q.Length == 0
                                  || b.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                  || b.Authors.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                    {
                        VisibleRows.Add(row);
                    }
                }
            }
            UpdateTicked();
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(CanAct));
        }
    }
}
