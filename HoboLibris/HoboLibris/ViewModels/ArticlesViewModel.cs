using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoboLibris.Resources;
using HoboLibris.Services;

namespace HoboLibris.ViewModels
{
    /// <summary>One row on the Articles screen: an article on the eReader, a kept copy, or both.</summary>
    public partial class ArticleRow : ObservableObject
    {
        public string Id { get; }
        public string Title { get; }
        public string Author { get; }
        public string Url { get; }
        public string Site => Articles.SiteOf(Url);
        public DateTime? Added { get; }
        public string AddedText => Added?.ToLocalTime().ToString("d") ?? "";

        /// <summary>The article as on the eReader; null for a copy whose article has gone from it.</summary>
        public Article? OnReader { get; }
        public bool IsOnReader => OnReader != null;
        public string ProgressText => OnReader == null ? "" : OnReader.Finished ? Strings.Art_Finished : $"{OnReader.Percent}%";
        public bool CanOpenPage => Site.Length > 0;

        /// <summary>What a screen reader reads for the row.</summary>
        public override string ToString() => Title;

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsKept), nameof(KeptText))]
        private string? _keptPath;

        /// <summary>Why keeping it failed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? _error;

        public bool IsKept => KeptPath != null;
        public bool HasError => Error != null;
        public string KeptText => !IsKept ? "" : IsOnReader ? Strings.Art_Kept : Strings.Art_OnlyHere;

        public event Action? SelectionChanged;

        partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

        public ArticleRow(Article onReader, string? keptPath)
        {
            Id = onReader.Id;
            Title = onReader.Title;
            Author = onReader.Author;
            Url = onReader.Url;
            Added = onReader.Added;
            OnReader = onReader;
            _keptPath = keptPath;
        }

        public ArticleRow(KeptArticle kept)
        {
            Id = kept.Id;
            Title = kept.Title;
            Author = kept.Author;
            Url = kept.Url;
            Added = kept.Added;
            _keptPath = kept.Path;
        }
    }

    /// <summary>
    /// The Articles screen (#53): the articles saved to the Kobo eReader with Instapaper, and the copies
    /// kept on this computer, in one list.  Ticked articles are kept as a web page each in the Articles
    /// folder, and a copy stays in the list after its article has gone from the eReader.
    /// </summary>
    public partial class ArticlesViewModel : ObservableObject
    {
        /// <summary>Where kept copies go; set from the settings.</summary>
        public Func<string> Folder { get; set; } = () => AppPaths.DefaultArticlesDir;

        /// <summary>The connected eReader's drive root, or null.</summary>
        public Func<string?> FindReader { get; set; } = KoboDeviceService.FirstRoot;

        /// <summary>Where the eReader's book list is copied before articles are removed; set from the settings.</summary>
        public Func<string> BackupFolder { get; set; } = () => AppPaths.BookListBackupDir;

        /// <summary>Asks the person before the ticked articles are removed from the eReader; set by the window.</summary>
        public Func<IReadOnlyList<ArticleRow>, bool>? ConfirmRemove { get; set; }

        public ObservableCollection<ArticleRow> Rows { get; } = new();
        public ObservableCollection<ArticleRow> VisibleRows { get; } = new();

        [ObservableProperty]
        private bool _tried;

        /// <summary>The eReader was found and its articles read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowNoReader), nameof(CanKeep), nameof(EmptyText))]
        private bool _readerFound;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanKeep))]
        private bool _busy;

        [ObservableProperty]
        private string _search = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? _error;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDone))]
        private string? _done;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(KeepText), nameof(CanKeep), nameof(RemoveText))]
        private int _ticked;

        [ObservableProperty]
        private string _folderPath = "";

        public bool HasError => !string.IsNullOrEmpty(Error);
        public bool HasDone => !string.IsNullOrEmpty(Done);
        public bool ShowNoReader => Tried && !ReaderFound;
        public bool CanKeep => ReaderFound && !Busy && Ticked > 0;
        public string KeepText => string.Format(Strings.Art_KeepTicked, Ticked);
        public string RemoveText => string.Format(Strings.Art_Remove, Ticked);
        public string EmptyText => Search.Trim().Length > 0 ? Strings.Coll_NoMatch : ReaderFound ? Strings.Art_None : Strings.Art_NoneKept;

        public event Action<string>? StatusChanged;

        partial void OnSearchChanged(string value) => Refilter();

        /// <summary>Read the articles on the eReader (when it is connected) and the copies in the Articles folder.</summary>
        public async Task LoadAsync()
        {
            Tried = true;
            Error = null;
            Done = null;
            var root = FindReader();
            var dir = Folder();
            FolderPath = dir;
            List<Article> onReader = new();
            var found = false;
            if (root != null)
            {
                try
                {
                    onReader = await Task.Run(() => Articles.Read(root));
                    found = true;
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex, "Reading the articles failed");
                    Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Coll_CouldNotRead, ex.Message);
                }
            }
            var kept = await Task.Run(() => Articles.Kept(dir));
            ReaderFound = found;
            Rebuild(onReader, kept);
            if (found)
            {
                StatusChanged?.Invoke(string.Format(Strings.Status_Articles, onReader.Count));
            }
        }

        private void Rebuild(List<Article> onReader, List<KeptArticle> kept)
        {
            var selected = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
            foreach (var row in Rows)
            {
                row.SelectionChanged -= UpdateTicked;
            }
            Rows.Clear();
            var keptById = kept.GroupBy(k => k.Id).ToDictionary(g => g.Key, g => g.First());
            var rows = onReader.Select(a => new ArticleRow(a, keptById.GetValueOrDefault(a.Id)?.Path)).ToList();
            var shown = onReader.Select(a => a.Id).ToHashSet();
            rows.AddRange(keptById.Values.Where(k => !shown.Contains(k.Id)).Select(k => new ArticleRow(k)));
            foreach (var row in rows.OrderByDescending(r => r.Added ?? DateTime.MinValue).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                row.IsSelected = row.IsOnReader && selected.Contains(row.Id);
                row.SelectionChanged += UpdateTicked;
                Rows.Add(row);
            }
            Refilter();
        }

        private void Refilter()
        {
            var q = Search.Trim();
            VisibleRows.Clear();
            foreach (var row in Rows)
            {
                if (q.Length == 0
                    || row.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || row.Author.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || row.Site.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                {
                    VisibleRows.Add(row);
                }
            }
            UpdateTicked();
            OnPropertyChanged(nameof(EmptyText));
        }

        public void UpdateTicked() => Ticked = VisibleRows.Count(r => r.IsSelected && r.IsOnReader);

        public void TickAllVisible(bool on)
        {
            foreach (var row in VisibleRows)
            {
                row.IsSelected = on && row.IsOnReader;
            }
            UpdateTicked();
        }

        [RelayCommand]
        private Task KeepTicked() => KeepAsync(VisibleRows.Where(r => r.IsSelected && r.IsOnReader).ToList());

        /// <summary>Keep copies of these articles in the Articles folder.  Returns those kept and those that failed, with why.</summary>
        public async Task<(List<ArticleRow> Kept, List<ArticleRow> Failed)> KeepAsync(IReadOnlyList<ArticleRow> rows)
        {
            var kept = new List<ArticleRow>();
            var failed = new List<ArticleRow>();
            var root = FindReader();
            if (root == null)
            {
                Error = Strings.Art_NoReader;
                return (kept, rows.ToList());
            }
            var dir = Folder();
            Busy = true;
            Done = null;
            Error = null;
            foreach (var row in rows.Where(r => r.OnReader != null))
            {
                try
                {
                    row.KeptPath = await Task.Run(() => Articles.Keep(root, row.OnReader!, dir));
                    row.Error = null;
                    row.IsSelected = false;
                    kept.Add(row);
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex, $"Keeping article {row.Id} failed");
                    row.Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Art_CouldNotKeep, row.Title, ex.Message);
                    failed.Add(row);
                }
            }
            Busy = false;
            UpdateTicked();
            Done = failed.Count == 0 ? string.Format(Strings.Art_KeptDone, kept.Count) : null;
            Error = failed.Count == 0 ? null : string.Format(Strings.Art_KeptSome, kept.Count, failed.Count);
            if (kept.Count > 0)
            {
                StatusChanged?.Invoke(string.Format(Strings.Art_KeptDone, kept.Count));
            }
            return (kept, failed);
        }

        [RelayCommand]
        private async Task RemoveTicked()
        {
            var rows = VisibleRows.Where(r => r.IsSelected && r.IsOnReader).ToList();
            if (rows.Count == 0 || ConfirmRemove?.Invoke(rows) != true)
            {
                return;
            }
            await RemoveAsync(rows);
        }

        /// <summary>
        /// The confirmation for removing these articles: how many, their titles, that Instapaper keeps
        /// them, and how many have no copy kept.
        /// </summary>
        public static string RemovePrompt(IReadOnlyList<ArticleRow> rows)
        {
            var text = string.Format(Strings.Art_RemoveConfirm, rows.Count) + "\n\n"
                + string.Join("\n", rows.Take(10).Select(r => "• " + r.Title)) + (rows.Count > 10 ? "\n…" : "")
                + "\n\n" + Strings.Art_RemoveInstapaper;
            var notKept = rows.Count(r => !r.IsKept);
            return notKept == 0 ? text : text + "\n\n" + string.Format(Strings.Art_RemoveNotKept, notKept);
        }

        /// <summary>Remove these articles from the eReader.  Returns those removed; Error says why when it failed.</summary>
        public async Task<List<ArticleRow>> RemoveAsync(IReadOnlyList<ArticleRow> rows)
        {
            var root = FindReader();
            var targets = rows.Where(r => r.IsOnReader).ToList();
            if (root == null)
            {
                Error = Strings.Art_NoReader;
                return new List<ArticleRow>();
            }
            Busy = true;
            Done = null;
            Error = null;
            try
            {
                var backup = await Task.Run(() => Articles.Remove(root, targets.Select(r => r.Id).ToList(), BackupFolder()));
                LogManager.Info($"Removed {targets.Count} articles from the eReader; book list copied to {backup}");
            }
            catch (Exception ex)
            {
                LogManager.Error(ex, "Removing articles failed");
                Busy = false;
                Error = ex is HoboLibrisException ? ex.Message : string.Format(Strings.Art_CouldNotRemove, ex.Message);
                return new List<ArticleRow>();
            }
            Busy = false;
            await LoadAsync();
            Done = string.Format(Strings.Art_Removed, targets.Count);
            StatusChanged?.Invoke(Done);
            return targets;
        }

        [RelayCommand]
        private Task LookAgain() => LoadAsync();

        [RelayCommand]
        private void OpenFolder() => Dialogs.OpenFolder(Folder());

        [RelayCommand]
        private void OpenPage(ArticleRow? row)
        {
            if (row != null && row.CanOpenPage && Uri.TryCreate(row.Url, UriKind.Absolute, out var page))
            {
                Start(page.AbsoluteUri);
            }
        }

        [RelayCommand]
        private void OpenCopy(ArticleRow? row)
        {
            if (row?.KeptPath is { } path && File.Exists(path))
            {
                Start(path);
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
