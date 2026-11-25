using System.Text.Json;
using System.Text.Json.Nodes;
using HoboLibris.Assistant;
using HoboLibris.Models;

namespace HoboLibris.Tests
{
    /// <summary>A model that gives the replies it was handed, in order, and records what it was sent.</summary>
    internal sealed class ScriptedModel : IChatProvider
    {
        private readonly Queue<ChatReply> _replies;

        public ScriptedModel(params ChatReply[] replies) => _replies = new Queue<ChatReply>(replies);

        public List<int> ToolCounts { get; } = new();

        /// <summary>The names of the tools sent with each request.</summary>
        public List<List<string>> ToolNames { get; } = new();

        /// <summary>The system prompt and the turns sent with each request.</summary>
        public List<string> Systems { get; } = new();
        public List<IReadOnlyList<ChatTurn>> Sent { get; } = new();
        public int Calls { get; private set; }

        /// <summary>Hand the text over a word at a time before the reply, as a streaming service does.</summary>
        public bool Streams { get; init; }

        public Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken cancel,
            Action<string>? onText = null)
        {
            Calls++;
            ToolCounts.Add(tools.Count);
            ToolNames.Add(tools.Select(t => t.Name).ToList());
            Systems.Add(system);
            Sent.Add(turns.ToList());
            var reply = _replies.Count > 0 ? _replies.Dequeue() : Text("(nothing more)");
            if (Streams)
            {
                foreach (var piece in System.Text.RegularExpressions.Regex.Split(reply.Text, @"(?<= )"))
                {
                    onText?.Invoke(piece);
                }
            }
            return Task.FromResult(reply);
        }

        public static ChatReply Text(string text) => new(text, Array.Empty<ToolCall>(), "end_turn", 0, 0);

        public static ChatReply Call(string tool, JsonObject? args = null) =>
            new("", new[] { new ToolCall("call-" + tool, tool, args ?? new JsonObject()) }, "tool_use", 0, 0);
    }

    internal sealed class TestHost : IAssistantHost
    {
        public AssistantSettings Settings { get; } = new() { Provider = ModelProvider.OpenAICompatible, Endpoint = "http://localhost:11434/v1" };
        public int Saves { get; private set; }
        public void SaveSettings() => Saves++;
        public List<AssistantTool> ToolList { get; } = new();
        public IReadOnlyList<AssistantTool> Tools => ToolList;
        public JsonObject Context() => new() { ["screen"] = "back_up" };
        public string ContextLine() => "Kobo";

        /// <summary>A tool that counts its runs and answers "ran".</summary>
        public AssistantTool AddTool(string name, bool changes = false, bool alwaysAsks = false, Func<JsonObject, ToolOutcome?>? check = null)
        {
            var tool = new AssistantTool
            {
                Name = name,
                Title = name,
                Description = name,
                Schema = Schema.Object(),
                Changes = changes,
                AlwaysAsks = alwaysAsks,
                Check = check,
                Run = (_, _) =>
                {
                    Runs.Add(name);
                    return Task.FromResult(new ToolOutcome("ran"));
                },
            };
            ToolList.Add(tool);
            return tool;
        }

        public List<string> Runs { get; } = new();
    }

    public class AssistantTests
    {
        private static AssistantSession Session(TestHost host, ScriptedModel model, string? path = null)
        {
            var session = new AssistantSession(host, path) { ProviderFactory = _ => model };
            return session;
        }

        /// <summary>Waits until the session shows an approval card, then answers it.</summary>
        private static async Task AnswerWhenAsked(AssistantSession session, bool allow, bool always)
        {
            for (int i = 0; i < 200 && !session.Items.OfType<ApprovalRequest>().Any(a => a.IsPending); i++)
            {
                await Task.Delay(10);
            }
            session.Items.OfType<ApprovalRequest>().Single(a => a.IsPending).Answer(allow, always);
        }

        [Fact]
        public async Task RunsAToolAndGivesTheResultBackToTheModel()
        {
            var host = new TestHost();
            host.AddTool("list_books");
            var model = new ScriptedModel(ScriptedModel.Call("list_books"), ScriptedModel.Text("You have no books."));
            var session = Session(host, model);

            await session.SendAsync("What books do I have?");

            Assert.Equal(new[] { "list_books" }, host.Runs);
            Assert.Equal(2, model.Calls);
            Assert.IsType<UserMessage>(session.Items[0]);
            var activity = Assert.IsType<ToolActivity>(session.Items[1]);
            Assert.Equal(ToolActivityState.Done, activity.State);
            Assert.Equal("You have no books.", Assert.IsType<AssistantMessage>(session.Items[2]).Text);
            var results = Assert.IsType<ToolResultsTurn>(session.Turns[2]);
            Assert.Equal("ran", results.Results.Single().Content);
            Assert.False(session.IsBusy);
        }

        [Fact]
        public async Task AToolThatChangesSomethingWaitsAndADeclineIsReported()
        {
            var host = new TestHost();
            host.AddTool("back_up_books", changes: true);
            var model = new ScriptedModel(ScriptedModel.Call("back_up_books"), ScriptedModel.Text("Not backed up."));
            var session = Session(host, model);

            var request = session.SendAsync("Back up everything");
            await AnswerWhenAsked(session, allow: false, always: false);
            await request;

            Assert.Empty(host.Runs);
            Assert.Equal(ToolActivityState.Declined, session.Items.OfType<ToolActivity>().Single().State);
            Assert.True(Assert.IsType<ToolResultsTurn>(session.Turns[2]).Results.Single().IsError);
        }

        [Fact]
        public async Task ADeclineTellsTheModelNothingWasDoneAndToCarryOn()
        {
            var host = new TestHost();
            host.AddTool("send_to_ereader", alwaysAsks: true);
            var model = new ScriptedModel(ScriptedModel.Call("send_to_ereader"), ScriptedModel.Text("Here is the table."));
            var session = Session(host, model);

            var request = session.SendAsync("List them in a table");
            await AnswerWhenAsked(session, allow: false, always: false);
            await request;

            var result = Assert.IsType<ToolResultsTurn>(session.Turns[2]).Results.Single();
            Assert.Equal(AssistantSession.DeclinedResult, result.Content);
            Assert.Contains("nothing was done", result.Content);
            Assert.Contains("Carry on with the rest of their request", result.Content);
        }

        // From a test with qwen3:8b on 24 November 2026: a send with ids from search_ebooks asked
        // "Copy these files (0) to the Kobo eReader".
        [Fact]
        public async Task ACallThatCanDoNothingIsNotPutToThePerson()
        {
            var host = new TestHost();
            host.AddTool("send_to_ereader", alwaysAsks: true,
                check: _ => new ToolOutcome("Nothing to send. These ids matched no book: pg-1234.", true));
            var model = new ScriptedModel(ScriptedModel.Call("send_to_ereader"), ScriptedModel.Text("Those were search results."));
            var session = Session(host, model);

            await session.SendAsync("Send them");

            Assert.Empty(session.Items.OfType<ApprovalRequest>());
            Assert.Empty(host.Runs);
            var activity = session.Items.OfType<ToolActivity>().Single();
            Assert.Equal(ToolActivityState.Failed, activity.State);
            Assert.Equal(HoboLibris.Resources.Strings.Asst_NotPossible, activity.Summary);
            Assert.Contains("pg-1234", activity.Result);
            var result = Assert.IsType<ToolResultsTurn>(session.Turns[2]).Results.Single();
            Assert.True(result.IsError);
            Assert.StartsWith("Nothing to send.", result.Content);
            Assert.Equal(2, model.Calls);
        }

        [Fact]
        public async Task NothingToDoIsReportedWithoutAsking()
        {
            var host = new TestHost();
            host.AddTool("back_up_books", changes: true, check: _ => new ToolOutcome("No books to back up."));
            var model = new ScriptedModel(ScriptedModel.Call("back_up_books"), ScriptedModel.Text("Everything is backed up."));
            var session = Session(host, model);

            await session.SendAsync("Back up everything");

            Assert.Empty(session.Items.OfType<ApprovalRequest>());
            Assert.Empty(host.Runs);
            Assert.Equal(ToolActivityState.Done, session.Items.OfType<ToolActivity>().Single().State);
            Assert.False(Assert.IsType<ToolResultsTurn>(session.Turns[2]).Results.Single().IsError);
        }

        [Fact]
        public async Task ACallThatPassesItsCheckStillAsks()
        {
            var host = new TestHost();
            var checks = 0;
            host.AddTool("send_to_ereader", alwaysAsks: true, check: _ => { checks++; return null; });
            var model = new ScriptedModel(ScriptedModel.Call("send_to_ereader"), ScriptedModel.Text("Sent."));
            var session = Session(host, model);

            var request = session.SendAsync("Send it");
            await AnswerWhenAsked(session, allow: true, always: false);
            await request;

            Assert.Equal(1, checks);
            Assert.Single(session.Items.OfType<ApprovalRequest>());
            Assert.Equal(new[] { "send_to_ereader" }, host.Runs);
        }

        [Theory]
        [InlineData("pg-1234", true)]
        [InlineData("OL-OL123W", true)]
        [InlineData("gb-abc", true)]
        [InlineData("op-1a2b3c4d", true)]
        [InlineData("e-1a2b3c4d", false)]
        [InlineData("r-1a2b3c4d", false)]
        [InlineData("", false)]
        public void SearchIdsAreToldApartFromListIds(string id, bool fromSearch)
        {
            Assert.Equal(fromSearch, SourceTools.IsSearchId(id));
        }

        [Fact]
        public async Task AlwaysAllowIsRememberedInTheSettings()
        {
            var host = new TestHost();
            host.AddTool("back_up_books", changes: true);
            var model = new ScriptedModel(ScriptedModel.Call("back_up_books"), ScriptedModel.Text("Done."),
                ScriptedModel.Call("back_up_books"), ScriptedModel.Text("Done again."));
            var session = Session(host, model);

            var first = session.SendAsync("Back up everything");
            await AnswerWhenAsked(session, allow: true, always: true);
            await first;
            await session.SendAsync("And again");

            Assert.Equal(2, host.Runs.Count);
            Assert.Single(session.Items.OfType<ApprovalRequest>());
            Assert.Equal(ToolPolicy.Allow, host.Settings.ToolPolicies["back_up_books"]);
            Assert.True(host.Saves > 0);
        }

        [Fact]
        public async Task WritingToTheEReaderAlwaysAsksWhateverTheSettingsSay()
        {
            var host = new TestHost();
            host.Settings.SkipApprovals = true;
            host.Settings.ToolPolicies["save_collections"] = ToolPolicy.Allow;
            var tool = host.AddTool("save_collections", alwaysAsks: true);
            var model = new ScriptedModel(ScriptedModel.Call("save_collections"), ScriptedModel.Text("Saved."));
            var session = Session(host, model);
            Assert.Equal(ToolPolicy.Ask, tool.Policy);

            var request = session.SendAsync("Save my collections");
            await AnswerWhenAsked(session, allow: true, always: false);
            await request;

            var card = session.Items.OfType<ApprovalRequest>().Single();
            Assert.False(card.CanAlwaysAllow);
            Assert.Equal(new[] { "save_collections" }, host.Runs);
        }

        [Fact]
        public async Task SkipApprovalsRunsOtherToolsWithoutACard()
        {
            var host = new TestHost();
            host.Settings.SkipApprovals = true;
            host.AddTool("eject_ereader", changes: true);
            var model = new ScriptedModel(ScriptedModel.Call("eject_ereader"), ScriptedModel.Text("Ejected."));
            var session = Session(host, model);

            await session.SendAsync("Eject it");

            Assert.Empty(session.Items.OfType<ApprovalRequest>());
            Assert.Equal(new[] { "eject_ereader" }, host.Runs);
        }

        [Fact]
        public async Task ToolsTurnedOffAreNotOfferedAndAreRefused()
        {
            var host = new TestHost();
            host.AddTool("list_books");
            host.AddTool("delete_backups", alwaysAsks: true);
            host.Settings.ToolPolicies["delete_backups"] = ToolPolicy.Off;
            var model = new ScriptedModel(ScriptedModel.Call("delete_backups"), ScriptedModel.Text("I can't."));
            var session = Session(host, model);

            await session.SendAsync("Delete my backups");

            Assert.Contains("list_books", model.ToolNames[0]);
            Assert.DoesNotContain("delete_backups", model.ToolNames[0]);
            Assert.Empty(host.Runs);
            Assert.True(Assert.IsType<ToolResultsTurn>(session.Turns[2]).Results.Single().IsError);
        }

        [Fact]
        public async Task WithoutAKeyNothingIsSent()
        {
            var host = new TestHost();
            host.Settings.Provider = ModelProvider.Anthropic;
            var model = new ScriptedModel(ScriptedModel.Text("Hello"));
            var session = Session(host, model);

            await session.SendAsync("Hello");

            Assert.Equal(0, model.Calls);
            Assert.True(Assert.IsType<NoticeMessage>(session.Items[1]).IsError);
            Assert.Empty(session.Turns);
        }

        [Fact]
        public async Task StopsAfterTheStepLimit()
        {
            var host = new TestHost();
            host.Settings.MaxSteps = 2;
            host.AddTool("list_books");
            var model = new ScriptedModel(ScriptedModel.Call("list_books"), ScriptedModel.Call("list_books"), ScriptedModel.Call("list_books"));
            var session = Session(host, model);

            await session.SendAsync("Keep going");

            Assert.Equal(2, model.Calls);
            Assert.IsType<NoticeMessage>(session.Items.Last());
        }

        [Fact]
        public async Task TheConversationIsKeptEncryptedAndComesBack()
        {
            var path = Path.Combine(TestFolder.Fresh("assistant-conversation"), "conversation.json");
            var host = new TestHost();
            host.AddTool("list_books");
            var model = new ScriptedModel(ScriptedModel.Call("list_books"), ScriptedModel.Text("You have *Middlemarch*."));
            await Session(host, model, path).SendAsync("What do I have?");

            var stored = File.ReadAllText(path);
            Assert.StartsWith("dpapi:", stored);
            Assert.DoesNotContain("Middlemarch", stored);

            var again = Session(host, new ScriptedModel(), path);
            Assert.Equal(3, again.Items.Count);
            Assert.Equal(4, again.Turns.Count);
            Assert.Equal("You have *Middlemarch*.", Assert.IsType<AssistantMessage>(again.Items[2]).Text);

            again.ClearConversation();
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void KeysAreEncryptedAndReadBack()
        {
            var settings = new AssistantSettings();
            settings.SetApiKey(ModelProvider.Anthropic, " sk-test-123 ");
            Assert.StartsWith("dpapi:", settings.ApiKeys["Anthropic"]);
            Assert.Equal("sk-test-123", settings.GetApiKey(ModelProvider.Anthropic));
            Assert.True(settings.HasKey);
            settings.SetApiKey(ModelProvider.Anthropic, "");
            Assert.False(settings.HasKey);
        }

        [Fact]
        public void ALocalServiceNeedsNoKey()
        {
            Assert.False(ChatProviders.NeedsKey(ModelProvider.OpenAICompatible, "http://localhost:1234/v1"));
            Assert.True(ChatProviders.NeedsKey(ModelProvider.OpenAICompatible, "https://api.mistral.ai/v1"));
            Assert.True(ChatProviders.NeedsKey(ModelProvider.Anthropic, ""));
        }

        [Fact]
        public void TheSettingsAreKeptByNameAndSurviveSigningOut()
        {
            var settings = new AppSettings { UserKey = "key", AccessToken = "token" };
            settings.Assistant.Provider = ModelProvider.OpenAI;
            settings.Assistant.ToolPolicies["eject_ereader"] = ToolPolicy.Off;
            var json = JsonSerializer.Serialize(settings);
            Assert.Contains("\"provider\":\"OpenAI\"", json);
            Assert.Contains("\"eject_ereader\":\"Off\"", json);
            Assert.DoesNotContain("HasKey", json);

            var back = JsonSerializer.Deserialize<AppSettings>(json)!;
            Assert.Equal(ModelProvider.OpenAI, back.Assistant.Provider);
            Assert.Equal(ModelProvider.OpenAI, back.SignedOut().Assistant.Provider);
        }

        [Fact]
        public void BookIdsAreShortStableAndHideThePath()
        {
            var path = @"C:\Users\someone\Documents\HoboLibris\Kobo\Middlemarch - George Eliot.epub";
            var id = BookIds.For(BookIds.Folder, path);
            Assert.Equal(id, BookIds.For(BookIds.Folder, path));
            Assert.Matches("^f-[0-9a-f]{8}$", id);

            var (found, unknown) = BookIds.Resolve(new[] { id.ToUpperInvariant(), "f-00000000" }, new[] { path }, p => BookIds.For(BookIds.Folder, p));
            Assert.Equal(new[] { path }, found);
            Assert.Equal(new[] { "f-00000000" }, unknown);
        }

        [Theory]
        [InlineData("**Saved** to [Kobo](https://www.kobo.com)", "Saved to Kobo (https://www.kobo.com)")]
        [InlineData("- *Middlemarch*\n- `Cranford`", "• Middlemarch\r\n• Cranford")]
        [InlineData("| Title | Author |\n|---|---|\n| Emma | Austen |", "Title\tAuthor\r\nEmma\tAusten")]
        public void CopiedAnswersLoseTheirMarkdown(string markdown, string plain)
        {
            Assert.Equal(plain, Views.ChatText.ToPlainText(markdown));
        }
    }
}
