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

        public SourceTools(MainViewModel main)
        {
            _main = main;
            _gutenberg = new GutenbergCatalogue(Path.Combine(AppPaths.DataDir, "Assistant", "pg_catalog.csv.gz"));
            All = Build();
        }

        public IReadOnlyList<AssistantTool> All { get; }

        private SourceSettings Sources => _main.Settings.Assistant.Sources;

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
                Description = "Search one source of free or the person's own e-books. Sources: gutenberg (Project Gutenberg, public domain in the USA; searched in its catalogue on this computer), open_library (Open Library and the Internet Archive, books free to read only, made from scans), google_books (free Google Books, when the person added a key), opds (one of the person's own catalogues, named with catalogue). The state lists which are on. Returns ids for download_ebook and open_book_page.",
                Schema = Schema.Object(
                    ("source", Schema.String("Where to search.", "gutenberg", "open_library", "google_books", "opds"), true),
                    ("query", Schema.String("Words from the title or subject."), false),
                    ("author", Schema.String("The author's name, or part of it."), false),
                    ("language", Schema.String("A two-letter language code, such as en, fr or de."), false),
                    ("catalogue", Schema.String("For source opds: the catalogue's name."), false),
                    ("limit", Schema.Integer("The most books to return (default 10, at most 40)."), false)),
                Run = (args, cancel) => SearchAsync(args, cancel),
            },
            new AssistantTool
            {
                Name = "download_ebook",
                Title = Strings.Asst_Tool_DownloadEbook,
                Description = "Download a book found with search_ebooks into the Kobo folder, as \"Title - Author.epub\". It then appears on Send to eReader; send it with send_to_ereader (ids from list_send) when the person wants it on the eReader. An existing file isn't downloaded again.",
                Schema = Schema.Object(("id", Schema.String("The book's id from search_ebooks."), true)),
                Changes = true,
                Describe = args => _found.TryGetValue(args.Str("id") ?? "", out var r)
                    ? string.Format(Strings.Asst_Desc_Download, r.Title, r.Authors.Length > 0 ? r.Authors : "?", SourceName(r.Source))
                    : string.Format(Strings.Asst_Desc_DownloadId, args.Str("id")),
                Run = (args, cancel) => DownloadAsync(args, cancel),
            },
            new AssistantTool
            {
                Name = "open_book_page",
                Title = Strings.Asst_Tool_OpenBookPage,
                Description = "Open a book's page on its source's website in the person's browser, for a book found with search_ebooks: useful when it can't be downloaded here, or to read about it.",
                Schema = Schema.Object(("id", Schema.String("The book's id from search_ebooks."), true)),
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
                    ("store", Schema.String("Which site.", "kobo", "standard_ebooks"), true),
                    ("query", Schema.String("What to search for."), true)),
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

        private async Task<ToolOutcome> SearchAsync(JsonObject args, CancellationToken cancel)
        {
            var query = args.Str("query");
            var author = args.Str("author");
            var language = args.Str("language");
            var limit = Math.Clamp(args.Int("limit", 10), 1, 40);
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
                    results = matches.Take(limit).Select(GutenbergCatalogue.ToResult).ToList();
                    break;
                case "open_library":
                    if (!Sources.OpenLibrary)
                    {
                        return Off("Open Library");
                    }
                    var json = await SourceHttp.GetJsonAsync(OpenLibrarySource.SearchUrl(query, author, language, limit), cancel);
                    total = json["numFound"]?.GetValue<int>();
                    results = OpenLibrarySource.ParseSearch(json);
                    break;
                case "google_books":
                    if (!Sources.GoogleBooksReady)
                    {
                        return new ToolOutcome("Google Books needs the person's API key in Settings, under E-book sources.", true);
                    }
                    var google = await SourceHttp.GetJsonAsync(GoogleBooksSource.SearchUrl(query, author, language, limit, Sources.GetGoogleBooksKey()), cancel);
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
                    results = await new OpdsSource(catalogue).SearchAsync(string.Join(" ", new[] { query, author }.Where(s => s != null)), limit, cancel);
                    break;
                default:
                    return new ToolOutcome("Unknown source; use gutenberg, open_library, google_books or opds.", true);
            }
            foreach (var r in results)
            {
                _found[r.Id] = r;
            }
            var output = new JsonObject { ["books"] = new JsonArray(results.Select(r => (JsonNode)r.ToJson()).ToArray()) };
            if (total != null)
            {
                output["total"] = total;
            }
            return new ToolOutcome(output.ToJsonString(), Summary: string.Format(Strings.Asst_BooksFound, total ?? results.Count));
        }

        private static ToolOutcome Off(string name) =>
            new($"{name} is switched off in Settings, under E-book sources.", true);

        private OpdsCatalogue? Catalogue(string? name) =>
            Sources.Catalogues.FirstOrDefault(c => c.Enabled && string.Equals(c.Name, name?.Trim(), StringComparison.CurrentCultureIgnoreCase))
            ?? (name == null && Sources.Catalogues.Count(c => c.Enabled) == 1 ? Sources.Catalogues.First(c => c.Enabled) : null);

        // ---- Download ---------------------------------------------------------------------------

        private async Task<ToolOutcome> DownloadAsync(JsonObject args, CancellationToken cancel)
        {
            var id = args.Str("id") ?? "";
            var found = _found.TryGetValue(id, out var result) ? result : null;
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
                Summary: saved.AlreadyThere ? Strings.Asst_AlreadyInFolder : Strings.Asst_Downloaded);
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
