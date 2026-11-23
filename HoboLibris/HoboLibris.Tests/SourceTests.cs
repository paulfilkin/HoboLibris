using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using HoboLibris.Assistant;
using HoboLibris.Assistant.Sources;

namespace HoboLibris.Tests
{
    /// <summary>A web server stand-in: answers every request with the same body.</summary>
    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _type;

        public StubHandler(byte[] body, string type)
        {
            _body = body;
            _type = type;
        }

        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var content = new ByteArrayContent(_body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_type);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    public class SourceTests
    {
        private const string Catalogue =
            "Text#,Type,Issued,Title,Language,Authors,Subjects,LoCC,Bookshelves\n" +
            "145,Text,1994-07-01,Middlemarch,en,\"Eliot, George, 1819-1880\",\"England -- Fiction; Married people -- Fiction\",PR,\"Best Books Ever Listings\"\n" +
            "1260,Text,1998-03-01,\"Jane Eyre: An Autobiography\",en,\"Brontë, Charlotte, 1816-1855\",Governesses -- Fiction,PR,\n" +
            "768,Text,1996-12-01,\"Wuthering Heights\",en,\"Brontë, Emily, 1818-1848\",\"Love stories; Yorkshire (England) -- Fiction\",PR,\n" +
            "9999,Sound,2003-01-01,\"An audio book\",en,\"Someone\",,,\n" +
            "17489,Text,2005-12-01,\"Les misérables, Tome I\nFantine\",fr,\"Hugo, Victor, 1802-1885\",,PQ,\n";

        private static List<GutenbergBook> Books() => GutenbergCatalogue.Parse(new StringReader(Catalogue));

        [Fact]
        public void ReadsTheGutenbergCatalogueWithQuotedLineBreaks()
        {
            var books = Books();
            Assert.Equal(new[] { 145, 1260, 768, 17489 }, books.Select(b => b.Number));
            Assert.Equal("Les misérables, Tome I Fantine", books.Last().Title);
        }

        [Fact]
        public void SearchesTheCatalogueWithoutAccentsAndPutsTitleMatchesFirst()
        {
            var books = Books();
            Assert.Equal(new[] { 768, 1260 }, GutenbergCatalogue.Search(books, null, "bronte", null).Select(b => b.Number));
            Assert.Equal(new[] { 768 }, GutenbergCatalogue.Search(books, "yorkshire", null, null).Select(b => b.Number));
            Assert.Equal(new[] { 17489 }, GutenbergCatalogue.Search(books, "miserables", null, "fr").Select(b => b.Number));
            Assert.Empty(GutenbergCatalogue.Search(books, "miserables", null, "en"));
            Assert.Equal("Charlotte Brontë", GutenbergCatalogue.ReadableAuthors("Brontë, Charlotte, 1816-1855"));
        }

        [Fact]
        public void GutenbergBooksComeFromTheMirrorsNotTheWebsite()
        {
            Assert.True(GutenbergCatalogue.TryParseId("pg-145", out var number));
            Assert.Equal(145, number);
            Assert.False(GutenbergCatalogue.TryParseId("ol-145", out _));
            Assert.All(GutenbergCatalogue.EpubUrls(145), u => Assert.EndsWith("pglaf.org", u.Host));
            Assert.Equal("www.gutenberg.org", GutenbergCatalogue.PageUrl(145).Host);
        }

        [Fact]
        public void OpenLibraryIsAskedForPublicBooksOnly()
        {
            var url = Uri.UnescapeDataString(OpenLibrarySource.SearchUrl("middlemarch", "eliot", "en", 5).Query);
            Assert.Contains("ebook_access:public", url);
            Assert.Contains("language:eng", url);

            var json = JsonNode.Parse("""
                {"docs":[
                  {"key":"/works/OL1W","title":"Middlemarch","author_name":["George Eliot"],"ebook_access":"public","ia":["middlemarch1902"]},
                  {"key":"/works/OL2W","title":"Middlemarch","ebook_access":"borrowable","ia":["lent"]},
                  {"key":"/works/OL3W","title":"Middlemarch","ebook_access":"public"}
                ]}
                """)!;
            var results = OpenLibrarySource.ParseSearch(json);
            Assert.Equal("ol-OL1W", Assert.Single(results).Id);
            Assert.Equal(new[] { "middlemarch1902" }, results[0].ArchiveItems);
        }

        [Theory]
        [InlineData("""{"metadata":{"collection":["americana"]},"files":[{"name":"book.epub","format":"EPUB"}]}""", true, null)]
        [InlineData("""{"metadata":{"collection":"americana","access-restricted-item":"true"},"files":[{"name":"book.epub"}]}""", false, "restricted")]
        [InlineData("""{"metadata":{"collection":["inlibrary","trent_university"]},"files":[{"name":"book.epub"}]}""", false, "lending")]
        [InlineData("""{"metadata":{"collection":["americana"]},"files":[{"name":"book.epub","private":"true"}]}""", false, "no public EPUB")]
        [InlineData("""{"metadata":{"collection":["americana"]},"files":[{"name":"book.pdf"}]}""", false, "no public EPUB")]
        [InlineData("""{"metadata":{"collection":["americana"],"date":"1957"},"files":[{"name":"book.epub"}]}""", false, "may still be in copyright")]
        [InlineData("""{"metadata":{"collection":["opensource"],"date":"1957","licenseurl":"https://creativecommons.org/licenses/by/4.0/"},"files":[{"name":"book.epub"}]}""", true, null)]
        [InlineData("""{"metadata":{"collection":["americana"],"date":"1911"},"files":[{"name":"book.epub"}]}""", true, null)]
        public void InternetArchiveItemsAreCheckedBeforeDownload(string metadata, bool allowed, string? reason)
        {
            var (file, why) = OpenLibrarySource.ChooseFile("item1", JsonNode.Parse(metadata)!);
            Assert.Equal(allowed, file != null);
            if (allowed)
            {
                Assert.Equal("https://archive.org/download/item1/book.epub", file!.AbsoluteUri);
            }
            else
            {
                Assert.Contains(reason!, why);
            }
        }

        [Fact]
        public void GoogleBooksLinksAreFetchedSecurelyAndBooksWithoutOneSaySo()
        {
            var json = JsonNode.Parse("""
                {"items":[
                  {"id":"abc","volumeInfo":{"title":"Emma","authors":["Jane Austen"],"publishedDate":"1816"},"accessInfo":{"epub":{"isAvailable":true,"downloadLink":"http://books.google.com/books/download/Emma.epub?id=abc&output=epub"}}},
                  {"id":"def","volumeInfo":{"title":"Persuasion"},"accessInfo":{"epub":{"isAvailable":false}}}
                ]}
                """)!;
            var results = GoogleBooksSource.ParseSearch(json);
            Assert.Equal("https", results[0].DownloadUrl!.Scheme);
            Assert.Equal("gb-abc", results[0].Id);
            Assert.Null(results[1].DownloadUrl);
            Assert.NotNull(results[1].Note);
            Assert.Contains("key=k%201", GoogleBooksSource.SearchUrl("emma", null, null, 5, "k 1").Query);
        }

        [Fact]
        public void OpdsEntriesOfferOnlyFreeEpubsAndPreferKepub()
        {
            var feed = OpdsSource.Parse("""
                <feed xmlns="http://www.w3.org/2005/Atom" xmlns:dc="http://purl.org/dc/terms/">
                  <entry>
                    <id>urn:book:1</id><title>Emma</title><author><name>Jane Austen</name></author><dc:language>en</dc:language>
                    <link rel="http://opds-spec.org/acquisition" type="application/epub+zip" href="/get/epub/1"/>
                    <link rel="http://opds-spec.org/acquisition" type="application/kepub+zip" href="/get/kepub/1"/>
                  </entry>
                  <entry>
                    <id>urn:book:2</id><title>For sale</title>
                    <link rel="http://opds-spec.org/acquisition/buy" type="application/epub+zip" href="/buy/2"/>
                  </entry>
                  <entry>
                    <id>urn:book:3</id><title>A PDF</title>
                    <link rel="http://opds-spec.org/acquisition" type="application/pdf" href="/get/pdf/3"/>
                  </entry>
                </feed>
                """);
            var results = OpdsSource.ParseEntries(feed, new Uri("http://192.168.1.10:8080/opds/search?q=emma"), "cat1", "Home");
            var emma = Assert.Single(results);
            Assert.Equal("kepub", emma.Format);
            Assert.Equal("http://192.168.1.10:8080/get/kepub/1", emma.DownloadUrl!.AbsoluteUri);
            Assert.Matches("^op-[0-9a-f]{8}$", emma.Id);
            Assert.Equal("cat1", emma.CatalogueId);
            Assert.DoesNotContain("192.168", emma.ToJson().ToJsonString());
        }

        [Fact]
        public void OpdsSearchAddressesComeFromTheOpenSearchDescription()
        {
            var description = XDocument.Parse("""
                <OpenSearchDescription xmlns="http://a9.com/-/spec/opensearch/1.1/">
                  <Url type="text/html" template="/search?q={searchTerms}"/>
                  <Url type="application/atom+xml" template="/opds/search/{searchTerms}?page={startPage?}"/>
                </OpenSearchDescription>
                """);
            var template = OpdsSource.SearchTemplate(description, new Uri("https://books.example.org/opds/osd"))!;
            Assert.Equal("https://books.example.org/opds/search/{searchTerms}?page={startPage?}", template);
            Assert.Equal("https://books.example.org/opds/search/jane%20austen?page=", OpdsSource.FillTemplate(template, "jane austen"));
        }

        [Fact]
        public void CatalogueSignInsAreEncryptedAndThePathIsNotShown()
        {
            var catalogue = OpdsCatalogue.Create(" Home ", "https://kavita.example.org/api/opds/secret-key", "me", "pw");
            Assert.Equal("Home", catalogue.Name);
            Assert.StartsWith("dpapi:", catalogue.Address);
            Assert.StartsWith("dpapi:", catalogue.Password);
            Assert.Equal("https://kavita.example.org", catalogue.Server);
            Assert.Equal("pw", catalogue.GetPassword());

            var sources = new SourceSettings();
            Assert.False(sources.GoogleBooksReady);
            sources.SetGoogleBooksKey("key");
            Assert.True(sources.GoogleBooksReady);
            Assert.DoesNotContain("GoogleBooksReady", JsonSerializer.Serialize(sources));
        }

        private static byte[] Epub()
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                {
                    writer.Write("application/epub+zip");
                }
                zip.CreateEntry("OEBPS/content.opf");
            }
            return memory.ToArray();
        }

        [Fact]
        public async Task DownloadsKeepOnlyRealEpubsAndNeverOverwrite()
        {
            var folder = TestFolder.Fresh("source-downloads");
            var url = new Uri("https://example.org/book.epub");

            var page = new HttpClient(new StubHandler(Encoding.UTF8.GetBytes("<html>Sign in</html>"), "text/html"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EbookDownloader.DownloadAsync(url, page, folder, "Emma", "Jane Austen", "epub", CancellationToken.None));
            Assert.Empty(Directory.GetFiles(folder));

            var handler = new StubHandler(Epub(), "application/epub+zip");
            var client = new HttpClient(handler);
            var (path, already) = await EbookDownloader.DownloadAsync(url, client, folder, "Emma: A Novel", "Jane Austen, Someone Else", "epub", CancellationToken.None);
            Assert.False(already);
            Assert.Equal("Emma_ A Novel - Jane Austen.epub", Path.GetFileName(path));
            Assert.Single(Directory.GetFiles(folder));

            var again = await EbookDownloader.DownloadAsync(url, client, folder, "Emma: A Novel", "Jane Austen", "epub", CancellationToken.None);
            Assert.True(again.AlreadyThere);
            Assert.Single(handler.Requests);

            Assert.EndsWith(".kepub.epub", EbookDownloader.FileName("Emma", "Jane Austen", "kepub"));
        }

        [Fact]
        public void TheUserAgentNamesTheAppAndNoPerson()
        {
            Assert.StartsWith("HoboLibris/", SourceHttp.UserAgent);
            Assert.Contains("github.com/paulfilkin/HoboLibris", SourceHttp.UserAgent);
            Assert.DoesNotContain("@", SourceHttp.UserAgent);
        }
    }
}
