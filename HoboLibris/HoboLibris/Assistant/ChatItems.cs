using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        public AssistantMessage(string text) => Text = text;

        public string Text { get; }

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
        public bool CanAlwaysAllow { get; }

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
}
