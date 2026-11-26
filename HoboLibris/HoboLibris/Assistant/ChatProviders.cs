using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HoboLibris.Resources;

namespace HoboLibris.Assistant
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ModelProvider
    {
        Anthropic,
        OpenAI,
        /// <summary>Any service with the OpenAI chat completions interface: Gemini, Mistral, Ollama, LM Studio...</summary>
        OpenAICompatible,
    }

    /// <summary>A tool the model may call: a name, what it does, and a JSON Schema for its arguments.</summary>
    public sealed record ToolSpec(string Name, string Description, JsonObject InputSchema);

    public sealed record ToolCall(string Id, string Name, JsonObject Arguments);

    public sealed record ToolResult(string CallId, string Name, string Content, bool IsError);

    /// <summary>The conversation, independent of the provider.</summary>
    public abstract record ChatTurn;

    /// <summary>
    /// The person's message, with HoboLibris's state (screen, connections, counts) as it was when they sent it.  The state
    /// goes with the message rather than in the system prompt, so the system prompt and every earlier turn stay the same
    /// from step to step and a hosted service can read them from its cache (#77).  A model on this computer is sent the
    /// turns without it and has the state in its prompt instead (AssistantSession.ShortPrompt).
    /// </summary>
    public sealed record UserTurn(string Text, string? State = null) : ChatTurn
    {
        /// <summary>
        /// The message as the model is sent it: what the person wrote, then the state when there is one.  Put first, the
        /// context led qwen3:8b to give an empty reply in Mycelia (26 November 2026).
        /// </summary>
        public string ForModel => State is null ? Text : $"{Text}\n\n[From HoboLibris, not the person: the state when this message was sent (JSON): {State}]";
    }

    public sealed record AssistantTurn(string Text, IReadOnlyList<ToolCall> ToolCalls) : ChatTurn;

    public sealed record ToolResultsTurn(IReadOnlyList<ToolResult> Results) : ChatTurn;

    public sealed record ChatReply(string Text, IReadOnlyList<ToolCall> ToolCalls, string StopReason, int InputTokens, int OutputTokens)
    {
        /// <summary>Token counts and times, where the service gives them (Ollama).</summary>
        public ReplyStats? Stats { get; init; }

        /// <summary>
        /// Input tokens read from the service's cache and written to it (#77), apart from InputTokens: Anthropic counts
        /// the three separately; for OpenAI the cached tokens are taken out of InputTokens.  OpenAI writes none it reports.
        /// </summary>
        public int CacheRead { get; init; }
        public int CacheWrite { get; init; }

        /// <summary>One line for the log: the input at full price, from the cache and written to it, and the output.</summary>
        public string Usage => $"input {InputTokens} tokens at full price, {CacheRead} read from the cache, {CacheWrite} written to it, output {OutputTokens}";
    }

    /// <summary>
    /// What one request cost: the tokens read and written, Ollama's own times for loading the model,
    /// reading the prompt and writing the reply, and the time to the first text or tool call and in all,
    /// measured by HoboLibris.
    /// </summary>
    public sealed record ReplyStats(int PromptTokens, int ReplyTokens, TimeSpan Loading, TimeSpan Reading, TimeSpan Writing,
        TimeSpan FirstOutput, TimeSpan Total)
    {
        public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"prompt {PromptTokens} tokens in {Reading.TotalSeconds:0.00} s, reply {ReplyTokens} tokens in {Writing.TotalSeconds:0.00} s, "
            + $"model loaded in {Loading.TotalSeconds:0.00} s, first output after {FirstOutput.TotalSeconds:0.00} s, {Total.TotalSeconds:0.00} s in all");
    }

    public sealed class ChatException : Exception
    {
        public ChatException(string message) : base(message)
        {
        }
    }

    public interface IChatProvider
    {
        /// <summary>
        /// Sends the conversation and returns the reply.  A service that streams calls onText with each
        /// piece of text as it comes; the reply still holds the whole text.
        /// </summary>
        Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken cancel,
            Action<string>? onText = null);
    }

    /// <summary>What a request needs.  ContextSize and Think are for Ollama only.</summary>
    public sealed record ChatProviderOptions(ModelProvider Provider, string Model, string ApiKey, string Endpoint, int MaxTokens,
        int ContextSize = AssistantSettings.DefaultOllamaContext, bool Think = false);

    /// <summary>The AI services the Assistant talks to.  Ported from Mycelia's Ai/Assistant/ChatProviders.cs.</summary>
    public static class ChatProviders
    {
        public const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";
        public const string AnthropicVersion = "2023-06-01";
        public const string OpenAiEndpoint = "https://api.openai.com/v1";

        /// <summary>Suggested model names per provider (any name can be typed).</summary>
        public static IReadOnlyList<string> SuggestedModels(ModelProvider provider) => provider switch
        {
            ModelProvider.Anthropic => new[] { "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5", "claude-fable-5-2" },
            ModelProvider.OpenAI => new[] { "gpt-5", "gpt-5-mini", "gpt-4.1", "gpt-4o" },
            _ => new[] { "llama3.1", "qwen2.5", "gemini-2.5-pro", "gemini-2.5-flash", "mistral-large-latest" },
        };

        public static string DefaultModel(ModelProvider provider) => SuggestedModels(provider)[0];

        /// <summary>Base addresses offered for OpenAI-compatible services.</summary>
        public static IReadOnlyList<string> SuggestedEndpoints { get; } = new[]
        {
            "http://localhost:11434/v1",
            "http://localhost:1234/v1",
            "https://generativelanguage.googleapis.com/v1beta/openai",
            "https://api.mistral.ai/v1",
        };

        public static bool NeedsKey(ModelProvider provider, string endpoint) =>
            provider != ModelProvider.OpenAICompatible || !IsLocalAddress(endpoint);

        /// <summary>Whether requests stay on this computer (a local OpenAI-compatible service such as Ollama).</summary>
        public static bool IsLocal(ModelProvider provider, string endpoint) => !NeedsKey(provider, endpoint);

        private static bool IsLocalAddress(string endpoint) =>
            endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase) || endpoint.Contains("127.0.0.1");

        public static IChatProvider Create(ChatProviderOptions options) => options.Provider switch
        {
            ModelProvider.Anthropic => new AnthropicChatProvider(options),
            // Ollama on this computer: its own API, which sets the context size and thinking and streams.
            _ when OllamaServer.IsOllamaEndpoint(options.Provider, options.Endpoint) => new OllamaChatProvider(options),
            _ => new OpenAiChatProvider(options),
        };

        internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

        /// <summary>
        /// The models a service offers, read from it: Anthropic's and OpenAI's lists with the person's
        /// key (OpenAI's chat models only), and an OpenAI-compatible service's installed models (Ollama,
        /// LM Studio: GET {base}/models).  Reached is false when the service couldn't be reached or
        /// refused the key within a few seconds; then the Model list stays empty.
        /// </summary>
        public static async Task<(bool Reached, IReadOnlyList<string> Models)> ListModelsAsync(
            ModelProvider provider, string endpoint, string apiKey, CancellationToken cancel = default)
        {
            if (NeedsKey(provider, endpoint) && apiKey.Length == 0)
            {
                return (false, Array.Empty<string>());
            }
            var url = ModelsUrl(provider, endpoint);
            if (url == null)
            {
                return (false, Array.Empty<string>());
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (provider == ModelProvider.Anthropic)
                {
                    request.Headers.Add("x-api-key", apiKey);
                    request.Headers.Add("anthropic-version", AnthropicVersion);
                }
                else if (apiKey.Length > 0)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
                using var response = await Http.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, Array.Empty<string>());
                }
                var models = ParseModels(await response.Content.ReadAsStringAsync(timeout.Token));
                if (provider == ModelProvider.OpenAI)
                {
                    models = models.Where(IsOpenAiChatModel).ToList();
                }
                return (true, models);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return (false, Array.Empty<string>());
            }
        }

        /// <summary>Where a service lists its models: the base address (the chat address without its last part) plus /models.</summary>
        public static Uri? ModelsUrl(ModelProvider provider, string endpoint)
        {
            var given = endpoint.Trim().TrimEnd('/');
            string baseUrl = provider switch
            {
                ModelProvider.Anthropic => given.Length == 0 ? "https://api.anthropic.com/v1" : Strip(given, "/messages"),
                ModelProvider.OpenAI => given.Length == 0 ? OpenAiEndpoint : Strip(given, "/chat/completions"),
                _ => Strip(given, "/chat/completions"),
            };
            return baseUrl.Length > 0 && Uri.TryCreate(baseUrl + "/models" + (provider == ModelProvider.Anthropic ? "?limit=100" : ""), UriKind.Absolute, out var url)
                ? url
                : null;
        }

        private static string Strip(string url, string tail) =>
            url.EndsWith(tail, StringComparison.OrdinalIgnoreCase) ? url[..^tail.Length] : url;

        /// <summary>OpenAI lists every model it has (speech, images, embeddings); the Assistant needs a chat model.</summary>
        public static bool IsOpenAiChatModel(string id) =>
            (id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
             || id.StartsWith("o3", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
             || id.StartsWith("chatgpt", StringComparison.OrdinalIgnoreCase))
            && !new[] { "audio", "realtime", "transcribe", "tts", "image", "search", "embedding", "instruct" }
                .Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase));

        /// <summary>The ids in an OpenAI-style model list ({"data":[{"id":"qwen3:8b"}]}), sorted.</summary>
        public static IReadOnlyList<string> ParseModels(string json)
        {
            try
            {
                return (JsonNode.Parse(json)?["data"] as JsonArray ?? new JsonArray())
                    .Select(m => (m?["id"] as JsonValue)?.ToString() ?? "")
                    .Where(id => id.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>A token count from a service's usage, 0 when it isn't there or isn't a number.</summary>
        internal static int Count(JsonObject? usage, string name) =>
            usage?[name] is JsonValue value && value.TryGetValue(out int count) ? count : 0;

        internal static async Task<JsonObject> PostAsync(HttpRequestMessage request, string providerName, CancellationToken cancel)
        {
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, cancel);
            }
            catch (HttpRequestException ex)
            {
                throw new ChatException(string.Format(Strings.Asst_ProviderUnreachable, providerName, ex.Message));
            }
            catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new ChatException(string.Format(Strings.Asst_ProviderTimeout, providerName));
            }
            using (response)
            {
                string body = await response.Content.ReadAsStringAsync(cancel);
                JsonObject? json = null;
                try
                {
                    json = JsonNode.Parse(body) as JsonObject;
                }
                catch (JsonException)
                {
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw Error(providerName, (int)response.StatusCode, body);
                }
                return json ?? throw new ChatException(string.Format(Strings.Asst_ProviderNotJson, providerName));
            }
        }

        /// <summary>A service's refusal, with the message from its answer ({"error":{"message":...}} or {"error":"..."}).</summary>
        public static ChatException Error(string providerName, int status, string body)
        {
            JsonObject? json = null;
            try
            {
                json = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
            }
            string message = ((json?["error"] as JsonObject)?["message"] as JsonValue)?.ToString()
                ?? (json?["error"] as JsonValue)?.ToString()
                ?? json?["error"]?.ToString()
                ?? (body.Length > 300 ? body[..300] + "…" : body);
            var format = status == 401 ? Strings.Asst_ProviderErrorKey : Strings.Asst_ProviderError;
            return new ChatException(string.Format(format, providerName, status, message));
        }
    }

    /// <summary>Anthropic Messages API with tool use (tool_use and tool_result content blocks).</summary>
    public sealed class AnthropicChatProvider : IChatProvider
    {
        private readonly ChatProviderOptions _options;

        public AnthropicChatProvider(ChatProviderOptions options) => _options = options;

        /// <summary>The request: the system prompt, the conversation and the tools, with the cache marks.</summary>
        public static JsonObject Body(ChatProviderOptions options, string system, IReadOnlyList<ChatTurn> turns, IReadOnlyList<ToolSpec> tools)
        {
            var messages = new JsonArray();
            void Add(string role, JsonArray content)
            {
                // Consecutive turns of one role are merged into one message.
                if (messages.Count > 0 && messages[^1]!["role"]!.GetValue<string>() == role)
                {
                    var existing = (JsonArray)messages[^1]!["content"]!;
                    foreach (var block in content.ToList())
                    {
                        content.Remove(block);
                        existing.Add(block);
                    }
                    return;
                }
                messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }

            foreach (var turn in turns)
            {
                switch (turn)
                {
                    case UserTurn user:
                        Add("user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = user.ForModel } });
                        break;
                    case AssistantTurn assistant:
                        var blocks = new JsonArray();
                        if (assistant.Text.Length > 0)
                        {
                            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = assistant.Text });
                        }
                        foreach (var call in assistant.ToolCalls)
                        {
                            blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = call.Arguments.DeepClone() });
                        }
                        if (blocks.Count > 0)
                        {
                            Add("assistant", blocks);
                        }
                        break;
                    case ToolResultsTurn results:
                        var resultBlocks = new JsonArray();
                        foreach (var result in results.Results)
                        {
                            resultBlocks.Add(new JsonObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = result.CallId,
                                ["content"] = result.Content.Length == 0 ? "(no output)" : result.Content,
                                ["is_error"] = result.IsError,
                            });
                        }
                        Add("user", resultBlocks);
                        break;
                }
            }

            // Prompt caching (#77), as Mycelia does it: the service reads the tools, then the system prompt, then the messages.
            // A mark at the end of the system prompt keeps the tools and the prompt, which don't change within a conversation;
            // it lasts an hour, so they are read rather than written again after a pause of more than five minutes.  A mark on
            // the last block of the last message keeps the conversation so far, so the next step reads it all from the cache.
            // The longer mark has to come first.
            if (messages.Count > 0 && messages[^1]!["content"] is JsonArray { Count: > 0 } last)
            {
                last[^1]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            }
            var body = new JsonObject
            {
                ["model"] = options.Model,
                ["max_tokens"] = options.MaxTokens,
                ["system"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = system,
                        ["cache_control"] = new JsonObject { ["type"] = "ephemeral", ["ttl"] = "1h" },
                    },
                },
                ["messages"] = messages,
            };
            if (tools.Count > 0)
            {
                body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.InputSchema.DeepClone(),
                }).ToArray());
            }
            return body;
        }

        public async Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken cancel,
            Action<string>? onText = null)
        {
            string url = string.IsNullOrWhiteSpace(_options.Endpoint) ? ChatProviders.AnthropicEndpoint : _options.Endpoint.Trim();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(Body(_options, system, turns, tools).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-api-key", _options.ApiKey);
            request.Headers.Add("anthropic-version", ChatProviders.AnthropicVersion);

            var json = await ChatProviders.PostAsync(request, "Anthropic", cancel);
            var text = new StringBuilder();
            var calls = new List<ToolCall>();
            if (json["content"] is JsonArray content)
            {
                foreach (var block in content.OfType<JsonObject>())
                {
                    switch (block["type"]?.GetValue<string>())
                    {
                        case "text":
                            text.Append(block["text"]?.GetValue<string>());
                            break;
                        case "tool_use":
                            calls.Add(new ToolCall(block["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                                block["name"]?.GetValue<string>() ?? string.Empty,
                                block["input"]?.DeepClone() as JsonObject ?? new JsonObject()));
                            break;
                    }
                }
            }
            var usage = json["usage"] as JsonObject;
            return new ChatReply(text.ToString(), calls, json["stop_reason"]?.GetValue<string>() ?? string.Empty,
                ChatProviders.Count(usage, "input_tokens"), ChatProviders.Count(usage, "output_tokens"))
            {
                CacheRead = ChatProviders.Count(usage, "cache_read_input_tokens"),
                CacheWrite = ChatProviders.Count(usage, "cache_creation_input_tokens"),
            };
        }
    }

    /// <summary>OpenAI chat completions with function tools; also any compatible service.</summary>
    public sealed class OpenAiChatProvider : IChatProvider
    {
        private readonly ChatProviderOptions _options;

        public OpenAiChatProvider(ChatProviderOptions options) => _options = options;

        private string ProviderName => _options.Provider == ModelProvider.OpenAI ? "OpenAI" : Strings.Asst_AiService;

        public async Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken cancel,
            Action<string>? onText = null)
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
            foreach (var turn in turns)
            {
                switch (turn)
                {
                    case UserTurn user:
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = user.ForModel });
                        break;
                    case AssistantTurn assistant:
                        // A reply with neither text nor tool calls (one cut short by the token limit) is refused by the API
                        // ("content: expected a string, got null"), and it would block every later request: leave it out,
                        // as the Anthropic side does.
                        if (assistant.Text.Length == 0 && assistant.ToolCalls.Count == 0)
                        {
                            break;
                        }
                        var message = new JsonObject { ["role"] = "assistant", ["content"] = assistant.Text.Length > 0 ? assistant.Text : null };
                        if (assistant.ToolCalls.Count > 0)
                        {
                            message["tool_calls"] = new JsonArray(assistant.ToolCalls.Select(c => (JsonNode)new JsonObject
                            {
                                ["id"] = c.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.ToJsonString() },
                            }).ToArray());
                        }
                        messages.Add(message);
                        break;
                    case ToolResultsTurn results:
                        foreach (var result in results.Results)
                        {
                            messages.Add(new JsonObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = result.CallId,
                                ["content"] = (result.IsError ? "ERROR: " : string.Empty) + (result.Content.Length == 0 ? "(no output)" : result.Content),
                            });
                        }
                        break;
                }
            }

            var body = new JsonObject { ["model"] = _options.Model, ["messages"] = messages };
            // OpenAI's newer models take max_completion_tokens; compatible services still read max_tokens.
            body[_options.Provider == ModelProvider.OpenAI ? "max_completion_tokens" : "max_tokens"] = _options.MaxTokens;
            if (tools.Count > 0)
            {
                body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.InputSchema.DeepClone(),
                    },
                }).ToArray());
            }

            string baseUrl = string.IsNullOrWhiteSpace(_options.Endpoint) ? ChatProviders.OpenAiEndpoint : _options.Endpoint.Trim().TrimEnd('/');
            string url = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (_options.ApiKey.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            var json = await ChatProviders.PostAsync(request, ProviderName, cancel);
            var choice = (json["choices"] as JsonArray)?.FirstOrDefault() as JsonObject
                ?? throw new ChatException(string.Format(Strings.Asst_ProviderNoAnswer, ProviderName));
            var reply = choice["message"] as JsonObject ?? new JsonObject();
            var calls = new List<ToolCall>();
            if (reply["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var call in toolCalls.OfType<JsonObject>())
                {
                    string arguments = call["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                    JsonObject parsed;
                    try
                    {
                        parsed = JsonNode.Parse(arguments.Length == 0 ? "{}" : arguments) as JsonObject ?? new JsonObject();
                    }
                    catch (JsonException)
                    {
                        parsed = new JsonObject();
                    }
                    calls.Add(new ToolCall(call["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                        call["function"]?["name"]?.GetValue<string>() ?? string.Empty, parsed));
                }
            }
            // OpenAI caches the start of a request by itself (from 1,024 tokens); the cached part is within prompt_tokens.
            var usage = json["usage"] as JsonObject;
            int cached = ChatProviders.Count(usage?["prompt_tokens_details"] as JsonObject, "cached_tokens");
            return new ChatReply(reply["content"]?.GetValue<string>() ?? string.Empty, calls,
                choice["finish_reason"]?.GetValue<string>() ?? string.Empty,
                ChatProviders.Count(usage, "prompt_tokens") - cached, ChatProviders.Count(usage, "completion_tokens"))
            {
                CacheRead = cached,
            };
        }
    }
}
