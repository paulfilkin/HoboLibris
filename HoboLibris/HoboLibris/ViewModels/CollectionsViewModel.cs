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

    /// <summary>One way to settle a conflict: a radio button.</summary>
    public partial class ConflictChoice : ObservableObject
    {
        public string Group { get; }
        public string Value { get; }
        public string Label { get; }

        [ObservableProperty]
        private bool _isChosen;

        public event Action? Changed;

        partial void OnIsChosenChanged(bool value) => Changed?.Invoke();

        public ConflictChoice(string group, string value, string label)
        {
            Group = group;
            Value = value;
            Label = label;
        }
    }

    /// <summary>Something the person decides before a merge is finished, with its choices.</summary>
    public class ConflictRow
    {
        public string Key { get; }
        public string Text { get; }
        public List<ConflictChoice> Choices { get; }
        public string? Chosen => Choices.FirstOrDefault(c => c.IsChosen)?.Value;

        public ConflictRow(string key, string text, IEnumerable<MergeOption> options)
        {
            Key = key;
            Text = text;
            Choices = options.Select(o => new ConflictChoice(key, o.Value, o.Label)).ToList();
        }
    }

    /// <summary>
    /// The Collections screen: see which collection each book is in, show only the books in no
    /// collection yet, add and remove books, create, rename and delete collections, then save it
    /// all back to the eReader in one go.
    ///
    /// The collections are kept on this computer with the changes (#32).  With no eReader they open
    /// offline from there, and saving waits; when it is plugged in again, the changes are merged with
    /// the eReader as it is now, and anything that clashes is put to the person first.
    /// </summary>
    public partial class CollectionsViewModel : ObservableObject
    {
        public Collections? Coll { get; private set; }

        /// <summary>Where the eReader's book list is copied before saving; set from the settings.</summary>
        public Func<string> BackupFolder { get; set; } = () => AppPaths.BookListBackupDir;

        /// <summary>Where the collections and their changes are kept on this computer.</summary>
        public CollectionsStore Store { get; set; } = new(AppPaths.CollectionsDir);

        /// <summary>The connected eReader's drive root, or null.</summary>
        public Func<string?> FindReader { get; set; } = KoboDeviceService.FirstRoot;

        /// <summary>What the person has to decide before a merge is finished.</summary>
        public ObservableCollection<ConflictRow> ConflictRows { get; } = new();

        /// <summary>The value of the choice that applies the changes for another eReader to this one.</summary>
        public const string ApplyHere = "apply";
        public const string KeepForOther = "keep";
        public const string DiscardThem = "discard";

        /// <summary>During a merge: the collections with the changes, and the eReader as it is now.</summary>
        private Collections? _here, _there;
        /// <summary>Changes waiting for another eReader than the one plugged in, while the person decides.</summary>
        private Collections? _other;
        /// <summary>The other eReader's collections, whose changes go once the merge here is finished.</summary>
        private Collections? _clearAfter;
        private readonly Dictionary<string, string> _choices = new();

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
        [NotifyPropertyChangedFor(nameof(PendingText), nameof(HasPending), nameof(CanSave))]
        private int _pending;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotLoaded), nameof(CanDiscardMine))]
        private bool _hasConflicts;

        /// <summary>Every conflict has a choice.</summary>
        [ObservableProperty]
        private bool _allChosen;

        [ObservableProperty]
        private string _conflictsIntro = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TickedText), nameof(CanAct))]
        private int _ticked;

        [ObservableProperty]
        private bool _tried;

        public bool IsLoaded => Loaded;
        public bool IsNotLoaded => !Loaded && !HasConflicts;
        public bool HasError => !string.IsNullOrEmpty(Error);
        public bool HasDone => !string.IsNullOrEmpty(Done);
        public bool HasPending => Pending > 0;
        public bool IsOffline => Coll?.Offline == true;
        public bool CanSave => HasPending && !IsOffline;
        public bool CanDiscardMine => HasConflicts && _other == null;
        public string OfflineText => Coll == null ? "" : string.Format(Strings.Coll_OfflineBanner, Coll.Taken.ToLocalTime().ToString("f"));
        public string PendingText => IsOffline
            ? Pending == 1 ? Strings.Coll_WaitingButtonOne : string.Format(Strings.Coll_WaitingButton, Pending)
            : Pending == 1 ? Strings.Coll_SaveButtonOne : string.Format(Strings.Coll_SaveButton, Pending);
        public string TickedText => string.Format(Strings.Coll_Ticked, Ticked);
        // AddTo is null when the list of collections was emptied under the combo box (the conflict card, #32).
        public bool CanAct => Ticked > 0 && Coll != null && AddTo != null && Coll.Current.ContainsKey(AddTo);
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

        /// <summary>
        /// Read the collections from the connected Kobo eReader, and merge any changes waiting for it.
        /// With no eReader, open the collections kept on this computer, offline.
        /// </summary>
        public async Task LoadAsync()
        {
            if (HasConflicts)
            {
                return;
            }
            Tried = true;
            Done = null;
            var root = FindReader();
            if (root == null)
            {
                OpenOffline(null);
                return;
            }
            Collections there;
            try
            {
                there = await Task.Run(() => Collections.Load(root));
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Loading collections failed");
                Coll?.GoOffline();
                Loaded = Coll != null;
                Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Coll_CouldNotRead, ex.Message);
                Rebuild();
                return;
            }
            StatusChanged?.Invoke(string.Format(Strings.Status_BooksAndCollections, there.Books.Count, there.Current.Count));

            // Changes waiting for another eReader: the person decides what happens to them.
            var waiting = Coll ?? Store.Latest();
            if (waiting != null && waiting.Changes().Count > 0 && !SameReader(waiting, there))
            {
                _other = waiting;
                _there = there;
                ShowConflicts(new[]
                {
                    new ConflictRow("other", string.Format(Strings.Coll_ConflictOtherReader, waiting.Taken.ToLocalTime().ToString("f")), new[]
                    {
                        new MergeOption(ApplyHere, Strings.Coll_ApplyHere),
                        new MergeOption(KeepForOther, Strings.Coll_KeepForOther),
                        new MergeOption(DiscardThem, Strings.Coll_DiscardThem),
                    }),
                }, waiting.Taken);
                return;
            }
            MergeWith(Coll != null && SameReader(Coll, there) ? Coll : Store.Load(there.Serial), there);
        }

        /// <summary>True unless both serial numbers are known and differ.</summary>
        private static bool SameReader(Collections a, Collections b) => a.Serial == null || b.Serial == null || a.Serial == b.Serial;

        /// <summary>The same collections with the same books.</summary>
        private static bool SameCollections(IReadOnlyDictionary<string, SortedSet<string>> a, IReadOnlyDictionary<string, SortedSet<string>> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other.SetEquals(kv.Value));

        /// <summary>Merge the changes made here (if any) with the eReader as it is now.</summary>
        private void MergeWith(Collections? here, Collections there)
        {
            if (here == null || here.Changes().Count == 0)
            {
                Accept(there, null);
                return;
            }
            _here = here;
            _there = there;
            _choices.Clear();
            Remerge();
        }

        private void Remerge()
        {
            var result = CollectionsMerge.Merge(_here!, _there!, _choices);
            if (result.Conflicts.Count > 0)
            {
                ShowConflicts(result.Conflicts.Select(c => new ConflictRow(c.Key, c.Text, c.Options)), _here!.Taken);
                return;
            }
            var changedThere = !SameCollections(_here!.Original, _there!.Original);
            var there = _there!;
            there.Apply(result.Collections, result.Renamed);
            Accept(there, changedThere ? Strings.Coll_Merged : null);
        }

        private void ShowConflicts(IEnumerable<ConflictRow> rows, DateTime taken)
        {
            ConflictRows.Clear();
            foreach (var row in rows)
            {
                foreach (var choice in row.Choices)
                {
                    choice.Changed += UpdateAllChosen;
                }
                ConflictRows.Add(row);
            }
            ConflictsIntro = string.Format(Strings.Coll_ConflictsIntro, taken.ToLocalTime().ToString("f"));
            Coll = null;
            Loaded = false;
            Error = null;
            HasConflicts = true;
            OnPropertyChanged(nameof(CanDiscardMine));
            UpdateAllChosen();
            Rebuild();
        }

        private void UpdateAllChosen() => AllChosen = ConflictRows.Count > 0 && ConflictRows.All(r => r.Chosen != null);

        /// <summary>The merge is finished (or wasn't needed): show these collections and keep them.</summary>
        private void Accept(Collections c, string? message)
        {
            _here = null;
            _there = null;
            _other = null;
            _choices.Clear();
            ConflictRows.Clear();
            HasConflicts = false;
            AllChosen = false;
            if (_clearAfter != null && !ReferenceEquals(_clearAfter, c))
            {
                _clearAfter.Discard();
                Store.Save(_clearAfter);
            }
            _clearAfter = null;
            Coll = c;
            Error = null;
            Loaded = true;
            Done = message;
            Rebuild();
        }

        /// <summary>Go on with the choices made for the conflicts.</summary>
        [RelayCommand]
        private void ApplyChoices()
        {
            if (!AllChosen || _there == null)
            {
                return;
            }
            if (_other != null)
            {
                var other = _other;
                var there = _there;
                var choice = ConflictRows[0].Chosen;
                _other = null;
                HasConflicts = false;
                ConflictRows.Clear();
                switch (choice)
                {
                    case ApplyHere:
                        // Merged with this eReader; the other's file loses them once that is finished.
                        _clearAfter = other;
                        MergeWith(other, there);
                        break;
                    case DiscardThem:
                        other.Discard();
                        Store.Save(other);
                        MergeWith(Store.Load(there.Serial), there);
                        break;
                    default:
                        MergeWith(Store.Load(there.Serial), there);
                        break;
                }
                return;
            }
            foreach (var row in ConflictRows)
            {
                _choices[row.Key] = row.Chosen!;
            }
            Remerge();
        }

        /// <summary>Throw away the changes made here and take the eReader's collections as they are.</summary>
        [RelayCommand]
        private void DiscardMine()
        {
            if (_there != null && _other == null)
            {
                Accept(_there, null);
            }
        }

        /// <summary>Show the collections kept on this computer, offline: the eReader isn't connected.</summary>
        private void OpenOffline(string? message)
        {
            if (HasConflicts)
            {
                // Unplugged before the conflicts were settled: back to the changes as they were.
                var back = _here ?? _other;
                _here = null;
                _there = null;
                _other = null;
                _clearAfter = null;
                _choices.Clear();
                ConflictRows.Clear();
                HasConflicts = false;
                Coll = back;
            }
            Coll ??= Store.Latest();
            if (Coll == null)
            {
                Loaded = false;
                Done = null;
                Error = message ?? Strings.Coll_NoReader;
                Rebuild();
                return;
            }
            Coll.GoOffline();
            Loaded = true;
            Error = null;
            Done = message;
            Rebuild();
        }

        /// <summary>The eReader was ejected or unplugged: go on offline, keeping the changes.</summary>
        public void Unload(string? message)
        {
            if (Tried)
            {
                OpenOffline(message);
            }
        }

        /// <summary>Write the changes to the eReader.</summary>
        public async Task SaveAsync()
        {
            if (Coll == null || Coll.Offline)
            {
                return;
            }
            if (Coll.ChangedOnDevice())
            {
                // The eReader's book list changed since it was read: merge, and save only when its
                // collections are as they were (something else in the book list changed).
                var before = Coll.Original.ToDictionary(kv => kv.Key, kv => new SortedSet<string>(kv.Value, StringComparer.Ordinal), Collections.NameComparer);
                await LoadAsync();
                if (Coll == null || HasConflicts || Coll.Offline)
                {
                    return;
                }
                if (!SameCollections(before, Coll.Original))
                {
                    Done = Strings.Coll_MergedChanged;
                    return;
                }
                if (Coll.Changes().Count == 0)
                {
                    return;
                }
            }
            var coll = Coll;
            try
            {
                var backup = await Task.Run(() => coll.Save(BackupFolder()));
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
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(OfflineText));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(PendingText));
            if (Coll == null)
            {
                Pending = 0;
                Refilter();
                return;
            }
            // Keep the collections and the changes on this computer, for working offline.
            Store.Save(Coll);
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
            if (AddTo == null || !Coll.Current.ContainsKey(AddTo))
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
