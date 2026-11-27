using System.Text.Json.Nodes;
using HoboLibris.Assistant;
using HoboLibris.Assistant.Sources;

namespace HoboLibris.Tests
{
    /// <summary>
    /// Books that aren't free (#91): listed for the person to buy, never downloaded, and a card asks
    /// whether they are wanted when the model doesn't say.
    /// </summary>
    public class PaidBooksTests
    {
        private static SourceSettings Sources(string paidBooks = SourceSettings.PaidBooksAsk)
        {
            var sources = new SourceSettings { PaidBooks = paidBooks };
            sources.SetGoogleBooksKey("test-key");
            return sources;
        }

        [Fact]
        public void GoogleBooksListsBooksForSaleWithTheirPriceButNoDownload()
        {
            Assert.Contains("filter=free-ebooks", GoogleBooksSource.SearchUrl("emma", null, null, 5, "k").Query);
            Assert.Contains("filter=ebooks", GoogleBooksSource.SearchUrl("emma", null, null, 5, "k", paid: true).Query);

            var json = JsonNode.Parse("""
                {"items":[
                  {"id":"free","volumeInfo":{"title":"Emma"},"saleInfo":{"saleability":"FREE"},
                   "accessInfo":{"epub":{"isAvailable":true,"downloadLink":"http://books.google.com/books/download/Emma.epub?id=free"}}},
                  {"id":"sale","volumeInfo":{"title":"Wolf Hall"},"saleInfo":{"saleability":"FOR_SALE",
                   "listPrice":{"amount":12.99,"currencyCode":"GBP"},"retailPrice":{"amount":9.5,"currencyCode":"GBP"}},
                   "accessInfo":{"epub":{"isAvailable":true,"downloadLink":"http://books.google.com/books/download/sample.epub"}}},
                  {"id":"none","volumeInfo":{"title":"Bring Up the Bodies"},"saleInfo":{"saleability":"NOT_FOR_SALE"}}
                ]}
                """)!;
            var results = GoogleBooksSource.ParseSearch(json);

            Assert.True(results[0].Free);
            Assert.NotNull(results[0].DownloadUrl);
            Assert.False(results[1].Free);
            Assert.Equal("9.50 GBP", results[1].Price);
            Assert.Null(results[1].DownloadUrl);
            Assert.False(results[2].Free);
            Assert.Null(results[2].Price);
            Assert.Equal(false, (bool?)results[1].ToJson()["free"]);
            Assert.Equal("9.50 GBP", (string?)results[1].ToJson()["price"]);
            Assert.Null(results[0].ToJson()["free"]);
        }

        [Fact]
        public void OpenLibraryListsItsWholeCatalogueOnlyWhenPaidBooksAreWanted()
        {
            var free = Uri.UnescapeDataString(OpenLibrarySource.SearchUrl("wolf hall", null, null, 5).Query);
            var all = Uri.UnescapeDataString(OpenLibrarySource.SearchUrl("wolf hall", null, null, 5, paid: true).Query);
            Assert.Contains("ebook_access:public", free);
            Assert.DoesNotContain("ebook_access:public", all);

            var json = JsonNode.Parse("""
                {"docs":[
                  {"key":"/works/OL1W","title":"Middlemarch","ebook_access":"public","ia":["middlemarch1902"]},
                  {"key":"/works/OL2W","title":"Wolf Hall","ebook_access":"borrowable","ia":["wolfhall"]},
                  {"key":"/works/OL3W","title":"The Mirror and the Light","ebook_access":"no_ebook"}
                ]}
                """)!;
            Assert.Equal("ol-OL1W", Assert.Single(OpenLibrarySource.ParseSearch(json)).Id);

            var results = OpenLibrarySource.ParseSearch(json, paid: true);
            Assert.Equal(new[] { true, false, false }, results.Select(r => r.Free));
            Assert.Empty(results[1].ArchiveItems);
            Assert.Contains("borrowed", results[1].Note);
            Assert.Equal("No free copy.", results[2].Note);
        }

        [Fact]
        public void TheCardIsShownOnlyWhenTheModelDidntSayAndTheSettingIsToAsk()
        {
            var ask = Sources();
            var question = SourceTools.PaidQuestion(new JsonObject { ["source"] = "google_books", ["query"] = "wolf hall" }, ask);
            Assert.NotNull(question);
            Assert.Equal("paid", question!.Argument);
            Assert.Equal(new[] { false, true }, question.Options.Select(o => (bool)o.Value));

            // The model said; or the source can't list books for sale; or the setting answers it.
            Assert.Null(SourceTools.PaidQuestion(new JsonObject { ["source"] = "google_books", ["paid"] = true }, ask));
            Assert.Null(SourceTools.PaidQuestion(new JsonObject { ["source"] = "gutenberg" }, ask));
            Assert.Null(SourceTools.PaidQuestion(new JsonObject { ["source"] = "opds" }, ask));
            Assert.Null(SourceTools.PaidQuestion(new JsonObject { ["source"] = "open_library" }, Sources(SourceSettings.PaidBooksFree)));
            Assert.Null(SourceTools.PaidQuestion(new JsonObject { ["source"] = "open_library" }, Sources(SourceSettings.PaidBooksPaid)));
            var off = Sources();
            off.OpenLibrary = false;
            Assert.Null(SourceTools.PaidQuestion(new JsonObject { ["source"] = "open_library" }, off));
        }

        [Fact]
        public void ASearchFollowsTheModelOrTheCardElseTheSetting()
        {
            Assert.True(SourceTools.WantsPaid(new JsonObject { ["source"] = "open_library", ["paid"] = true }, Sources(SourceSettings.PaidBooksFree)));
            Assert.False(SourceTools.WantsPaid(new JsonObject { ["source"] = "open_library", ["paid"] = false }, Sources(SourceSettings.PaidBooksPaid)));
            Assert.True(SourceTools.WantsPaid(new JsonObject { ["source"] = "google_books" }, Sources(SourceSettings.PaidBooksPaid)));
            Assert.False(SourceTools.WantsPaid(new JsonObject { ["source"] = "google_books" }, Sources(SourceSettings.PaidBooksFree)));
            Assert.False(SourceTools.WantsPaid(new JsonObject { ["source"] = "gutenberg", ["paid"] = true }, Sources()));
        }

        private static EbookResult ForSale() => new()
        {
            Id = "gb-sale", Source = "google_books", Title = "Wolf Hall", Authors = "Hilary Mantel", Free = false, Price = "9.50 GBP",
            DownloadUrl = null, PageUrl = new Uri("https://books.google.com/books?id=sale"),
        };

        [Fact]
        public void BooksForSaleAreCountedAndTheModelIsToldTheyMustBeBought()
        {
            var free = new EbookResult { Id = "gb-free", Source = "google_books", Title = "Emma", DownloadUrl = new Uri("https://books.google.com/e.epub") };
            var outcome = SourceTools.Found(new[] { free, ForSale() }, 2, 10, "Google Books", paid: true);

            var json = JsonNode.Parse(outcome.Content)!;
            Assert.Equal(true, (bool?)json["books_for_sale_included"]);
            Assert.Contains("Only 1 of them are free to download", (string?)json["note"]);
            Assert.EndsWith(string.Format(HoboLibris.Resources.Strings.Asst_ResultsFreeCount, "1"), Assert.IsType<SearchResults>(outcome.Show).Heading);

            // A source that can't list books for sale says nothing about them.
            Assert.Null(JsonNode.Parse(SourceTools.Found(new[] { free }, 1, 10, "Project Gutenberg").Content)!["books_for_sale_included"]);
        }

        [Fact]
        public void ABookForSaleHasAStoreButtonInsteadOfDownloadAndIsKeptSo()
        {
            var list = new SearchResults("heading", new[] { ForSale() });
            Uri? opened = null;
            list.OpenPage = url => opened = url;
            var row = list.Rows.Single();

            Assert.False(row.Downloadable);
            Assert.True(row.CanFindInStore);
            Assert.Contains("9.50 GBP", row.PriceText);
            row.FindInStoreCommand.Execute(null);
            Assert.Equal("www.kobo.com", opened!.Host);
            Assert.Contains("Wolf Hall Hilary Mantel", Uri.UnescapeDataString(opened.Query));

            var restored = SearchResults.FromSaved("heading", list.ToSaved(), DateTime.Now).Rows.Single();
            Assert.False(restored.Book.Free);
            Assert.Equal("9.50 GBP", restored.Book.Price);
            Assert.False(restored.Downloadable);

            // A list saved before #91 holds only free books.
            var old = SearchResults.FromSaved("heading", """[{"id":"pg-145","source":"gutenberg","title":"Middlemarch"}]""", DateTime.Now).Rows.Single();
            Assert.True(old.Book.Free);
            Assert.True(old.Downloadable);
        }

        [Fact]
        public void ABookForSaleIsNeverDownloaded()
        {
            var refusal = SourceTools.NotFree(ForSale());
            Assert.True(refusal.IsError);
            Assert.Contains("open_store_search", refusal.Content);
        }

        // ---- The card in a conversation ---------------------------------------------------------

        private static (TestHost Host, List<JsonObject> Ran) HostWithQuestion()
        {
            var host = new TestHost();
            var ran = new List<JsonObject>();
            host.ToolList.Add(new AssistantTool
            {
                Name = "search_ebooks",
                Title = "Search",
                Description = "Search.",
                Schema = Schema.Object(),
                Ask = args => args["paid"] != null ? null : new ToolQuestion("Paid books too?", "paid", new (string, JsonNode)[]
                {
                    ("Free books only", JsonValue.Create(false)),
                    ("Paid books too", JsonValue.Create(true)),
                }),
                Run = (args, _) =>
                {
                    ran.Add(args);
                    return Task.FromResult(new ToolOutcome("found"));
                },
            });
            return (host, ran);
        }

        private static async Task<ChoiceRequest> WaitForCard(AssistantSession session)
        {
            for (int i = 0; i < 200 && !session.Items.OfType<ChoiceRequest>().Any(c => c.IsPending); i++)
            {
                await Task.Delay(10);
            }
            return session.Items.OfType<ChoiceRequest>().Single(c => c.IsPending);
        }

        private static AssistantSession Session(TestHost host, ScriptedModel model) => new(host, null) { ProviderFactory = _ => model };

        [Fact]
        public async Task TheAnswerOnTheCardIsWhatTheToolRunsWith()
        {
            var (host, ran) = HostWithQuestion();
            var model = new ScriptedModel(ScriptedModel.Call("search_ebooks", new JsonObject { ["query"] = "wolf hall" }), ScriptedModel.Text("Found."));
            var session = Session(host, model);

            var request = session.SendAsync("Find Wolf Hall");
            var card = await WaitForCard(session);
            Assert.Equal(new[] { "Free books only", "Paid books too" }, card.Options.Select(o => o.Label));
            card.Options[1].ChooseCommand.Execute(null);
            await request;

            Assert.Equal(true, (bool?)Assert.Single(ran)["paid"]);
            Assert.Equal("Paid books too", card.Outcome);
            // The call as the model made it stays in the conversation.
            var call = Assert.IsType<AssistantTurn>(session.Turns[1]).ToolCalls.Single();
            Assert.Null(call.Arguments["paid"]);
        }

        [Fact]
        public async Task NoCardWhenTheModelSaid()
        {
            var (host, ran) = HostWithQuestion();
            var model = new ScriptedModel(ScriptedModel.Call("search_ebooks", new JsonObject { ["paid"] = false }), ScriptedModel.Text("Found."));
            var session = Session(host, model);

            await session.SendAsync("Find a free copy of Middlemarch");

            Assert.Empty(session.Items.OfType<ChoiceRequest>());
            Assert.Equal(false, (bool?)Assert.Single(ran)["paid"]);
        }

        [Fact]
        public async Task DecliningTheCardRunsNothing()
        {
            var (host, ran) = HostWithQuestion();
            var model = new ScriptedModel(ScriptedModel.Call("search_ebooks"), ScriptedModel.Text("All right."));
            var session = Session(host, model);

            var request = session.SendAsync("Find Wolf Hall");
            (await WaitForCard(session)).DeclineCommand.Execute(null);
            await request;

            Assert.Empty(ran);
            Assert.Equal(ToolActivityState.Declined, session.Items.OfType<ToolActivity>().Single().State);
            Assert.True(Assert.IsType<ToolResultsTurn>(session.Turns[2]).Results.Single().IsError);
        }

        [Fact]
        public async Task StopWhileTheCardWaitsRunsNothing()
        {
            var (host, ran) = HostWithQuestion();
            var model = new ScriptedModel(ScriptedModel.Call("search_ebooks"), ScriptedModel.Text("All right."));
            var session = Session(host, model);

            var request = session.SendAsync("Find Wolf Hall");
            var card = await WaitForCard(session);
            session.Stop();
            await request;

            Assert.Empty(ran);
            Assert.False(card.IsPending);
            Assert.False(session.IsBusy);
        }

        [Fact]
        public async Task TheCardIsKeptWithTheConversation()
        {
            var path = Path.Combine(Path.GetTempPath(), "hobolibris-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var (host, _) = HostWithQuestion();
                var model = new ScriptedModel(ScriptedModel.Call("search_ebooks"), ScriptedModel.Text("Found."));
                var session = new AssistantSession(host, path) { ProviderFactory = _ => model };
                var request = session.SendAsync("Find Wolf Hall");
                (await WaitForCard(session)).Options[0].ChooseCommand.Execute(null);
                await request;

                var restored = new AssistantSession(host, path).Items.OfType<ChoiceRequest>().Single();
                Assert.False(restored.IsPending);
                Assert.Equal("Free books only", restored.Outcome);
                Assert.Equal("Paid books too?", restored.Question);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
