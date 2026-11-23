using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoboLibris.Assistant;
using HoboLibris.Assistant.Sources;
using HoboLibris.Resources;
using HoboLibris.Services;

namespace HoboLibris.ViewModels
{
    /// <summary>A choice in a drop-down: the value and the words shown for it.</summary>
    public sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>One OPDS catalogue in Settings › E-book sources.</summary>
    public sealed class CatalogueRow
    {
        public CatalogueRow(OpdsCatalogue catalogue, Action save)
        {
            Catalogue = catalogue;
            _save = save;
        }

        private readonly Action _save;

        public OpdsCatalogue Catalogue { get; }
        public string Name => Catalogue.Name;

        /// <summary>The server only: the path may hold a key (Kavita).</summary>
        public string Server => Catalogue.Server;

        public bool Enabled
        {
            get => Catalogue.Enabled;
            set
            {
                Catalogue.Enabled = value;
                _save();
            }
        }

        public override string ToString() => Name;
    }

    /// <summary>One tool in Settings › Built-in tools: Allow, Ask first or Off.</summary>
    public sealed partial class ToolPolicyRow : ObservableObject
    {
        private readonly AssistantTool _tool;
        private readonly Action<AssistantTool, ToolPolicy> _set;

        public ToolPolicyRow(AssistantTool tool, Action<AssistantTool, ToolPolicy> set)
        {
            _tool = tool;
            _set = set;
            var choices = new List<Choice<ToolPolicy>>();
            if (!tool.AlwaysAsks)
            {
                choices.Add(new(ToolPolicy.Allow, Strings.Asst_PolicyAllow));
            }
            choices.Add(new(ToolPolicy.Ask, tool.AlwaysAsks ? Strings.Asst_PolicyAlwaysAsk : Strings.Asst_PolicyAsk));
            choices.Add(new(ToolPolicy.Off, Strings.Asst_PolicyOff));
            Choices = choices;
        }

        public string Title => _tool.Title;
        public IReadOnlyList<Choice<ToolPolicy>> Choices { get; }

        public ToolPolicy Policy
        {
            get => _tool.Policy;
            set
            {
                if (_tool.Policy == value)
                {
                    return;
                }
                _set(_tool, value);
                OnPropertyChanged();
            }
        }

        public override string ToString() => Title;
    }

    /// <summary>
    /// The Assistant pane and its settings: the conversation (AssistantSession), the box to type in,
    /// and Settings › AI model, Assistant and Built-in tools.  Every settings change is saved at once,
    /// as the rest of Settings is.
    /// </summary>
    public sealed partial class AssistantViewModel : ObservableObject, IAssistantHost
    {
        private readonly MainViewModel _main;
        private readonly HoboTools _tools;

        /// <param name="conversationPath">Where the conversation is kept; null to keep nothing (screenshots).</param>
        public AssistantViewModel(MainViewModel main, string? conversationPath)
        {
            _main = main;
            _tools = new HoboTools(main);
            Session = new AssistantSession(this, conversationPath);
            Session.BusyChanged += () =>
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(BusyText));
                OnPropertyChanged(nameof(SendToolTip));
                OnPropertyChanged(nameof(IsEmpty));
                SendOrStopCommand.NotifyCanExecuteChanged();
            };
            Session.Status += s => _main.Status = s;
            Session.Prepare = PrepareAsync;
            Session.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
            ToolRows = _tools.All.Select(t => new ToolPolicyRow(t, SetPolicy)).ToList();
            Providers = new List<Choice<ModelProvider>>
            {
                new(ModelProvider.Anthropic, "Anthropic"),
                new(ModelProvider.OpenAI, "OpenAI"),
                new(ModelProvider.OpenAICompatible, Strings.Asst_ProviderCompatible),
            };
            Models = new ObservableCollection<string>(ChatProviders.SuggestedModels(Settings.Provider));
            Catalogues = new ObservableCollection<CatalogueRow>(Settings.Sources.Catalogues.Select(c => new CatalogueRow(c, SaveSettings)));
        }

        public AssistantSession Session { get; }

        /// <summary>Called once the window is up: fetches what the sources need in the background.</summary>
        public Task WarmUpAsync() => _tools.WarmUpAsync();
        public ObservableCollection<ChatItem> Items => Session.Items;

        // ---- IAssistantHost ---------------------------------------------------------------------

        public AssistantSettings Settings => _main.Settings.Assistant;
        public void SaveSettings() => SettingsService.Save(_main.Settings);
        public IReadOnlyList<AssistantTool> Tools => _tools.All;
        public JsonObject Context() => _tools.Context();
        public string ContextLine() => _tools.ScreenName();

        // ---- The pane ---------------------------------------------------------------------------

        /// <summary>Asks before the conversation is cleared.</summary>
        public Func<bool>? ConfirmClear { get; set; }

        /// <summary>Puts text on the clipboard.</summary>
        public Action<string>? CopyText { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SendOrStopCommand))]
        private string _input = "";

        public bool IsBusy => Session.IsBusy;
        public string BusyText => Session.BusyText;
        public bool IsEmpty => Items.Count == 0;
        public string SendToolTip => IsBusy ? Strings.Asst_StopTip : Strings.Asst_SendTip;

        public bool PaneVisible
        {
            get => Settings.PaneVisible;
            set
            {
                if (Settings.PaneVisible == value)
                {
                    return;
                }
                Settings.PaneVisible = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }

        /// <summary>Shows the pane for a check picture without saving that it is open.</summary>
        public void ShowPaneWithoutSaving()
        {
            Settings.PaneVisible = true;
            Settings.PaneFolded = false;
            OnPropertyChanged(nameof(PaneVisible));
            OnPropertyChanged(nameof(PaneFolded));
        }

        /// <summary>The pane folded to a strip at the edge (still open, out of the way).</summary>
        public bool PaneFolded
        {
            get => Settings.PaneFolded;
            set
            {
                if (Settings.PaneFolded == value)
                {
                    return;
                }
                Settings.PaneFolded = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }

        [RelayCommand]
        private void FoldPane() => PaneFolded = true;

        public double PaneWidth
        {
            get => Math.Clamp(Settings.PaneWidth, 300, 900);
            set
            {
                Settings.PaneWidth = Math.Round(value);
                SaveSettings();
            }
        }

        /// <summary>A model and its key are set up; until then a banner points to Settings.</summary>
        public bool IsSetUp => Settings.HasKey;
        public string ModelLine => Settings.ModelLine;

        [RelayCommand]
        private void TogglePane()
        {
            // The title-bar icon opens the pane (from closed or folded) and closes an open one.
            if (PaneVisible && !PaneFolded)
            {
                PaneVisible = false;
                return;
            }
            PaneFolded = false;
            PaneVisible = true;
        }

        [RelayCommand(CanExecute = nameof(CanSendOrStop))]
        private void SendOrStop()
        {
            if (IsBusy)
            {
                Session.Stop();
                return;
            }
            var text = Input;
            if (text.Trim().Length == 0)
            {
                return;
            }
            Input = "";
            _ = Session.SendAsync(text);
        }

        private bool CanSendOrStop() => IsBusy || Input.Trim().Length > 0;

        [RelayCommand]
        private void Clear()
        {
            if (IsBusy || Items.Count == 0)
            {
                return;
            }
            if (ConfirmClear?.Invoke() ?? true)
            {
                Session.ClearConversation();
            }
        }

        [RelayCommand]
        private void Copy()
        {
            if (Items.Count > 0)
            {
                CopyText?.Invoke(Session.ConversationText());
            }
        }

        // ---- Settings › AI model ----------------------------------------------------------------

        public IReadOnlyList<Choice<ModelProvider>> Providers { get; }

        /// <summary>Suggested model names for the provider; any name can be typed.</summary>
        public ObservableCollection<string> Models { get; }

        /// <summary>The provider changed, so the key box shows that provider's key (Settings window).</summary>
        public event Action? ProviderChanged;

        public ModelProvider Provider
        {
            get => Settings.Provider;
            set
            {
                if (Settings.Provider == value)
                {
                    return;
                }
                var oldSuggestions = ChatProviders.SuggestedModels(Settings.Provider);
                Settings.Provider = value;
                // A model name from the old provider's list means nothing to the new one.
                if (Settings.Model.Length == 0 || oldSuggestions.Contains(Settings.Model))
                {
                    Settings.Model = ChatProviders.DefaultModel(value);
                }
                Models.Clear();
                foreach (var m in ChatProviders.SuggestedModels(value))
                {
                    Models.Add(m);
                }
                SaveSettings();
                TestResult = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Model));
                OnPropertyChanged(nameof(IsCompatible));
                ModelSettingsChanged();
                ProviderChanged?.Invoke();
                _ = RefreshOllamaAsync();
            }
        }

        public bool IsCompatible => Settings.Provider == ModelProvider.OpenAICompatible;
        public IReadOnlyList<string> Endpoints => ChatProviders.SuggestedEndpoints;

        public string Model
        {
            get => Settings.Model;
            set
            {
                Settings.Model = value.Trim();
                SaveSettings();
                ModelSettingsChanged();
            }
        }

        public string Endpoint
        {
            get => Settings.Endpoint;
            set
            {
                Settings.Endpoint = value.Trim();
                SaveSettings();
                ModelSettingsChanged();
                _ = RefreshOllamaAsync();
            }
        }

        /// <summary>Whether a key is saved for the provider, in words for under the key box.</summary>
        public string KeyStatus => Settings.GetApiKey(Settings.Provider).Length > 0
            ? Strings.Asst_KeySaved
            : ChatProviders.NeedsKey(Settings.Provider, Settings.Endpoint) ? Strings.Asst_KeyNone : Strings.Asst_KeyNotNeeded;

        /// <summary>The key typed in Settings; encrypted at once.  It is never read back into the box.</summary>
        public void SetApiKey(string key)
        {
            Settings.SetApiKey(Settings.Provider, key);
            SaveSettings();
            ModelSettingsChanged();
        }

        [RelayCommand]
        private void RemoveApiKey() => SetApiKey("");

        public int MaxTokens
        {
            get => Settings.MaxTokens;
            set
            {
                Settings.MaxTokens = Math.Clamp(value, 256, 64000);
                SaveSettings();
                OnPropertyChanged();
            }
        }

        private void ModelSettingsChanged()
        {
            OnPropertyChanged(nameof(KeyStatus));
            OnPropertyChanged(nameof(IsSetUp));
            OnPropertyChanged(nameof(ModelLine));
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTestResult))]
        private string? _testResult;

        [ObservableProperty]
        private bool _testFailed;

        [ObservableProperty]
        private bool _testing;

        public bool HasTestResult => TestResult != null;

        /// <summary>Sends one short message to the model and says what came back.</summary>
        [RelayCommand]
        private async Task TestAsync()
        {
            if (Testing)
            {
                return;
            }
            Testing = true;
            TestResult = Strings.Asst_Testing;
            TestFailed = false;
            try
            {
                if (!Settings.HasKey)
                {
                    throw new ChatException(string.Format(Strings.Asst_AddKeyFirst, Settings.ModelLine));
                }
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var provider = Session.ProviderFactory(new ChatProviderOptions(Settings.Provider, Settings.Model,
                    Settings.GetApiKey(Settings.Provider), Settings.Endpoint, 256));
                var reply = await provider.CompleteAsync("Reply with the single word OK.", new ChatTurn[] { new UserTurn("Test") },
                    Array.Empty<ToolSpec>(), cancel.Token);
                TestResult = string.Format(Strings.Asst_TestOk, Settings.ModelLine, reply.Text.Trim());
            }
            catch (Exception ex) when (ex is ChatException or OperationCanceledException or InvalidOperationException)
            {
                TestFailed = true;
                TestResult = ex is OperationCanceledException ? string.Format(Strings.Asst_ProviderTimeout, Settings.ProviderName) : ex.Message;
            }
            Testing = false;
        }

        // ---- Settings › Assistant -----------------------------------------------------------------

        public string Instructions
        {
            get => Settings.Instructions;
            set
            {
                Settings.Instructions = value;
                SaveSettings();
            }
        }

        public int MaxSteps
        {
            get => Settings.MaxSteps;
            set
            {
                Settings.MaxSteps = Math.Clamp(value, 1, 100);
                SaveSettings();
                OnPropertyChanged();
            }
        }

        public bool SkipApprovals
        {
            get => Settings.SkipApprovals;
            set
            {
                Settings.SkipApprovals = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }

        // ---- Settings › E-book sources -------------------------------------------------------------

        private SourceSettings Sources => Settings.Sources;

        public bool UseGutenberg
        {
            get => Sources.Gutenberg;
            set
            {
                Sources.Gutenberg = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }

        public bool UseOpenLibrary
        {
            get => Sources.OpenLibrary;
            set
            {
                Sources.OpenLibrary = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }

        public bool UseGoogleBooks
        {
            get => Sources.GoogleBooks;
            set
            {
                Sources.GoogleBooks = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }

        public string GoogleKeyStatus => Sources.GetGoogleBooksKey().Length > 0 ? Strings.Asst_KeySaved : Strings.Src_GoogleNoKey;

        /// <summary>The Google Books key typed in Settings; encrypted at once and never read back into the box.</summary>
        public void SetGoogleBooksKey(string key)
        {
            Sources.SetGoogleBooksKey(key);
            SaveSettings();
            OnPropertyChanged(nameof(GoogleKeyStatus));
        }

        [RelayCommand]
        private void RemoveGoogleBooksKey() => SetGoogleBooksKey("");

        public ObservableCollection<CatalogueRow> Catalogues { get; }
        public bool HasCatalogues => Catalogues.Count > 0;

        [ObservableProperty]
        private string _newCatalogueName = "";

        [ObservableProperty]
        private string _newCatalogueAddress = "";

        [ObservableProperty]
        private string _newCatalogueUser = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCatalogueError))]
        private string? _catalogueError;

        public bool HasCatalogueError => CatalogueError != null;

        /// <summary>Adds the catalogue typed in Settings; the password comes from its box.  False when something is missing.</summary>
        public bool AddCatalogue(string password)
        {
            var name = NewCatalogueName.Trim();
            var address = NewCatalogueAddress.Trim();
            if (name.Length == 0)
            {
                CatalogueError = Strings.Src_NameFirst;
                return false;
            }
            if (Sources.Catalogues.Any(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                CatalogueError = string.Format(Strings.Src_NameTaken, name);
                return false;
            }
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                CatalogueError = Strings.Src_AddressFirst;
                return false;
            }
            var catalogue = OpdsCatalogue.Create(name, address, NewCatalogueUser, password);
            Sources.Catalogues.Add(catalogue);
            SaveSettings();
            Catalogues.Add(new CatalogueRow(catalogue, SaveSettings));
            OnPropertyChanged(nameof(HasCatalogues));
            NewCatalogueName = "";
            NewCatalogueAddress = "";
            NewCatalogueUser = "";
            CatalogueError = null;
            return true;
        }

        [RelayCommand]
        private void RemoveCatalogue(CatalogueRow? row)
        {
            if (row == null)
            {
                return;
            }
            Sources.Catalogues.Remove(row.Catalogue);
            SaveSettings();
            Catalogues.Remove(row);
            OnPropertyChanged(nameof(HasCatalogues));
        }

        // ---- Settings › Built-in tools --------------------------------------------------------------

        public IReadOnlyList<ToolPolicyRow> ToolRows { get; }

        private void SetPolicy(AssistantTool tool, ToolPolicy policy)
        {
            if (policy == tool.Default)
            {
                Settings.ToolPolicies.Remove(tool.Name);
            }
            else
            {
                Settings.ToolPolicies[tool.Name] = policy;
            }
            SaveSettings();
            Session.ApplyPolicies();
        }

        // ---- Settings › AI model: Ollama on this computer --------------------------------------------

        private readonly OllamaServer _ollama = new();

        /// <summary>The address is Ollama's on this computer, so HoboLibris can start and stop it.</summary>
        public bool IsOllama => OllamaServer.IsOllamaEndpoint(Settings.Provider, Settings.Endpoint);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartOllama), nameof(CanStopOllama))]
        private bool _ollamaRunning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartOllama), nameof(CanStopOllama))]
        private bool _ollamaBusy;

        [ObservableProperty]
        private string _ollamaStatus = "";

        public bool OllamaInstalled => OllamaServer.FindExecutable() != null;
        public bool CanStartOllama => !OllamaRunning && !OllamaBusy && OllamaInstalled;

        /// <summary>Only a server HoboLibris started can be stopped here.</summary>
        public bool CanStopOllama => OllamaRunning && !OllamaBusy && _ollama.StartedHere;

        public bool AutoStartOllama
        {
            get => Settings.AutoStartOllama;
            set
            {
                Settings.AutoStartOllama = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }

        /// <summary>Looks whether Ollama is running, for the line in Settings.</summary>
        public async Task RefreshOllamaAsync()
        {
            OnPropertyChanged(nameof(IsOllama));
            if (!IsOllama)
            {
                return;
            }
            OllamaRunning = await OllamaServer.IsRunningAsync(Settings.Endpoint);
            OllamaStatus = !OllamaInstalled && !OllamaRunning
                ? Strings.Asst_OllamaMissing
                : !OllamaRunning ? Strings.Asst_OllamaStopped
                : _ollama.StartedHere ? Strings.Asst_OllamaRunningHere : Strings.Asst_OllamaRunning;
            OnPropertyChanged(nameof(CanStartOllama));
            OnPropertyChanged(nameof(CanStopOllama));
        }

        [RelayCommand]
        private async Task StartOllamaAsync()
        {
            OllamaBusy = true;
            OllamaStatus = Strings.Asst_StartingOllama;
            var problem = await _ollama.StartAsync(Settings.Endpoint);
            OllamaBusy = false;
            await RefreshOllamaAsync();
            if (problem != null)
            {
                OllamaStatus = string.Format(Strings.Asst_OllamaDidNotStart, problem);
            }
        }

        [RelayCommand]
        private async Task StopOllamaAsync()
        {
            _ollama.Stop();
            await RefreshOllamaAsync();
        }

        /// <summary>
        /// Before a request to Ollama on this computer: start it when the person asked for that, or
        /// say it isn't running (rather than "couldn't be reached").
        /// </summary>
        private async Task<string?> PrepareAsync(CancellationToken cancel)
        {
            if (!IsOllama || await OllamaServer.IsRunningAsync(Settings.Endpoint, cancel))
            {
                return null;
            }
            if (!Settings.AutoStartOllama)
            {
                return Strings.Asst_OllamaNotRunning;
            }
            Session.SetStatusText(Strings.Asst_StartingOllama);
            var problem = await _ollama.StartAsync(Settings.Endpoint, cancel);
            await RefreshOllamaAsync();
            return problem == null ? null : string.Format(Strings.Asst_OllamaDidNotStart, problem);
        }

        /// <summary>The window is closing: the conversation is saved, and an Ollama HoboLibris started is stopped.</summary>
        public void Shutdown()
        {
            Session.Shutdown();
            _ollama.Dispose();
        }
    }
}
