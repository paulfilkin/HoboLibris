using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoboLibris.Assistant.Sources;
using HoboLibris.Resources;

namespace HoboLibris.Assistant
{
    /// <summary>One line of the Assistant pane.  The pane draws each kind with its own template.</summary>
    public abstract class ChatItem : ObservableObject
    {
        // Set when an item is restored from a saved conversation.
        public DateTime Time { get; init; } = DateTime.Now;

        internal static readonly JsonSerializerOptions Indented = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        internal static string Format(JsonObject arguments) => arguments.ToJsonString(Indented);
    }

    public sealed class UserMessage : ChatItem
    {
        public UserMessage(string text, string context)
        {
            Text = text;
            Context = context;
        }

        public string Text { get; }

        /// <summary>Where the person was when they asked ("Kobo · Collections"), shown under the message.</summary>
        public string Context { get; }

        public override string ToString() => Text;
    }

    public sealed class AssistantMessage : ChatItem
    {
        private string _text;

        public AssistantMessage(string text) => _text = text;

        /// <summary>Grows while a streamed reply comes in.</summary>
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        public override string ToString() => Text;
    }

    public sealed class NoticeMessage : ChatItem
    {
        public NoticeMessage(string text, bool isError)
        {
            Text = text;
            IsError = isError;
        }

        public string Text { get; }
        public bool IsError { get; }

        public override string ToString() => Text;
    }

    public enum ToolActivityState
    {
        Running,
        Done,
        Failed,
        Declined,
    }

    /// <summary>A tool the Assistant used: what it was, its arguments, and what came back.</summary>
    public sealed partial class ToolActivity : ChatItem
    {
        public ToolActivity(AssistantTool tool, JsonObject arguments)
            : this(tool.Title, Format(arguments))
        {
        }

        /// <summary>A tool activity as saved (a restored conversation).</summary>
        public ToolActivity(string title, string arguments)
        {
            Title = title;
            Arguments = arguments;
            _summary = Strings.Asst_Running;
        }

        public string Title { get; }
        public string Arguments { get; }

        [ObservableProperty]
        private ToolActivityState _state = ToolActivityState.Running;

        [ObservableProperty]
        private string _summary;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasResult))]
        private string _result = string.Empty;

        /// <summary>The arguments and result are shown.</summary>
        [ObservableProperty]
        private bool _isExpanded;

        public bool HasResult => Result.Length > 0;

        public override string ToString() => $"{Title}: {Summary}";
    }

    /// <summary>
    /// The books a search found, drawn by HoboLibris under the search step: every match, not only the
    /// few the model is given, with a Download and an Open page button on each.  The model needn't
    /// list them, so the list is complete and quick with any model.
    /// </summary>
    public sealed partial class SearchResults : ChatItem
    {
        /// <summary>Rows shown before Show all is pressed.</summary>
        public const int FirstShown = 10;

        public SearchResults(string heading, IEnumerable<EbookResult> books)
        {
            Heading = heading;
            Rows = books.Select(b => new SearchResultRow(this, b)).ToList();
        }

        /// <summary>"76 found in Project Gutenberg" or "40 of 2,113 found in Open Library".</summary>
        public string Heading { get; }

        public IReadOnlyList<SearchResultRow> Rows { get; }

        /// <summary>Downloads a book into the Kobo folder: set by the session, so a restored list works too.</summary>
        public Func<EbookResult, Task<ToolOutcome>>? Download { get; set; }

        /// <summary>Opens a book's page in the browser: set by the session.</summary>
        public Action<Uri>? OpenPage { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Shown), nameof(HasMore))]
        private bool _showAll;

        public IEnumerable<SearchResultRow> Shown => ShowAll ? Rows : Rows.Take(FirstShown);

        public bool HasMore => !ShowAll && Rows.Count > FirstShown;

        public string ShowAllText => string.Format(Strings.Asst_ShowAllResults, Rows.Count);

        [RelayCommand]
        private void ShowEverything() => ShowAll = true;

        /// <summary>
        /// The rows as saved with the conversation: no download addresses, since an OPDS address may hold
        /// a key.  A restored Google Books or OPDS row can only be opened, not downloaded.
        /// </summary>
        public string ToSaved() => new JsonArray(Rows.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Book.Id,
            ["source"] = r.Book.Source,
            ["title"] = r.Book.Title,
            ["authors"] = r.Book.Authors,
            ["language"] = r.Book.Language,
            ["page"] = r.Book.PageUrl?.AbsoluteUri,
            ["free"] = r.Book.Free,
            ["price"] = r.Book.Price,
        }).ToArray()).ToJsonString();

        public static SearchResults FromSaved(string heading, string? rows, DateTime time)
        {
            var books = new List<EbookResult>();
            try
            {
                foreach (var row in (JsonNode.Parse(rows ?? "[]") as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    books.Add(new EbookResult
                    {
                        Id = (row["id"] as JsonValue)?.ToString() ?? "",
                        Source = (row["source"] as JsonValue)?.ToString() ?? "",
                        Title = (row["title"] as JsonValue)?.ToString() ?? "",
                        Authors = (row["authors"] as JsonValue)?.ToString() ?? "",
                        Language = (row["language"] as JsonValue)?.ToString(),
                        PageUrl = Uri.TryCreate((row["page"] as JsonValue)?.ToString(), UriKind.Absolute, out var page) ? page : null,
                        // Lists saved before #91 hold only free books.
                        Free = row["free"] is not JsonValue free || !free.TryGetValue(out bool isFree) || isFree,
                        Price = (row["price"] as JsonValue)?.ToString(),
                    });
                }
            }
            catch (JsonException)
            {
            }
            return new SearchResults(heading, books) { Time = time };
        }

        public override string ToString() => Heading;
    }

    public enum SearchRowState
    {
        Ready,
        Downloading,
        Done,
        Failed,
    }

    /// <summary>One book in a search's list.</summary>
    public sealed partial class SearchResultRow : ObservableObject
    {
        private readonly SearchResults _owner;

        public SearchResultRow(SearchResults owner, EbookResult book)
        {
            _owner = owner;
            Book = book;
        }

        public EbookResult Book { get; }
        public string Title => Book.Title;
        public string Authors => Book.Authors;
        public string Language => Book.Language ?? "";

        /// <summary>
        /// Gutenberg and Open Library books by their ids; Google Books and OPDS books by the address the
        /// search found.  Never a book that isn't free (#91).
        /// </summary>
        public bool Downloadable => Book.Free && (Book.Source is "gutenberg" or "open_library" || Book.DownloadUrl != null);

        public bool CanOpen => Book.PageUrl != null && Book.Source != "opds";

        /// <summary>A book that isn't free can be looked for in the Kobo store, in the person's browser (#91).</summary>
        public bool CanFindInStore => !Book.Free;

        /// <summary>"Not free · 9.99 GBP" under the title of a book that isn't free; empty for a free one.</summary>
        public string PriceText => Book.Free ? "" : Book.Price is { } price ? string.Format(Strings.Asst_ForSale, price) : Strings.Asst_NotFree;

        [RelayCommand]
        private void FindInStore()
        {
            if (CanFindInStore && Uri.TryCreate(Services.Previews.SearchUrl($"{Book.Title} {Book.Authors}".Trim()), UriKind.Absolute, out var url))
            {
                _owner.OpenPage?.Invoke(url);
            }
        }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
        private SearchRowState _state;

        /// <summary>What the download did, shown as the button's tooltip.</summary>
        [ObservableProperty]
        private string _message = Strings.Asst_DownloadTip;

        private bool CanDownload() => Downloadable && State is SearchRowState.Ready or SearchRowState.Failed && _owner.Download != null;

        [RelayCommand(CanExecute = nameof(CanDownload))]
        private async Task DownloadAsync()
        {
            State = SearchRowState.Downloading;
            Message = Strings.Asst_Downloading;
            var outcome = await _owner.Download!(Book);
            State = outcome.IsError ? SearchRowState.Failed : SearchRowState.Done;
            Message = outcome.IsError ? outcome.Content : outcome.Summary ?? Strings.Asst_Downloaded;
        }

        [RelayCommand]
        private void Open()
        {
            if (CanOpen)
            {
                _owner.OpenPage?.Invoke(Book.PageUrl!);
            }
        }
    }

    /// <summary>The Assistant wants to use a tool that asks first: the person allows it once, always, or declines.</summary>
    public sealed partial class ApprovalRequest : ChatItem
    {
        private readonly TaskCompletionSource<(bool Allowed, bool Always)> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ApprovalRequest(AssistantTool tool, JsonObject arguments)
            : this(tool.Title, tool.Describe?.Invoke(arguments) ?? string.Format(Strings.Asst_UseTool, tool.Title), Format(arguments))
        {
            CanAlwaysAllow = !tool.AlwaysAsks;
        }

        private ApprovalRequest(string title, string description, string arguments)
        {
            Title = title;
            Description = description;
            Arguments = arguments;
        }

        /// <summary>A card still waiting, with its sentence given (the sample exchange in check pictures).</summary>
        public static ApprovalRequest Sample(AssistantTool tool, string description, JsonObject arguments) =>
            new(tool.Title, description, Format(arguments)) { CanAlwaysAllow = !tool.AlwaysAsks };

        /// <summary>An approval as saved (a restored conversation): already answered, with its outcome.</summary>
        public static ApprovalRequest Restored(string title, string description, string arguments, string outcome)
        {
            var approval = new ApprovalRequest(title, description, arguments);
            approval._answer.TrySetResult((false, false));
            approval.Outcome = outcome;
            return approval;
        }

        public string Title { get; }
        public string Description { get; }
        public string Arguments { get; }

        /// <summary>False for tools that ask every time (writing to the eReader, deleting files).</summary>
        public bool CanAlwaysAllow { get; private init; }

        public bool IsPending => !_answer.Task.IsCompleted;

        [ObservableProperty]
        private string _outcome = string.Empty;

        [ObservableProperty]
        private bool _isExpanded;

        public Task<(bool Allowed, bool Always)> Answered => _answer.Task;

        public void Answer(bool allowed, bool always)
        {
            if (!_answer.TrySetResult((allowed, always)))
            {
                return;
            }
            Outcome = !allowed ? Strings.Asst_Declined : always ? Strings.Asst_AllowedAlways : Strings.Asst_Allowed;
            OnPropertyChanged(nameof(IsPending));
        }

        [RelayCommand]
        private void Allow() => Answer(true, false);

        [RelayCommand]
        private void AlwaysAllow() => Answer(true, true);

        [RelayCommand]
        private void Decline() => Answer(false, false);

        public override string ToString() => Description;
    }

    /// <summary>
    /// A question the program puts to the person before a tool runs (#91), such as free books only or
    /// paid books too.  The card is the program's, so it is asked whatever the model.
    /// </summary>
    public sealed partial class ChoiceRequest : ChatItem
    {
        private readonly TaskCompletionSource<int> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChoiceRequest(string title, ToolQuestion question)
            : this(title, question.Text, question.Options.Select(o => o.Label))
        {
        }

        private ChoiceRequest(string title, string question, IEnumerable<string> labels)
        {
            Title = title;
            Question = question;
            Options = labels.Select((label, i) => new ChoiceOption(this, label, i)).ToList();
        }

        /// <summary>A question as saved (a restored conversation): already answered, with its outcome.</summary>
        public static ChoiceRequest Restored(string title, string question, string outcome)
        {
            var choice = new ChoiceRequest(title, question, Array.Empty<string>());
            choice._answer.TrySetResult(-1);
            choice.Outcome = outcome;
            return choice;
        }

        public string Title { get; }
        public string Question { get; }
        public IReadOnlyList<ChoiceOption> Options { get; }

        public bool IsPending => !_answer.Task.IsCompleted;

        [ObservableProperty]
        private string _outcome = string.Empty;

        /// <summary>The option chosen, or -1 when the person declined or pressed Stop.</summary>
        public Task<int> Answered => _answer.Task;

        public void Answer(int index)
        {
            if (!_answer.TrySetResult(index))
            {
                return;
            }
            Outcome = index >= 0 && index < Options.Count ? Options[index].Label : Strings.Asst_Declined;
            OnPropertyChanged(nameof(IsPending));
        }

        [RelayCommand]
        private void Decline() => Answer(-1);

        public override string ToString() => Question;
    }

    /// <summary>One button on a ChoiceRequest card.</summary>
    public sealed partial class ChoiceOption
    {
        private readonly ChoiceRequest _owner;
        private readonly int _index;

        public ChoiceOption(ChoiceRequest owner, string label, int index)
        {
            _owner = owner;
            Label = label;
            _index = index;
        }

        public string Label { get; }

        [RelayCommand]
        private void Choose() => _owner.Answer(_index);
    }
}
