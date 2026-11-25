using System.Text.Json.Nodes;
using HoboLibris.Assistant;
using HoboLibris.Assistant.Sources;

namespace HoboLibris.Tests
{
    /// <summary>A host whose list downloads are recorded.</summary>
    internal sealed class DownloadingHost : IAssistantHost
    {
        private readonly TestHost _inner = new();
        public AssistantSettings Settings => _inner.Settings;
        public void SaveSettings() => _inner.SaveSettings();
        public IReadOnlyList<AssistantTool> Tools => _inner.Tools;
        public JsonObject Context() => _inner.Context();
        public string ContextLine() => _inner.ContextLine();
        public TestHost Inner => _inner;
        public List<string> Downloads { get; } = new();

        public Task<ToolOutcome> DownloadBookAsync(EbookResult book)
        {
            Downloads.Add(book.Id);
            return Task.FromResult(new ToolOutcome("{}", Summary: "Saved"));
        }
    }

    public class SmallModelTests
    {
        private static EbookResult Book(int n) => new()
        {
            Id = $"pg-{n}", Source = "gutenberg", Title = $"Book {n}", Authors = "Someone", Language = "en",
            PageUrl = new Uri($"https://www.gutenberg.org/ebooks/{n}"),
        };

        private static ToolResultsTurn Result(string id, string content) => new(new[] { new ToolResult(id, "search_ebooks", content, false) });

        // ---- The list of books a search found ----------------------------------------------------

        [Fact]
        public void TheModelGetsAFewBooksAndThePersonGetsThemAll()
        {
            var books = Enumerable.Range(1, 76).Select(Book).ToList();

            var outcome = SourceTools.Found(books, 76, 5, "Project Gutenberg");

            var json = JsonNode.Parse(outcome.Content)!;
            Assert.Equal(5, json["books"]!.AsArray().Count);
            Assert.Equal(76, json["total"]!.GetValue<int>());
            Assert.Equal(76, json["in_list"]!.GetValue<int>());
            Assert.Contains("Don't list them again", json["note"]!.GetValue<string>());
            var list = Assert.IsType<SearchResults>(outcome.Show);
            Assert.Equal(76, list.Rows.Count);
            Assert.Equal("76 found in Project Gutenberg", list.Heading);
        }

        [Fact]
        public void APageOfAnOnlineSearchSaysHowManyThereAre()
        {
            var outcome = SourceTools.Found(Enumerable.Range(1, 40).Select(Book).ToList(), 2113, 10, "Open Library");

            Assert.Equal("40 of 2,113 found in Open Library", Assert.IsType<SearchResults>(outcome.Show).Heading);
        }

        [Fact]
        public void NothingFoundShowsNoList()
        {
            var outcome = SourceTools.Found(Array.Empty<EbookResult>(), 0, 10, "Project Gutenberg");

            Assert.Null(outcome.Show);
            Assert.Equal(0, JsonNode.Parse(outcome.Content)!["total"]!.GetValue<int>());
        }

        [Fact]
        public void TheListShowsTenUntilShowAll()
        {
            var list = new SearchResults("76 found", Enumerable.Range(1, 76).Select(Book));

            Assert.Equal(10, list.Shown.Count());
            Assert.True(list.HasMore);
            list.ShowEverythingCommand.Execute(null);
            Assert.Equal(76, list.Shown.Count());
            Assert.False(list.HasMore);
        }

        [Fact]
        public async Task AListIsShownUnderTheStepKeptAndDownloadsThroughTheHost()
        {
            var host = new DownloadingHost();
            var tool = host.Inner.AddTool("search_ebooks");
            host.Inner.ToolList[0] = new AssistantTool
            {
                Name = tool.Name, Title = tool.Title, Description = tool.Description, Schema = tool.Schema,
                Run = (_, _) => Task.FromResult(SourceTools.Found(Enumerable.Range(1, 12).Select(Book).ToList(), 12, 5, "Project Gutenberg")),
            };
            var path = Path.Combine(TestFolder.Fresh("results-list"), "conversation.json");
            var model = new ScriptedModel(ScriptedModel.Call("search_ebooks"), ScriptedModel.Text("Twelve books were found."));
            var session = new AssistantSession(host, path) { ProviderFactory = _ => model };

            await session.SendAsync("Find books");

            var items = session.Items.ToList();
            int step = items.FindIndex(i => i is ToolActivity);
            var list = Assert.IsType<SearchResults>(items[step + 1]);
            Assert.Equal(12, list.Rows.Count);

            var restored = new AssistantSession(host, path).Items.OfType<SearchResults>().Single();
            Assert.Equal(12, restored.Rows.Count);
            Assert.Equal(("pg-3", "Book 3", "https://www.gutenberg.org/ebooks/3"),
                (restored.Rows[2].Book.Id, restored.Rows[2].Title, restored.Rows[2].Book.PageUrl!.AbsoluteUri));
            await restored.Rows[2].DownloadCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "pg-3" }, host.Downloads);
            Assert.Equal(SearchRowState.Done, restored.Rows[2].State);
        }

        [Fact]
        public void ARestoredOnlineRowWithoutItsAddressCantBeDownloaded()
        {
            var list = new SearchResults("1 found", new[] { new EbookResult { Id = "gb-x", Source = "google_books", Title = "T", DownloadUrl = new Uri("https://example.org/a.epub") } });

            var restored = SearchResults.FromSaved(list.Heading, list.ToSaved(), DateTime.Now);

            Assert.True(list.Rows[0].Downloadable);
            Assert.False(restored.Rows[0].Downloadable);
            Assert.DoesNotContain("example.org", list.ToSaved());
        }

        // ---- Fitting the conversation into the context ---------------------------------------------

        [Fact]
        public void TheBudgetKeepsRoomForTheReply()
        {
            Assert.Equal(6144, ContextFit.Budget(8192));
            Assert.Equal(1536, ContextFit.Budget(2048));
            Assert.Equal(3072, ContextFit.Budget(4096));
        }

        [Fact]
        public void AConversationThatFitsIsSentWhole()
        {
            var turns = new ChatTurn[] { new UserTurn("Hello"), new AssistantTurn("Hi", Array.Empty<ToolCall>()), new UserTurn("Thanks") };

            var sent = ContextFit.Fit(turns, 100, 1000, out bool trimmed);

            Assert.False(trimmed);
            Assert.Equal(turns, sent);
        }

        [Fact]
        public void OldToolResultsGoFirst()
        {
            var big = new string('x', 3000);
            var call = new AssistantTurn("", new[] { new ToolCall("c1", "search_ebooks", new JsonObject()) });
            var turns = new ChatTurn[] { new UserTurn("Find books"), call, Result("c1", big), new AssistantTurn("Found some.", Array.Empty<ToolCall>()), new UserTurn("Thanks") };

            var sent = ContextFit.Fit(turns, 100, 600, out bool trimmed);

            Assert.True(trimmed);
            Assert.Equal(5, sent.Count);
            Assert.Equal(ContextFit.LeftOut, Assert.IsType<ToolResultsTurn>(sent[2]).Results[0].Content);
            Assert.Equal("Thanks", Assert.IsType<UserTurn>(sent[4]).Text);
        }

        [Fact]
        public void ThenOldExchangesGoOldestFirst()
        {
            string Long(int n) => n + new string('y', 900);
            var turns = new List<ChatTurn>();
            for (int i = 0; i < 4; i++)
            {
                turns.Add(new UserTurn(Long(i)));
                turns.Add(new AssistantTurn(Long(i), Array.Empty<ToolCall>()));
            }
            turns.Add(new UserTurn("Now this"));

            var sent = ContextFit.Fit(turns, 100, 1400, out bool trimmed);

            Assert.True(trimmed);
            Assert.IsType<UserTurn>(sent[0]);
            Assert.StartsWith("2", ((UserTurn)sent[0]).Text);
            Assert.Equal("Now this", ((UserTurn)sent[^1]).Text);
        }

        [Fact]
        public void TheCurrentRequestIsCutNotLeftOut()
        {
            var call = new AssistantTurn("", new[] { new ToolCall("c1", "list_books", new JsonObject()) });
            var turns = new ChatTurn[] { new UserTurn("Old"), new AssistantTurn("Old reply", Array.Empty<ToolCall>()),
                new UserTurn("Which books are ready to send?"), call, Result("c1", new string('x', 27000)) };

            var sent = ContextFit.Fit(turns, 2000, 6144, out bool trimmed);

            Assert.True(trimmed);
            Assert.Equal(3, sent.Count);
            Assert.Equal("Which books are ready to send?", Assert.IsType<UserTurn>(sent[0]).Text);
            var content = Assert.IsType<ToolResultsTurn>(sent[2]).Results[0].Content;
            Assert.EndsWith(ContextFit.CutShort, content);
            Assert.True(2000 + sent.Sum(ContextFit.Estimate) <= 6144);
            Assert.True(content.Length > 10000);
        }

        [Fact]
        public void TheFirstRequestIsCutToo()
        {
            var call = new AssistantTurn("", new[] { new ToolCall("c1", "list_books", new JsonObject()) });
            var turns = new ChatTurn[] { new UserTurn("List my books"), call, Result("c1", new string('x', 27000)) };

            var sent = ContextFit.Fit(turns, 2000, 6144, out bool trimmed);

            Assert.True(trimmed);
            Assert.EndsWith(ContextFit.CutShort, Assert.IsType<ToolResultsTurn>(sent[2]).Results[0].Content);
        }

        [Fact]
        public async Task OllamaIsSentWhatFitsAndThePaneSaysSo()
        {
            var host = new TestHost();
            host.Settings.OllamaContext = 2048;
            var model = new ScriptedModel(ScriptedModel.Text(new string('a', 2500)), ScriptedModel.Text(new string('b', 2500)), ScriptedModel.Text("Done."));
            var session = new AssistantSession(host, null) { ProviderFactory = _ => model };

            await session.SendAsync("First " + new string('q', 600));
            await session.SendAsync("Second");
            await session.SendAsync("Third");

            Assert.Equal(6, session.Turns.Count);
            Assert.True(model.Sent[2].Count < 5);
            Assert.Equal("Third", ((UserTurn)model.Sent[2][^1]).Text);
            Assert.Contains(session.Items.OfType<NoticeMessage>(), n => n.Text.Contains("2,048"));
        }

        [Fact]
        public async Task AHostedServiceIsSentEverything()
        {
            var host = new TestHost();
            host.Settings.Endpoint = "https://api.mistral.ai/v1";
            host.Settings.SetApiKey(ModelProvider.OpenAICompatible, "test-key");
            var model = new ScriptedModel(ScriptedModel.Text(new string('a', 30000)), ScriptedModel.Text("Done."));
            var session = new AssistantSession(host, null) { ProviderFactory = _ => model };

            await session.SendAsync("First");
            await session.SendAsync("Second");

            Assert.Equal(3, model.Sent[1].Count);
            Assert.DoesNotContain(session.Items.OfType<NoticeMessage>(), n => !n.IsError);
        }

        // ---- Tool groups and the shorter prompt --------------------------------------------------

        private static TestHost HostWithGroups()
        {
            var host = new TestHost();
            foreach (var name in new[] { "list_books", "list_collections", "list_send", "list_kindle_books", "search_ebooks", "show_screen" })
            {
                host.AddTool(name);
            }
            return host;
        }

        [Fact]
        public async Task AModelOnThisComputerGetsTheScreensToolsAndAShortPrompt()
        {
            var host = HostWithGroups();
            var model = new ScriptedModel(ScriptedModel.Text("Hello."));
            var session = new AssistantSession(host, null) { ProviderFactory = _ => model };

            await session.SendAsync("Hello");

            Assert.Equal(new[] { "list_books", "search_ebooks", "show_screen", "use_tools" }, model.ToolNames[0]);
            Assert.Contains("call use_tools with collections", model.Systems[0]);
            Assert.DoesNotContain("save_collections, which the person approves", model.Systems[0]);
        }

        [Fact]
        public async Task AHostedModelGetsEveryToolAndTheFullPrompt()
        {
            var host = HostWithGroups();
            host.Settings.Endpoint = "https://api.mistral.ai/v1";
            host.Settings.SetApiKey(ModelProvider.OpenAICompatible, "test-key");
            var model = new ScriptedModel(ScriptedModel.Text("Hello."));
            var session = new AssistantSession(host, null) { ProviderFactory = _ => model };

            await session.SendAsync("Hello");

            Assert.Equal(6, model.ToolNames[0].Count);
            Assert.DoesNotContain("use_tools", model.ToolNames[0]);
            Assert.Contains("How to work:", model.Systems[0]);
        }

        [Fact]
        public async Task UseToolsAddsAGroupForTheRestOfTheConversation()
        {
            var host = HostWithGroups();
            var model = new ScriptedModel(ScriptedModel.Call("use_tools", new JsonObject { ["group"] = "collections" }),
                ScriptedModel.Call("list_collections"), ScriptedModel.Text("No eReader."), ScriptedModel.Text("Still here."));
            var session = new AssistantSession(host, null) { ProviderFactory = _ => model };

            await session.SendAsync("Which collections are there?");
            await session.SendAsync("And now?");

            Assert.DoesNotContain("list_collections", model.ToolNames[0]);
            Assert.Contains("list_collections", model.ToolNames[1]);
            Assert.Contains("list_collections", model.ToolNames[3]);
            Assert.Contains("save_collections, which the person approves", model.Systems[1]);
            Assert.Equal(new[] { "list_collections" }, host.Runs);
        }

        [Fact]
        public async Task AToolOfAGroupNotSentStillRuns()
        {
            var host = HostWithGroups();
            var model = new ScriptedModel(ScriptedModel.Call("list_kindle_books"), ScriptedModel.Text("Two Kindle books."));
            var session = new AssistantSession(host, null) { ProviderFactory = _ => model };

            await session.SendAsync("List my Kindle books");

            Assert.Equal(new[] { "list_kindle_books" }, host.Runs);
            Assert.Contains("list_kindle_books", model.ToolNames[1]);
        }

        [Fact]
        public async Task ClearingTheConversationDropsTheAddedGroups()
        {
            var host = HostWithGroups();
            var model = new ScriptedModel(ScriptedModel.Call("use_tools", new JsonObject { ["group"] = "kindle" }), ScriptedModel.Text("Ready."), ScriptedModel.Text("Hello."));
            var session = new AssistantSession(host, null) { ProviderFactory = _ => model };

            await session.SendAsync("Kindle please");
            session.ClearConversation();
            await session.SendAsync("Hello");

            Assert.Contains("list_kindle_books", model.ToolNames[1]);
            Assert.DoesNotContain("list_kindle_books", model.ToolNames[2]);
        }
    }
}
