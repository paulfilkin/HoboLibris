using System.Text.Json.Serialization;
using HoboLibris.Assistant.Sources;
using HoboLibris.Resources;

namespace HoboLibris.Assistant
{
    /// <summary>Settings › AI model and Assistant, kept in settings.json under "assistant".</summary>
    public sealed class AssistantSettings
    {
        [JsonPropertyName("provider")]
        public ModelProvider Provider { get; set; } = ModelProvider.Anthropic;

        [JsonPropertyName("model")]
        public string Model { get; set; } = ChatProviders.DefaultModel(ModelProvider.Anthropic);

        /// <summary>The base address of an OpenAI-compatible service (or a different Anthropic or OpenAI address).</summary>
        [JsonPropertyName("endpoint")]
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>API keys by provider name, encrypted with DPAPI (SecretStore).</summary>
        [JsonPropertyName("api_keys")]
        public Dictionary<string, string> ApiKeys { get; set; } = new();

        /// <summary>
        /// The most tokens in one reply (256 to 64000).  16000 as in Mycelia: reasoning models such as
        /// GPT-5 count their reasoning in it.
        /// </summary>
        public const int DefaultMaxTokens = 16000;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = DefaultMaxTokens;

        /// <summary>The most model turns (each may call tools) for one request.</summary>
        [JsonPropertyName("max_steps")]
        public int MaxSteps { get; set; } = 20;

        /// <summary>Extra instructions added to every conversation.</summary>
        [JsonPropertyName("instructions")]
        public string Instructions { get; set; } = string.Empty;

        /// <summary>Never ask before a tool runs (every Ask counts as Allow), except tools that always ask.  Off by default.</summary>
        [JsonPropertyName("skip_approvals")]
        public bool SkipApprovals { get; set; }

        /// <summary>Start Ollama's server when a request needs it (an Ollama address only).  Off by default.</summary>
        [JsonPropertyName("auto_start_ollama")]
        public bool AutoStartOllama { get; set; }

        /// <summary>Policies for the tools that differ from their defaults.</summary>
        [JsonPropertyName("tool_policies")]
        public Dictionary<string, ToolPolicy> ToolPolicies { get; set; } = new();

        /// <summary>The e-book sources the Assistant may search and download from.</summary>
        [JsonPropertyName("sources")]
        public SourceSettings Sources { get; set; } = new();

        /// <summary>The Assistant pane on the right of the main window: shown or not, and its width.</summary>
        [JsonPropertyName("pane_visible")]
        public bool PaneVisible { get; set; }

        [JsonPropertyName("pane_width")]
        public double PaneWidth { get; set; } = 400;

        public string GetApiKey(ModelProvider provider) =>
            ApiKeys.TryGetValue(provider.ToString(), out var stored) ? SecretStore.Unprotect(stored) : string.Empty;

        public void SetApiKey(ModelProvider provider, string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                ApiKeys.Remove(provider.ToString());
            }
            else
            {
                ApiKeys[provider.ToString()] = SecretStore.Protect(key.Trim());
            }
        }

        [JsonIgnore]
        public bool HasKey => !ChatProviders.NeedsKey(Provider, Endpoint) || GetApiKey(Provider).Length > 0;

        [JsonIgnore]
        public string ProviderName => Provider switch
        {
            ModelProvider.Anthropic => "Anthropic",
            ModelProvider.OpenAI => "OpenAI",
            _ => ChatProviders.IsLocal(Provider, Endpoint) ? Strings.Asst_LocalAiService : Strings.Asst_AiService,
        };

        [JsonIgnore]
        public string ModelLine => $"{ProviderName} · {Model}";
    }
}
