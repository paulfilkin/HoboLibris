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
        public string Screen { get; set; } = "back_up";
        public JsonObject Context() => new() { ["screen"] = Screen };
        public string ContextLine() => "Kobo";

        /// <summary>A tool that counts its runs and answers "ran", or the outcome given.</summary>
        public AssistantTool AddTool(string name, bool changes = false, bool alwaysAsks = false, Func<JsonObject, ToolOutcome?>? check = null,
            ToolOutcome? outcome = null)
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
                    return Task.FromResult(outcome ?? new ToolOutcome("ran"));
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
        public void AParameterWithNoDescriptionSendsNone()
        {
            var schema = Schema.Object(
                ("ids", Schema.Array(Schema.String(null), null), true),
                ("screen", Schema.String(null, "back_up", "kindle"), true),
                ("limit", Schema.Integer("Default 200."), false));

            var props = schema["properties"]!.AsObject();
            Assert.Equal("""{"type":"array","items":{"type":"string"}}""", props["ids"]!.ToJsonString());
            Assert.Equal("""{"type":"string","enum":["back_up","kindle"]}""", props["screen"]!.ToJsonString());
            Assert.Equal("Default 200.", (string?)props["limit"]!["description"]);
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

        private static ChatReply Empty() => new("", Array.Empty<ToolCall>(), "stop", 0, 0);

        // #74: Ollama drops a call to a tool that wasn't in the request, and the reply comes back empty.
        [Fact]
        public async Task AnEmptyReplyFromALocalModelIsAskedAgainWithANote()
        {
            var host = new TestHost();
            host.AddTool("list_books");
            host.AddTool("list_collections");
            var model = new ScriptedModel(Empty(), ScriptedModel.Text("You have two collections."));
            var session = Session(host, model);

            await session.SendAsync("Which collections do I have?");

            Assert.Equal(2, model.Calls);
            var note = Assert.IsType<UserTurn>(model.Sent[1][^1]);
            Assert.StartsWith("(A note from HoboLibris", note.Text);
            Assert.Contains("use_tools", note.Text);
            Assert.Contains("collections: list_collections", note.Text);
            Assert.DoesNotContain("list_books", note.Text);
            Assert.DoesNotContain(session.Turns, t => t is UserTurn u && u.Text == note.Text);
            Assert.Equal("You have two collections.", session.Items.OfType<AssistantMessage>().Single().Text);
            Assert.Empty(session.Items.OfType<NoticeMessage>());
        }

        [Fact]
        public async Task TwoEmptyRepliesFromALocalModelSaySoInThePane()
        {
            var host = new TestHost();
            host.AddTool("list_books");
            var model = new ScriptedModel(Empty(), Empty(), ScriptedModel.Text("Not asked for."));
            var session = Session(host, model);

            await session.SendAsync("What books do I have?");

            Assert.Equal(2, model.Calls);
            Assert.Equal(HoboLibris.Resources.Strings.Asst_NoAnswer, session.Items.OfType<NoticeMessage>().Single().Text);
            Assert.Empty(session.Items.OfType<AssistantMessage>());
        }

        [Fact]
        public async Task AnEmptyReplyFromAHostedModelSaysSoInThePane()
        {
            var host = new TestHost();
            host.Settings.Provider = ModelProvider.Anthropic;
            host.Settings.Endpoint = "";
            host.Settings.SetApiKey(ModelProvider.Anthropic, "sk-test-123");
            host.AddTool("list_books");
            var model = new ScriptedModel(Empty(), ScriptedModel.Text("Not asked for."));
            var session = Session(host, model);

            await session.SendAsync("What books do I have?");

            Assert.Equal(1, model.Calls);
            Assert.Equal(HoboLibris.Resources.Strings.Asst_NoAnswer, session.Items.OfType<NoticeMessage>().Single().Text);
        }

        // #75: what a request changed, from the tools' own record.
        [Fact]
        public async Task WhatARequestChangedIsListedAtTheEnd()
        {
            var host = new TestHost();
            host.Settings.SkipApprovals = true;
            host.AddTool("back_up_books", changes: true, outcome: new ToolOutcome("ran") { Changed = "Kobo books backed up: 2" });
            host.AddTool("list_books");
            var model = new ScriptedModel(ScriptedModel.Call("back_up_books"), ScriptedModel.Call("list_books"), ScriptedModel.Text("Backed up two books."));
            var session = Session(host, model);

            await session.SendAsync("Back up the rest");

            var notice = Assert.IsType<NoticeMessage>(session.Items.Last());
            Assert.Equal(HoboLibris.Resources.Strings.Asst_ChangesMade + "\n• Kobo books backed up: 2", notice.Text);
            Assert.Single(session.Items.OfType<NoticeMessage>());
        }

        [Fact]
        public async Task CollectionChangesAreListedOnceWithTheLastCount()
        {
            var host = new TestHost();
            host.Settings.SkipApprovals = true;
            host.AddTool("create_collection", outcome: new ToolOutcome("ran") { Changed = "Collection changes waiting to be saved: 1", ChangeKey = "collections" });
            host.AddTool("eject_ereader", changes: true, outcome: new ToolOutcome("ran") { Changed = "Kobo eReader ejected" });
            host.AddTool("add_to_collection", outcome: new ToolOutcome("ran") { Changed = "Collection changes waiting to be saved: 3", ChangeKey = "collections" });
            var model = new ScriptedModel(ScriptedModel.Call("create_collection"), ScriptedModel.Call("eject_ereader"),
                ScriptedModel.Call("add_to_collection"), ScriptedModel.Text("Done."));
            var session = Session(host, model);

            await session.SendAsync("Make a Poetry collection, then eject");

            var lines = session.Items.OfType<NoticeMessage>().Single().Text.Split('\n');
            Assert.Equal(new[] { HoboLibris.Resources.Strings.Asst_ChangesMade, "• Kobo eReader ejected", "• Collection changes waiting to be saved: 3" }, lines);
        }

        [Fact]
        public async Task FailedAndDeclinedChangesAreNotListed()
        {
            var host = new TestHost();
            host.Settings.SkipApprovals = true;
            host.AddTool("back_up_books", changes: true, outcome: new ToolOutcome("No eReader.", true) { Changed = "Kobo books backed up: 2" });
            host.AddTool("send_to_ereader", alwaysAsks: true, outcome: new ToolOutcome("ran") { Changed = "Books sent to the eReader: 1" });
            var model = new ScriptedModel(ScriptedModel.Call("back_up_books"), ScriptedModel.Call("send_to_ereader"),
                ScriptedModel.Text("Nothing could be done."));
            var session = Session(host, model);

            var request = session.SendAsync("Back up and send");
            await AnswerWhenAsked(session, allow: false, always: false);
            await request;

            Assert.Empty(session.Items.OfType<NoticeMessage>());
        }

        // From Mycelia's test on 15 November 2026: the model said it had updated a research log and added a note, and had
        // called no tool that changes anything.
        [Fact]
        public async Task AClaimedChangeThatWasntMadeIsFlagged()
        {
            var host = new TestHost();
            host.AddTool("list_collections");
            var model = new ScriptedModel(ScriptedModel.Call("list_collections"), ScriptedModel.Text("I've added the three books to Poetry."));
            var session = Session(host, model);

            await session.SendAsync("Put them in Poetry");

            Assert.Equal(HoboLibris.Resources.Strings.Asst_NoChangesMade, Assert.IsType<NoticeMessage>(session.Items.Last()).Text);
        }

        [Fact]
        public async Task AnOfferIsNotFlagged()
        {
            var host = new TestHost();
            var model = new ScriptedModel(ScriptedModel.Text("I can add the three books to Poetry."));
            var session = Session(host, model);

            await session.SendAsync("Could they go in Poetry?");

            Assert.Empty(session.Items.OfType<NoticeMessage>());
        }

        [Theory]
        [InlineData("I've added the three books to Poetry.", true)]
        [InlineData("I backed up 12 books.", true)]
        [InlineData("I have now sent Middlemarch to your eReader.", true)]
        [InlineData("Done.\n- Added 3 books to Poetry\n- Renamed Novels to Fiction", true)]
        [InlineData("**Backed up** 4 books.", true)]
        [InlineData("Your books have been backed up.", true)]
        [InlineData("I looked at your books and moved the novels to Fiction.", true)]
        [InlineData("Your books are backed up in the Kobo folder.", false)]
        [InlineData("I can add them to Poetry.", false)]
        [InlineData("I'll back them up when you say so.", false)]
        [InlineData("Would you like me to send it?", false)]
        [InlineData("Three books have no backup copy yet.", false)]
        [InlineData("Ich habe die Bücher gesichert.", false)]
        [InlineData("", false)]
        public void ClaimedChangesAreFoundAndOffersAreNot(string reply, bool claims)
        {
            Assert.Equal(claims, AssistantSession.ClaimsChanges(reply));
        }

        // #77: the state goes on the person's message, so a hosted model's system prompt stays the same and is cached.
        private static TestHost Hosted()
        {
            var host = new TestHost();
            host.Settings.Provider = ModelProvider.Anthropic;
            host.Settings.Endpoint = "";
            host.Settings.SetApiKey(ModelProvider.Anthropic, "sk-test-123");
            return host;
        }

        [Fact]
        public void TheStateComesAfterThePersonsText()
        {
            Assert.Equal("Hello", new UserTurn("Hello").ForModel);
            var turn = new UserTurn("Hello", "{\"screen\":\"kindle\"}");
            Assert.StartsWith("Hello\n\n[From HoboLibris, not the person: the state when this message was sent (JSON): {\"screen\":\"kindle\"}]", turn.ForModel);
        }

        [Fact]
        public async Task AHostedModelGetsTheStateOnEachMessageAndTheSamePrompt()
        {
            var host = Hosted();
            host.AddTool("list_books");
            var model = new ScriptedModel(ScriptedModel.Call("list_books"), ScriptedModel.Text("Two books."), ScriptedModel.Text("Nothing on Kindle."));
            var session = Session(host, model);

            await session.SendAsync("What books do I have?");
            host.Screen = "kindle";
            await session.SendAsync("And on my Kindle?");

            Assert.Equal(3, model.Calls);
            Assert.All(model.Systems, s => Assert.Equal(model.Systems[0], s));
            Assert.DoesNotContain("back_up", model.Systems[0]);
            var users = model.Sent[2].OfType<UserTurn>().ToList();
            Assert.Contains("\"back_up\"", users[0].State);
            Assert.Contains("\"kindle\"", users[1].State);
            // The first message is sent with the same state on every later step.
            Assert.Equal(model.Sent[0].OfType<UserTurn>().Single(), users[0]);
            Assert.Contains("get_state", model.ToolNames[0]);
        }

        [Fact]
        public async Task GetStateAnswersWithTheStateNow()
        {
            var host = Hosted();
            var model = new ScriptedModel(ScriptedModel.Call("get_state"), ScriptedModel.Text("You're on the Kindle tab."));
            var session = Session(host, model);
            host.Screen = "kindle";

            await session.SendAsync("Where am I?");

            var result = Assert.IsType<ToolResultsTurn>(session.Turns[2]).Results.Single();
            Assert.False(result.IsError);
            Assert.Contains("\"kindle\"", result.Content);
        }

        [Fact]
        public async Task AModelOnThisComputerHasTheStateInItsPromptNotOnTheMessage()
        {
            var host = new TestHost();
            host.AddTool("list_books");
            var model = new ScriptedModel(ScriptedModel.Text("Hello."));
            var session = Session(host, model);

            await session.SendAsync("Hello");

            Assert.Contains("\"back_up\"", model.Systems[0]);
            Assert.Null(model.Sent[0].OfType<UserTurn>().Single().State);
            Assert.DoesNotContain("get_state", model.ToolNames[0]);
            // The conversation keeps it, for a hosted model chosen later.
            Assert.NotNull(session.Turns.OfType<UserTurn>().Single().State);
        }

        [Fact]
        public void AnthropicCachesThePromptForAnHourAndTheConversationSoFar()
        {
            var options = new ChatProviderOptions(ModelProvider.Anthropic, "claude-sonnet-5-5", "sk-test-123", "", 1000);
            var turns = new ChatTurn[]
            {
                new UserTurn("What books do I have?", "{\"screen\":\"back_up\"}"),
                new AssistantTurn("", new[] { new ToolCall("c1", "list_books", new JsonObject()) }),
                new ToolResultsTurn(new[] { new ToolResult("c1", "list_books", "[]", false) }),
            };
            var body = AnthropicChatProvider.Body(options, "The prompt.", turns, new[] { new ToolSpec("list_books", "List.", Schema.Object()) });

            var system = Assert.IsType<JsonArray>(body["system"]).Single()!;
            Assert.Equal("The prompt.", system["text"]!.GetValue<string>());
            Assert.Equal("1h", system["cache_control"]!["ttl"]!.GetValue<string>());
            var messages = Assert.IsType<JsonArray>(body["messages"]);
            Assert.Contains("[From HoboLibris, not the person", messages[0]!["content"]![0]!["text"]!.GetValue<string>());
            var lastBlock = messages[^1]!["content"]!.AsArray()[^1]!;
            Assert.Equal("tool_result", lastBlock["type"]!.GetValue<string>());
            Assert.Equal("ephemeral", lastBlock["cache_control"]!["type"]!.GetValue<string>());
            Assert.Null(lastBlock["cache_control"]!["ttl"]);
            // Only those two marks.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(body.ToJsonString(), "cache_control").Count);
        }

        [Fact]
        public void TheStateIsKeptWithASavedConversation()
        {
            var turn = new UserTurn("Hello", "{\"screen\":\"send\"}");
            var saved = ConversationStore.ToSaved(turn);
            var json = JsonSerializer.Serialize(saved);
            Assert.Equal(turn, ConversationStore.FromSaved(JsonSerializer.Deserialize<SavedTurn>(json)!));
            // A conversation saved before #77 has no state and still loads.
            Assert.Equal(new UserTurn("Hello"), ConversationStore.FromSaved(new SavedTurn { Kind = "user", Text = "Hello" }));
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
