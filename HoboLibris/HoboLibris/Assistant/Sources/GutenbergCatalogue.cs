using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace HoboLibris.Assistant.Sources
{
    /// <summary>One book in Project Gutenberg's catalogue.</summary>
    public sealed record GutenbergBook(int Number, string Title, string Authors, string Language, string Subjects, string Issued);

    /// <summary>
    /// Project Gutenberg, used as its policy allows: its website is for people only and blocks
    /// automated access, but the machine-readable catalogue and the mirrors are there for programs.
    /// The catalogue (pg_catalog.csv.gz, about 6 MB) is fetched from a PGLAF mirror at most once a
    /// week and searched on this computer; EPUBs come from the mirror.  Nothing goes to
    /// www.gutenberg.org.
    /// </summary>
    public sealed class GutenbergCatalogue
    {
        public static readonly string[] Mirrors = { "https://gutenberg.pglaf.org", "https://aleph.pglaf.org" };
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        private readonly string _cacheFile;
        private readonly SemaphoreSlim _loading = new(1, 1);
        private List<GutenbergBook>? _books;

        public GutenbergCatalogue(string cacheFile) => _cacheFile = cacheFile;

        /// <summary>The book's page, for the person to open in a browser.</summary>
        public static Uri PageUrl(int number) => new($"https://www.gutenberg.org/ebooks/{number}");

        /// <summary>Where the mirror keeps the book's EPUB, with images, newest kind first.</summary>
        public static IEnumerable<Uri> EpubUrls(int number) =>
            Mirrors.SelectMany(m => new[]
            {
                new Uri($"{m}/cache/epub/{number}/pg{number}-images-3.epub"),
                new Uri($"{m}/cache/epub/{number}/pg{number}-images.epub"),
            });

        public static bool TryParseId(string id, out int number) =>
            int.TryParse(id.StartsWith("pg-", StringComparison.OrdinalIgnoreCase) ? id[3..] : "", NumberStyles.None, CultureInfo.InvariantCulture, out number);

        /// <summary>
        /// The catalogue, fetched first when there is no copy or it is a week old.  The mirrors can be
        /// slow (minutes for the 6 MB file), so the app fetches it in the background at start-up;
        /// only one fetch runs at a time.
        /// </summary>
        public async Task<IReadOnlyList<GutenbergBook>> BooksAsync(CancellationToken cancel)
        {
            if (_books != null)
            {
                return _books;
            }
            await _loading.WaitAsync(cancel);
            try
            {
                return _books ??= await LoadAsync(cancel);
            }
            finally
            {
                _loading.Release();
            }
        }

        /// <summary>Whether the copy on this computer is missing or a week old.</summary>
        public bool NeedsFetching => !File.Exists(_cacheFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFile) > MaxAge;

        private async Task<List<GutenbergBook>> LoadAsync(CancellationToken cancel)
        {
            var info = new FileInfo(_cacheFile);
            if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge)
            {
                try
                {
                    await DownloadCatalogueAsync(cancel);
                }
                catch (Exception ex) when (info.Exists && ex is not OperationCanceledException)
                {
                    // An older copy is better than none.
                    Services.LogManager.Warn($"The Gutenberg catalogue couldn't be refreshed: {ex.Message}");
                }
            }
            return await Task.Run(() =>
            {
                using var file = File.OpenRead(_cacheFile);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                return Parse(reader);
            }, cancel);
        }

        private async Task DownloadCatalogueAsync(CancellationToken cancel)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            Exception? last = null;
            foreach (var mirror in Mirrors)
            {
                var uri = new Uri($"{mirror}/cache/epub/feeds/pg_catalog.csv.gz");
                try
                {
                    using var turn = await SourceHttp.TurnAsync(uri, cancel);
                    using var response = await SourceHttp.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
                    SourceHttp.Check(response, uri);
                    var temp = _cacheFile + ".part";
                    await using (var output = File.Create(temp))
                    {
                        await response.Content.CopyToAsync(output, cancel);
                    }
                    File.Move(temp, _cacheFile, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or IOException)
                {
                    last = ex;
                }
            }
            throw new InvalidOperationException("The Project Gutenberg catalogue couldn't be fetched from its mirrors: " + last?.Message);
        }

        /// <summary>Reads pg_catalog.csv: text books only.</summary>
        public static List<GutenbergBook> Parse(TextReader reader)
        {
            var books = new List<GutenbergBook>();
            var header = true;
            foreach (var row in Csv(reader))
            {
                if (header)
                {
                    header = false;
                    continue;
                }
                // Text#,Type,Issued,Title,Language,Authors,Subjects,LoCC,Bookshelves
                if (row.Count < 7 || row[1] != "Text" || !int.TryParse(row[0], out var number))
                {
                    continue;
                }
                books.Add(new GutenbergBook(number, row[3].Replace('\n', ' ').Replace("\r", ""), row[5], row[4], row[6], row[2]));
            }
            return books;
        }

        /// <summary>Rows of a CSV file whose quoted fields may hold commas, quotes and line breaks.</summary>
        private static IEnumerable<List<string>> Csv(TextReader reader)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            int c;
            while ((c = reader.Read()) != -1)
            {
                var ch = (char)c;
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (reader.Peek() == '"')
                        {
                            field.Append('"');
                            reader.Read();
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        field.Append(ch);
                    }
                    continue;
                }
                switch (ch)
                {
                    case '"':
                        quoted = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(field.ToString());
                        field.Clear();
                        yield return row;
                        row = new List<string>();
                        break;
                    default:
                        field.Append(ch);
                        break;
                }
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                yield return row;
            }
        }

        /// <summary>
        /// Books whose title, author or subjects hold every word of the query, and whose authors
        /// hold every word of author.  Title matches come first.
        /// </summary>
        public static List<GutenbergBook> Search(IEnumerable<GutenbergBook> books, string? query, string? author, string? language)
        {
            language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
            var words = Words(query);
            var authorWords = Words(author);
            var results = new List<(GutenbergBook Book, int Rank)>();
            foreach (var b in books)
            {
                if (language != null && !b.Language.Split(';', StringSplitOptions.TrimEntries).Contains(language, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                var authors = Fold(b.Authors);
                if (authorWords.Any(w => !authors.Contains(w, StringComparison.Ordinal)))
                {
                    continue;
                }
                var title = Fold(b.Title);
                var all = title + " " + authors + " " + Fold(b.Subjects);
                if (words.Any(w => !all.Contains(w, StringComparison.Ordinal)))
                {
                    continue;
                }
                var rank = words.Count == 0 ? 1 : words.All(w => title.Contains(w, StringComparison.Ordinal)) ? 0 : 2;
                results.Add((b, rank));
            }
            return results.OrderBy(r => r.Rank).ThenBy(r => r.Book.Number).Select(r => r.Book).ToList();
        }

        private static List<string> Words(string? text) =>
            Fold(text ?? "").Split(new[] { ' ', ',', ';', ':', '.', '\'', '"', '-' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        /// <summary>Lower case without accents, so "Bronte" finds "Brontë".</summary>
        private static string Fold(string text)
        {
            var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }
            return builder.ToString();
        }

        /// <summary>"Eliot, George, 1819-1880; Smith, John" → "George Eliot, John Smith".</summary>
        public static string ReadableAuthors(string authors) => string.Join(", ", authors
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a =>
            {
                var parts = a.Split(',', StringSplitOptions.TrimEntries).Where(p => !p.Any(char.IsDigit) && !p.StartsWith('[')).ToList();
                return parts.Count >= 2 ? $"{parts[1]} {parts[0]}" : parts.FirstOrDefault() ?? a;
            }));

        public static EbookResult ToResult(GutenbergBook b) => new()
        {
            Id = "pg-" + b.Number.ToString(CultureInfo.InvariantCulture),
            Source = "gutenberg",
            Title = b.Title,
            Authors = ReadableAuthors(b.Authors),
            Year = b.Issued.Length >= 4 ? b.Issued[..4] + " (added to Gutenberg)" : null,
            Language = b.Language,
            PageUrl = PageUrl(b.Number),
        };
    }
}
