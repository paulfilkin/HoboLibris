using CommunityToolkit.Mvvm.ComponentModel;
using HoboLibris.Models;
using HoboLibris.Resources;

namespace HoboLibris.ViewModels
{
    public enum RowSource
    {
        Store,
        Reader,
        Folder,
        Kindle,
    }

    /// <summary>
    /// One row of a book list: a Kobo store book, a book on the eReader, a book only in the Kobo
    /// folder, or a Kindle book. The row knows whether it is saved and what its copy is doing.
    /// </summary>
    public partial class BookRow : ObservableObject
    {
        public RowSource Source { get; }
        public string Id { get; }
        public string Title { get; }
        public string Authors { get; }
        public string Format { get; }
        /// <summary>The file or folder behind the row, for the tool tip.</summary>
        public string Location { get; }
        public StoreBook? StoreBook { get; }
        public DeviceBook? DeviceBook { get; }
        public SendItem? FolderItem { get; }

        /// <summary>What a screen reader reads for the row.</summary>
        public override string ToString() => Title;

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsSaved), nameof(ShowProgress), nameof(ShowBackUp),
            nameof(ShowAgain), nameof(ShowCopyAnyway), nameof(IsFailed), nameof(StatusToolTip), nameof(IsGood))]
        private string? _savedPath;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowProgress), nameof(ShowBackUp), nameof(ShowAgain),
            nameof(ShowCopyAnyway), nameof(IsFailed), nameof(StatusToolTip), nameof(Progress), nameof(IsGood))]
        private RowState _state = RowState.Idle;

        public BookRow(StoreBook book)
        {
            Source = RowSource.Store;
            Id = book.Id;
            Title = book.Title;
            Authors = book.Authors;
            Format = book.FormatLabel;
            Location = book.Title;
            StoreBook = book;
        }

        public BookRow(DeviceBook book, RowSource source)
        {
            Source = source;
            Id = book.Id;
            Title = book.Title;
            Authors = book.Authors;
            Format = book.Format;
            Location = book.Source;
            DeviceBook = book;
        }

        public BookRow(SendItem item)
        {
            Source = RowSource.Folder;
            Id = item.Id;
            Title = item.Title;
            Authors = item.Author;
            Format = item.Format;
            Location = item.Path;
            FolderItem = item;
        }

        public bool IsSaved => SavedPath != null;
        public bool IsFailed => State.Status == RowStatus.Failed;
        public bool ShowProgress => State.Status == RowStatus.Working;
        public double Progress => State.Progress;

        /// <summary>Why "Back up all" skips this book, when it does.</summary>
        public string? Duplicate => DeviceBook?.Duplicate;

        /// <summary>The store doesn't offer a file for this book.</summary>
        public bool NotDownloadable => Source == RowSource.Store && !(StoreBook?.CanDownload ?? false);

        /// <summary>The "From" column: Store, Store · protected, eReader, This computer, or where a Kindle book was found.</summary>
        public string FromText => Source switch
        {
            RowSource.Store => StoreBook!.IsProtected ? Strings.Row_From_StoreProtected : Strings.Row_From_Store,
            RowSource.Reader => Strings.Row_From_Reader,
            RowSource.Folder => Strings.Row_From_Computer,
            _ => DeviceBook?.Origin ?? "",
        };

        public string FromToolTip => Source switch
        {
            RowSource.Store => Strings.Row_From_StoreTip,
            RowSource.Reader => Strings.Row_From_ReaderTip,
            RowSource.Folder => Strings.Row_From_ComputerTip,
            _ => DeviceBook?.Source ?? "",
        };

        public bool IsStoreFree => Source == RowSource.Store && !(StoreBook?.IsProtected ?? true);

        /// <summary>The text in the "On this computer" column.</summary>
        public string StatusText
        {
            get
            {
                switch (State.Status)
                {
                    case RowStatus.Queued:
                        return Strings.Row_Waiting;
                    case RowStatus.Working:
                        return "";
                    case RowStatus.Failed:
                        return Strings.Row_Failed;
                }
                if (Source == RowSource.Folder)
                {
                    return Strings.Row_InTheFolder;
                }
                if (IsSaved)
                {
                    return Strings.Row_Saved;
                }
                if (Duplicate != null)
                {
                    return Source == RowSource.Reader ? Strings.Row_SameAsStore : Strings.Row_FromStore;
                }
                if (NotDownloadable)
                {
                    return Strings.Row_NotDownloadable;
                }
                return "";
            }
        }

        public string? StatusToolTip
        {
            get
            {
                if (State.Status == RowStatus.Failed)
                {
                    return State.Error;
                }
                if (Source == RowSource.Folder)
                {
                    return Location;
                }
                if (IsSaved)
                {
                    return SavedPath;
                }
                if (Duplicate != null)
                {
                    return Source == RowSource.Reader ? Strings.Row_SameAsStoreTip : string.Format(Strings.Row_FromStoreTip, Duplicate);
                }
                return null;
            }
        }

        /// <summary>Green text: saved, in the folder, or skipped because the store copy covers it.</summary>
        public bool IsGood => State.Status is RowStatus.Idle && (IsSaved || Source == RowSource.Folder || (Duplicate != null && Source != RowSource.Reader));

        /// <summary>The "Back up" button: not saved, not a duplicate, downloadable, idle or failed.</summary>
        public bool ShowBackUp =>
            Source != RowSource.Folder
            && State.Status is RowStatus.Idle or RowStatus.Failed
            && !IsSaved && Duplicate == null && !NotDownloadable;

        /// <summary>The small "Again" button beside "Saved".</summary>
        public bool ShowAgain => Source != RowSource.Folder && State.Status == RowStatus.Idle && IsSaved;

        /// <summary>The small "Copy" button beside "Same as store book".</summary>
        public bool ShowCopyAnyway => Source != RowSource.Folder && State.Status == RowStatus.Idle && !IsSaved && Duplicate != null;

        /// <summary>Whether "Back up all not yet saved" includes this row.</summary>
        public bool NeedsBackup => Source != RowSource.Folder && !IsSaved && Duplicate == null && !NotDownloadable;

        public bool Matches(string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }
            return Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                   || Authors.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
