using System.Diagnostics;
using System.Text.Json.Nodes;
using HoboLibris.Assistant;

namespace HoboLibris.Tests
{
    /// <summary>A model that fails with the message it was given.</summary>
    internal sealed class FailingModel : IChatProvider
    {
        private readonly string _message;

        public FailingModel(string message) => _message = message;

        public Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken cancel,
            Action<string>? onText = null) => throw new ChatException(_message);
    }

    public class OllamaChatTests
    {
        private static ChatProviderOptions Options(int context = 8192, bool think = false) =>
            new(ModelProvider.OpenAICompatible, "qwen3:8b", "", "http://localhost:11434/v1", 16000, context, think);

        [Theory]
        [InlineData("http://localhost:11434/v1", "http://localhost:11434/api/chat")]
        [InlineData("http://127.0.0.1:11434/v1/", "http://127.0.0.1:11434/api/chat")]
        [InlineData("http://localhost:11434", "http://localhost:11434/api/chat")]
        public void TheChatAddressIsOnTheSameHostAndPort(string endpoint, string expected)
        {
            Assert.Equal(expected, OllamaChatProvider.ChatUrl(endpoint).ToString());
        }

        [Fact]
        public void OllamaOnThisComputerGetsItsOwnApi()
        {
            Assert.IsType<OllamaChatProvider>(ChatProviders.Create(Options()));
            Assert.IsType<OpenAiChatProvider>(ChatProviders.Create(Options() with { Endpoint = "http://localhost:1234/v1" }));
            Assert.IsType<OpenAiChatProvider>(ChatProviders.Create(Options() with { Endpoint = "https://api.mistral.ai/v1" }));
            Assert.IsType<AnthropicChatProvider>(ChatProviders.Create(Options() with { Provider = ModelProvider.Anthropic }));
        }

        [Fact]
        public void TheRequestSetsTheContextThinkingAndStreaming()
        {
            var body = OllamaChatProvider.Body(Options(context: 12288), "Be brief.", new ChatTurn[] { new UserTurn("Hello") }, Array.Empty<ToolSpec>());

            Assert.Equal(12288, body["options"]!["num_ctx"]!.GetValue<int>());
            Assert.Equal(16000, body["options"]!["num_predict"]!.GetValue<int>());
            Assert.False(body["think"]!.GetValue<bool>());
            Assert.False(body["truncate"]!.GetValue<bool>());
            Assert.True(body["stream"]!.GetValue<bool>());
            Assert.Null(body["tools"]);
            var messages = body["messages"]!.AsArray();
            Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
            Assert.Equal("Hello", messages[1]!["content"]!.GetValue<string>());

            Assert.True(OllamaChatProvider.Body(Options(think: true), "", Array.Empty<ChatTurn>(), Array.Empty<ToolSpec>())["think"]!.GetValue<bool>());
        }

        [Fact]
        public void ToolCallsAndResultsAreInOllamasForm()
        {
            var call = new ToolCall("call_1", "search_ebooks", new JsonObject { ["query"] = "Islam" });
            var turns = new ChatTurn[]
            {
                new UserTurn("Find books"),
                new AssistantTurn("", new[] { call }),
                new ToolResultsTurn(new[] { new ToolResult("call_1", "search_ebooks", "Books found: 76", false) }),
                new AssistantTurn("", Array.Empty<ToolCall>()),
            };
            var tools = new[] { new ToolSpec("search_ebooks", "Search", new JsonObject { ["type"] = "object" }) };

            var body = OllamaChatProvider.Body(Options(), "", turns, tools);

            var messages = body["messages"]!.AsArray();
            Assert.Equal(4, messages.Count);
            var sent = messages[2]!["tool_calls"]![0]!;
            Assert.Equal("call_1", sent["id"]!.GetValue<string>());
            Assert.Equal("Islam", sent["function"]!["arguments"]!["query"]!.GetValue<string>());
            Assert.Equal("tool", messages[3]!["role"]!.GetValue<string>());
            Assert.Equal("search_ebooks", messages[3]!["tool_name"]!.GetValue<string>());
            Assert.Equal("call_1", messages[3]!["tool_call_id"]!.GetValue<string>());
            Assert.Equal("Books found: 76", messages[3]!["content"]!.GetValue<string>());
            Assert.Equal("search_ebooks", body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        }

        [Fact]
        public void TheStreamGivesTheTextToolCallsAndFigures()
        {
            var stream = new OllamaStream(Stopwatch.StartNew());
            var pieces = new[]
            {
                """{"message":{"role":"assistant","content":"Hel"},"done":false}""",
                "",
                """{"message":{"role":"assistant","content":"lo"},"done":false}""",
                """{"message":{"role":"assistant","content":"","tool_calls":[{"id":"call_a","function":{"index":0,"name":"list_books","arguments":{"limit":5}}}]},"done":false}""",
                """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","load_duration":2000000000,"prompt_eval_count":3500,"prompt_eval_duration":1500000000,"eval_count":40,"eval_duration":800000000}""",
            }.Select(stream.Add).ToList();

            var reply = stream.Finish();

            Assert.Equal(new[] { "Hel", "", "lo", "", "" }, pieces);
            Assert.Equal("Hello", reply.Text);
            Assert.Equal("stop", reply.StopReason);
            var call = Assert.Single(reply.ToolCalls);
            Assert.Equal(("call_a", "list_books", 5), (call.Id, call.Name, call.Arguments["limit"]!.GetValue<int>()));
            Assert.Equal((3500, 40), (reply.InputTokens, reply.OutputTokens));
            var stats = reply.Stats!;
            Assert.Equal(TimeSpan.FromSeconds(2), stats.Loading);
            Assert.Equal(TimeSpan.FromSeconds(1.5), stats.Reading);
            Assert.Equal(TimeSpan.FromSeconds(0.8), stats.Writing);
            Assert.Contains("prompt 3500 tokens in 1.50 s, reply 40 tokens in 0.80 s", stats.ToString());
        }

        [Fact]
        public void ArgumentsGivenAsTextAreRead()
        {
            var stream = new OllamaStream(Stopwatch.StartNew());
            stream.Add("""{"message":{"content":"","tool_calls":[{"function":{"name":"list_books","arguments":"{\"limit\":3}"}}]},"done":false}""");
            stream.Add("""{"message":{"content":""},"done":true,"done_reason":"stop"}""");

            var call = Assert.Single(stream.Finish().ToolCalls);

            Assert.Equal(3, call.Arguments["limit"]!.GetValue<int>());
            Assert.NotEmpty(call.Id);
        }

        [Fact]
        public void AnErrorInTheStreamOrNoEndIsReported()
        {
            var failing = new OllamaStream(Stopwatch.StartNew());
            Assert.Contains("out of memory", Assert.Throws<ChatException>(() => failing.Add("""{"error":"out of memory"}""")).Message);

            var cut = new OllamaStream(Stopwatch.StartNew());
            cut.Add("""{"message":{"content":"Hel"},"done":false}""");
            Assert.Throws<ChatException>(() => cut.Finish());

            Assert.Throws<ChatException>(() => new OllamaStream(Stopwatch.StartNew()).Add("not json"));
        }

        [Fact]
        public void OllamasRefusalKeepsItsMessage()
        {
            var error = ChatProviders.Error("Ollama", 400,
                """{"error":"request (3024 tokens) exceeds the available context size (2048 tokens), try increasing it"}""");

            Assert.Contains("400", error.Message);
            Assert.Contains("exceeds the available context size (2048 tokens)", error.Message);
        }

        [Fact]
        public async Task AConversationLargerThanTheContextIsReported()
        {
            var host = new TestHost();
            host.Settings.OllamaContext = 4096;
            var session = new AssistantSession(host, null)
            {
                ProviderFactory = _ => new FailingModel("Ollama answered 400: request (5100 tokens) exceeds the available context size (4096 tokens), try increasing it"),
            };

            await session.SendAsync("Find books");

            var notices = session.Items.OfType<NoticeMessage>().ToList();
            Assert.Equal(2, notices.Count);
            Assert.True(notices[0].IsError);
            Assert.Contains("4,096", notices[1].Text);
        }

        [Fact]
        public async Task AStreamedReplyIsOneMessageWithTheWholeText()
        {
            var host = new TestHost();
            var model = new ScriptedModel(ScriptedModel.Text("  You have three books.  ")) { Streams = true };
            var session = new AssistantSession(host, null) { ProviderFactory = _ => model };

            await session.SendAsync("How many books?");

            var answer = Assert.Single(session.Items.OfType<AssistantMessage>());
            Assert.Equal("You have three books.", answer.Text);
        }

        [Fact]
        public void TheContextSentIsKeptInRange()
        {
            var settings = new AssistantSettings { OllamaContext = 100, OllamaThink = true };
            Assert.Equal((AssistantSettings.MinOllamaContext, true), (settings.ProviderOptions(256).ContextSize, settings.ProviderOptions(256).Think));

            settings.OllamaContext = 1_000_000;
            Assert.Equal(AssistantSettings.MaxOllamaContext, settings.ProviderOptions(256).ContextSize);

            Assert.Equal(AssistantSettings.DefaultOllamaContext, new AssistantSettings().OllamaContext);
        }
    }
}
