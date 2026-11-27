using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using HoboLibris.Assistant.Sources;
using HoboLibris.Resources;
using HoboLibris.Services;
using HoboLibris.ViewModels;

namespace HoboLibris.Assistant
{
    /// <summary>
    /// The Assistant's tools for e-book sources: search one, download a book into the Kobo folder,
    /// and open a book's page or a store's search page in the browser.  Only sources whose terms
    /// allow it are used; Standard Ebooks and the Kobo store are links only.
    /// </summary>
    public sealed class SourceTools
    {
        private readonly MainViewModel _main;
        private readonly GutenbergCatalogue _gutenberg;

        /// <summary>Books found in this session, by id, so a download knows where the file is.</summary>
        private readonly Dictionary<string, EbookResult> _found = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The id prefixes search_ebooks gives: Project Gutenberg, Open Library, Google Books, OPDS catalogues.</summary>
        private static readonly string[] SearchIdPrefixes = { "pg-", "ol-", "gb-", "op-" };

        /// <summary>The id came from search_ebooks, not from one of the book lists.</summary>
        public static bool IsSearchId(string id) =>
            SearchIdPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        public SourceTools(MainViewModel main)
        {
            _main = main;
            _gutenberg = new GutenbergCatalogue(Path.Combine(AppPaths.DataDir, "Assistant", "pg_catalog.csv.gz"));
            All = Build();
        }

        public IReadOnlyList<AssistantTool> All { get; }

        private SourceSettings Sources => _main.Settings.Assistant.Sources;

        /// <summary>The most Project Gutenberg matches in a search's list (its catalogue is on this computer).</summary>
        public const int MostListed = 200;

        /// <summary>How many books an online source is asked for, for the list: one page.</summary>
        public const int PageSize = 40;

        /// <summary>
        /// Fetches the Gutenberg catalogue in the background when it is needed soon: the Assistant is
        /// set up, Gutenberg is on, and the copy is missing or a week old.  Its mirrors can take
        /// minutes, which would otherwise hold up the first search.
        /// </summary>
        public async Task WarmUpAsync()
        {
            if (!Sources.Gutenberg || !_main.Settings.Assistant.HasKey || !_gutenberg.NeedsFetching)
            {
                return;
            }
            try
            {
                await _gutenberg.BooksAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or OperationCanceledException)
            {
                LogManager.Warn($"The Gutenberg catalogue couldn't be fetched in the background: {ex.Message}");
            }
        }

        /// <summary>The sources on now, for the system prompt.</summary>
        public JsonArray EnabledSources()
        {
            var list = new JsonArray();
            if (Sources.Gutenberg)
            {
                list.Add("gutenberg");
            }
            if (Sources.OpenLibrary)
            {
                list.Add("open_library");
            }
            if (Sources.GoogleBooksReady)
            {
                list.Add("google_books");
            }
            foreach (var c in Sources.Catalogues.Where(c => c.Enabled))
            {
                list.Add("opds: " + c.Name);
            }
            return list;
        }

        private List<AssistantTool> Build() => new()
        {
            new AssistantTool
            {
                Name = "search_ebooks",
                Title = Strings.Asst_Tool_SearchEbooks,
                Description = "Search one source of e-books: gutenberg (Project Gutenberg, public domain in the USA; its catalogue is on this computer), open_library (Open Library and the Internet Archive; free copies are scans), google_books (when the person added a key), opds (one of the person's own catalogues, named with catalogue). Only free books and the person's own can be downloaded; books for sale are listed for the person to buy. The state lists which are on. Returns ids for download_ebook and open_book_page.",
                Schema = Schema.Object(
                    ("source", Schema.String(null, "gutenberg", "open_library", "google_books", "opds"), true),
                    ("query", Schema.String("Words from the title or subject."), false),
                    ("author", Schema.String("The author's name, or part of it."), false),
                    ("language", Schema.String("A two-letter language code, such as en, fr or de."), false),
                    ("catalogue", Schema.String("For source opds: the catalogue's name."), false),
                    ("paid", Schema.Boolean("open_library and google_books: true to list books for sale too, false for free books only. Leave it out when the person didn't say; the program asks them."), false),
                    ("limit", Schema.Integer("The most books to give you, at most 40. The person sees every match in a list anyway."), false)),
                Ask = PaidQuestion,
                Run = (args, cancel) => SearchAsync(args, cancel),
            },
            new AssistantTool
            {
                Name = "download_ebook",
                Title = Strings.Asst_Tool_DownloadEbook,
                Description = "Download a book found with search_ebooks into the Kobo folder, as \"Title - Author.epub\". Only when the person asked to download, read or send a book; not when they asked to find or list books. It then appears on Send to eReader; send it with send_to_ereader (ids from list_send) when the person wants it on the eReader. An existing file isn't downloaded again.",
                Schema = Schema.Object(("id", Schema.String(null), true)),
                Changes = true,
                Check = DownloadCheck,
                Describe = args => _found.TryGetValue(args.Str("id") ?? "", out var r)
                    ? string.Format(Strings.Asst_Desc_Download, r.Title, r.Authors.Length > 0 ? r.Authors : "?", SourceName(r.Source))
                    : string.Format(Strings.Asst_Desc_DownloadId, args.Str("id")),
                Run = (args, cancel) => DownloadAsync(args.Str("id") ?? "", cancel),
            },
            new AssistantTool
            {
                Name = "open_book_page",
                Title = Strings.Asst_Tool_OpenBookPage,
                Description = "Open a book's page on its source's website in the person's browser, for a book found with search_ebooks: useful when it can't be downloaded here, or to read about it.",
                Schema = Schema.Object(("id", Schema.String(null), true)),
                Run = (args, _) =>
                {
                    if (!_found.TryGetValue(args.Str("id") ?? "", out var r) || r.PageUrl == null || r.Source == "opds")
                    {
                        return Task.FromResult(new ToolOutcome("No page is known for that id; search again.", true));
                    }
                    return Task.FromResult(Open(r.PageUrl));
                },
            },
            new AssistantTool
            {
                Name = "open_store_search",
                Title = Strings.Asst_Tool_OpenStoreSearch,
                Description = "Open a search on the Kobo store or Standard Ebooks in the person's browser. HoboLibris can't buy or download there: the person buys Kobo books themselves (they then appear on Back up books after a refresh), and downloads Standard Ebooks books themselves into the Kobo folder.",
                Schema = Schema.Object(
                    ("store", Schema.String(null, "kobo", "standard_ebooks"), true),
                    ("query", Schema.String(null), true)),
                Run = (args, _) =>
                {
                    var query = Uri.EscapeDataString(args.Str("query") ?? "");
                    var url = args.Str("store") switch
                    {
                        "kobo" => new Uri("https://www.kobo.com/search?query=" + query),
                        "standard_ebooks" => new Uri("https://standardebooks.org/ebooks?query=" + query),
                        _ => null,
                    };
                    return Task.FromResult(url == null ? new ToolOutcome("Unknown store; use kobo or standard_ebooks.", true) : Open(url));
                },
            },
        };

        private static string SourceName(string source) => source switch
        {
            "gutenberg" => "Project Gutenberg",
            "open_library" => "Open Library",
            "google_books" => "Google Books",
            _ => "OPDS",
        };

        private static ToolOutcome Open(Uri url)
        {
            if (url.Scheme != "https" && url.Scheme != "http")
            {
                return new ToolOutcome("Only web pages can be opened.", true);
            }
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return new ToolOutcome("Opened in the person's browser.", Summary: Strings.Asst_OpenedInBrowser);
        }

        // ---- Search -----------------------------------------------------------------------------

        /// <summary>The sources that can list books for sale (#91), when they are on.</summary>
        public static bool CanListPaid(string? source, SourceSettings sources) =>
            (source == "open_library" && sources.OpenLibrary) || (source == "google_books" && sources.GoogleBooksReady);

        private bool CanListPaid(string? source) => CanListPaid(source, Sources);

        /// <summary>
        /// The card asking whether books for sale are wanted (#91): only when the model left paid out, the
        /// source can list them, and the setting is to ask.
        /// </summary>
        public static ToolQuestion? PaidQuestion(JsonObject args, SourceSettings sources)
        {
            if (args["paid"] != null || !CanListPaid(args.Str("source"), sources) || sources.PaidBooks != SourceSettings.PaidBooksAsk)
            {
                return null;
            }
            return new ToolQuestion(Strings.Asst_PaidQuestion, "paid", new (string, JsonNode)[]
            {
                (Strings.Asst_FreeOnly, JsonValue.Create(false)),
                (Strings.Asst_PaidToo, JsonValue.Create(true)),
            });
        }

        private ToolQuestion? PaidQuestion(JsonObject args) => PaidQuestion(args, Sources);

        /// <summary>Whether this search lists books for sale: as the model or the card said, else as the setting says.</summary>
        public static bool WantsPaid(JsonObject args, SourceSettings sources) =>
            CanListPaid(args.Str("source"), sources)
            && (args["paid"] != null ? args.Bool("paid", false) : sources.PaidBooks == SourceSettings.PaidBooksPaid);

        private async Task<ToolOutcome> SearchAsync(JsonObject args, CancellationToken cancel)
        {
            var paid = WantsPaid(args, Sources);
            var query = args.Str("query");
            var author = args.Str("author");
            var language = args.Str("language");
            // A model on this computer gets fewer: the list shows them all to the person.
            var settings = _main.Settings.Assistant;
            var limit = Math.Clamp(args.Int("limit", ChatProviders.IsLocal(settings.Provider, settings.Endpoint) ? 5 : 10), 1, 40);
            if (query == null && author == null)
            {
                return new ToolOutcome("Give a query or an author.", true);
            }
            List<EbookResult> results;
            int? total = null;
            switch (args.Str("source"))
            {
                case "gutenberg":
                    if (!Sources.Gutenberg)
                    {
                        return Off("Project Gutenberg");
                    }
                    var books = await _gutenberg.BooksAsync(cancel);
                    var matches = GutenbergCatalogue.Search(books, query, author, language);
                    total = matches.Count;
                    results = matches.Take(MostListed).Select(GutenbergCatalogue.ToResult).ToList();
                    break;
                case "open_library":
                    if (!Sources.OpenLibrary)
                    {
                        return Off("Open Library");
                    }
                    var json = await SourceHttp.GetJsonAsync(OpenLibrarySource.SearchUrl(query, author, language, Math.Max(limit, PageSize), paid), cancel);
                    total = json["numFound"]?.GetValue<int>();
                    results = OpenLibrarySource.ParseSearch(json, paid);
                    break;
                case "google_books":
                    if (!Sources.GoogleBooksReady)
                    {
                        return new ToolOutcome("Google Books needs the person's API key in Settings, under E-book sources.", true);
                    }
                    var google = await SourceHttp.GetJsonAsync(GoogleBooksSource.SearchUrl(query, author, language, Math.Max(limit, PageSize), Sources.GetGoogleBooksKey(), paid), cancel);
                    total = google["totalItems"]?.GetValue<int>();
                    results = GoogleBooksSource.ParseSearch(google);
                    break;
                case "opds":
                    var catalogue = Catalogue(args.Str("catalogue"));
                    if (catalogue == null)
                    {
                        var names = Sources.Catalogues.Where(c => c.Enabled).Select(c => $"\"{c.Name}\"").ToList();
                        return new ToolOutcome(names.Count == 0
                            ? "The person has added no OPDS catalogues."
                            : "Name one of the catalogues: " + string.Join(", ", names) + ".", true);
                    }
                    results = await new OpdsSource(catalogue).SearchAsync(string.Join(" ", new[] { query, author }.Where(s => s != null)), Math.Max(limit, PageSize), cancel);
                    break;
                default:
                    return new ToolOutcome("Unknown source; use gutenberg, open_library, google_books or opds.", true);
            }
            foreach (var r in results)
            {
                _found[r.Id] = r;
            }
            return Found(results, total, limit, SourceName(args.Str("source") ?? ""), CanListPaid(args.Str("source")) ? paid : null);
        }

        /// <summary>
        /// What a search gives: the model gets the first few books with their ids and the counts; the
        /// person gets every book found in a list under the step, so the model needn't list them.
        /// </summary>
        /// <param name="paid">For a source that can list books for sale (#91): whether this search did; null for the others.</param>
        public static ToolOutcome Found(IReadOnlyList<EbookResult> results, int? total, int limit, string sourceName, bool? paid = null)
        {
            int found = Math.Max(total ?? 0, results.Count);
            var books = new JsonArray(results.Take(limit).Select(r => (JsonNode)r.ToJson()).ToArray());
            if (results.Count == 0)
            {
                var none = new JsonObject { ["total"] = 0 };
                if (paid != null)
                {
                    none["books_for_sale_included"] = paid;
                }
                none["books"] = books;
                return new ToolOutcome(none.ToJsonString(), Summary: string.Format(Strings.Asst_BooksFound, 0));
            }
            int free = results.Count(r => r.Free);
            // The note and the counts come first, so they stay when a small model's context cuts the result short.
            var output = new JsonObject { ["total"] = found, ["in_list"] = results.Count };
            if (paid != null)
            {
                output["books_for_sale_included"] = paid;
            }
            output["note"] = $"These {results.Count} books are already shown in a list in the pane, with buttons to download each one or open its page. "
                + "Don't list them again or draw another table: tell the person in a sentence or two what you found, without offering to download them."
                + (free < results.Count
                    ? $" Only {free} of them are free to download; those marked \"free\": false must be bought, and each has a button to find it in the Kobo store."
                    : "");
            output["books"] = books;
            string heading = found > results.Count
                ? string.Format(Strings.Asst_ResultsHeadingPart, results.Count.ToString("N0"), found.ToString("N0"), sourceName)
                : string.Format(Strings.Asst_ResultsHeading, results.Count.ToString("N0"), sourceName);
            if (free < results.Count)
            {
                heading += string.Format(Strings.Asst_ResultsFreeCount, free.ToString("N0"));
            }
            return new ToolOutcome(output.ToJsonString(), Summary: string.Format(Strings.Asst_BooksFound, found))
            {
                Show = new SearchResults(heading, results),
            };
        }

        private static ToolOutcome Off(string name) =>
            new($"{name} is switched off in Settings, under E-book sources.", true);

        private OpdsCatalogue? Catalogue(string? name) =>
            Sources.Catalogues.FirstOrDefault(c => c.Enabled && string.Equals(c.Name, name?.Trim(), StringComparison.CurrentCultureIgnoreCase))
            ?? (name == null && Sources.Catalogues.Count(c => c.Enabled) == 1 ? Sources.Catalogues.First(c => c.Enabled) : null);

        // ---- Download ---------------------------------------------------------------------------

        /// <summary>
        /// Why a download can't run, before the person is asked.  Project Gutenberg and Open Library ids
        /// can be looked up without a search; the others must come from a search in this conversation.
        /// </summary>
        private ToolOutcome? DownloadCheck(JsonObject args)
        {
            var id = args.Str("id") ?? "";
            if (_found.TryGetValue(id, out var book) && !book.Free)
            {
                return NotFree(book);
            }
            if (GutenbergCatalogue.TryParseId(id, out _))
            {
                return Sources.Gutenberg ? null : Off("Project Gutenberg");
            }
            if (id.StartsWith("ol-", StringComparison.OrdinalIgnoreCase))
            {
                return Sources.OpenLibrary ? null : Off("Open Library");
            }
            return _found.ContainsKey(id)
                ? null
                : new ToolOutcome("That id isn't from a search in this conversation; search again. Ids from list_books or list_send are not for download_ebook.", true);
        }

        /// <summary>
        /// The person pressed Download in a search's list.  A book found in this session is downloaded as
        /// found; one from a restored list by its id.
        /// </summary>
        public async Task<ToolOutcome> DownloadFoundAsync(EbookResult book)
        {
            if (!_found.ContainsKey(book.Id))
            {
                _found[book.Id] = book;
            }
            try
            {
                return await DownloadAsync(book.Id, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException
                                           or System.Text.Json.JsonException or OperationCanceledException)
            {
                return new ToolOutcome(ex.Message, true);
            }
        }

        /// <summary>A book that isn't free is never downloaded (#91): the model is told where the person can buy it.</summary>
        public static ToolOutcome NotFree(EbookResult book) =>
            new($"\"{book.Title}\" isn't free, so HoboLibris can't download it. The person can buy it: open the Kobo store's search for it "
                + "(open_store_search with store kobo and its title and author), or its page (open_book_page).", true);

        private async Task<ToolOutcome> DownloadAsync(string id, CancellationToken cancel)
        {
            var found = _found.TryGetValue(id, out var result) ? result : null;
            if (found is { Free: false })
            {
                return NotFree(found);
            }
            var folder = _main.KoboFolder;
            (string Path, bool AlreadyThere) saved;

            if (GutenbergCatalogue.TryParseId(id, out var number))
            {
                if (!Sources.Gutenberg)
                {
                    return Off("Project Gutenberg");
                }
                if (found == null)
                {
                    var book = (await _gutenberg.BooksAsync(cancel)).FirstOrDefault(b => b.Number == number);
                    if (book == null)
                    {
                        return new ToolOutcome("There is no such Project Gutenberg book.", true);
                    }
                    found = GutenbergCatalogue.ToResult(book);
                }
                saved = await FirstThatWorksAsync(GutenbergCatalogue.EpubUrls(number), SourceHttp.Client, found, cancel);
            }
            else if (id.StartsWith("ol-", StringComparison.OrdinalIgnoreCase))
            {
                if (!Sources.OpenLibrary)
                {
                    return Off("Open Library");
                }
                if (found == null)
                {
                    var json = await SourceHttp.GetJsonAsync(OpenLibrarySource.WorkUrl(id[3..]), cancel);
                    found = OpenLibrarySource.ParseSearch(json).FirstOrDefault();
                    if (found == null)
                    {
                        return new ToolOutcome("Open Library has no free copy of that book.", true);
                    }
                }
                var reasons = new List<string>();
                Uri? file = null;
                foreach (var item in found.ArchiveItems)
                {
                    var metadata = await SourceHttp.GetJsonAsync(OpenLibrarySource.MetadataUrl(item), cancel);
                    var (choice, reason) = OpenLibrarySource.ChooseFile(item, metadata);
                    if (choice != null)
                    {
                        file = choice;
                        break;
                    }
                    reasons.Add($"{item}: {reason}");
                }
                if (file == null)
                {
                    return new ToolOutcome("No Internet Archive copy of this book can be downloaded freely: " + string.Join("; ", reasons)
                        + ". It may still be readable on its Open Library page (open_book_page).", true);
                }
                saved = await EbookDownloader.DownloadAsync(file, SourceHttp.Client, folder, found.Title, found.Authors, "epub", cancel);
            }
            else if (found == null)
            {
                return new ToolOutcome("That id isn't from a search in this conversation; search again.", true);
            }
            else if (found.Source == "google_books")
            {
                if (found.DownloadUrl == null)
                {
                    return new ToolOutcome("Google Books offers no EPUB for this book. Its page can be opened with open_book_page.", true);
                }
                saved = await EbookDownloader.DownloadAsync(found.DownloadUrl, SourceHttp.Client, folder, found.Title, found.Authors, "epub", cancel);
            }
            else if (found.Source == "opds" && found.DownloadUrl != null)
            {
                var catalogue = Sources.Catalogues.FirstOrDefault(c => c.Id == found.CatalogueId && c.Enabled);
                if (catalogue == null)
                {
                    return new ToolOutcome("That catalogue has been removed or switched off.", true);
                }
                saved = await EbookDownloader.DownloadAsync(found.DownloadUrl, new OpdsSource(catalogue).Client, folder, found.Title, found.Authors, found.Format, cancel);
            }
            else
            {
                return new ToolOutcome("That book can't be downloaded.", true);
            }

            await _main.ScanSendFolderAsync();
            var row = _main.Send.Rows.FirstOrDefault(r => string.Equals(r.Path, saved.Path, StringComparison.OrdinalIgnoreCase));
            var outcome = new JsonObject
            {
                ["file"] = Path.GetFileName(saved.Path),
                ["already_in_folder"] = saved.AlreadyThere,
            };
            if (row != null)
            {
                outcome["send_id"] = BookIds.For(BookIds.Send, row.Id);
                outcome["send_status"] = row.Kind.ToString();
                outcome["why"] = row.Verdict.Why;
            }
            return new ToolOutcome(outcome.ToJsonString(),
                Summary: saved.AlreadyThere ? Strings.Asst_AlreadyInFolder : Strings.Asst_Downloaded)
            {
                Changed = saved.AlreadyThere ? null : string.Format(Strings.Asst_Chg_Downloaded, Path.GetFileName(saved.Path)),
            };
        }

        /// <summary>Tries each address in turn (the mirrors, the EPUB kinds) until one gives an EPUB.</summary>
        private async Task<(string Path, bool AlreadyThere)> FirstThatWorksAsync(IEnumerable<Uri> urls, HttpClient client, EbookResult book, CancellationToken cancel)
        {
            Exception? last = null;
            foreach (var url in urls)
            {
                try
                {
                    return await EbookDownloader.DownloadAsync(url, client, _main.KoboFolder, book.Title, book.Authors, "epub", cancel);
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
                {
                    last = ex;
                }
            }
            throw new InvalidOperationException(last?.Message ?? "The book couldn't be downloaded.");
        }
    }
}
