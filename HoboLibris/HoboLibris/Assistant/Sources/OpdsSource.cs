using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HoboLibris.Assistant.Sources
{
    /// <summary>
    /// An OPDS catalogue the person added: Calibre's content server, Calibre-Web, Kavita, a library
    /// server.  The user name and password go only to the catalogue's own server (scheme, host and
    /// port); a redirect elsewhere gets no sign-in.  Kavita puts its key in the address, so the
    /// address is kept encrypted too and never shown to the model.
    /// </summary>
    public sealed class OpdsSource
    {
        private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
        private static readonly XNamespace OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";
        private static readonly XNamespace Dc = "http://purl.org/dc/terms/";
        private static readonly XNamespace DcElements = "http://purl.org/dc/elements/1.1/";

        private readonly OpdsCatalogue _catalogue;
        private HttpClient? _client;

        public OpdsSource(OpdsCatalogue catalogue) => _catalogue = catalogue;

        public string Name => _catalogue.Name;

        /// <summary>A client that signs in to the catalogue's own server only.</summary>
        public HttpClient Client => _client ??= CreateClient(new Uri(_catalogue.GetAddress()), _catalogue.GetUserName(), _catalogue.GetPassword());

        private static HttpClient CreateClient(Uri address, string user, string password)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true, PreAuthenticate = true };
            if (user.Length > 0)
            {
                var origin = new Uri(address.GetLeftPart(UriPartial.Authority) + "/");
                var cache = new CredentialCache();
                var credential = new NetworkCredential(user, password);
                cache.Add(origin, "Basic", credential);
                cache.Add(origin, "Digest", credential);
                handler.Credentials = cache;
            }
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(SourceHttp.UserAgent);
            return client;
        }

        public async Task<List<EbookResult>> SearchAsync(string query, int limit, CancellationToken cancel)
        {
            var root = new Uri(_catalogue.GetAddress());
            var feed = Parse(await SourceHttp.GetTextAsync(root, cancel, Client));
            var template = await SearchTemplateAsync(feed, root, cancel)
                ?? throw new InvalidOperationException($"The catalogue \"{Name}\" offers no search.");
            var url = new Uri(FillTemplate(template, query));
            var results = ParseEntries(Parse(await SourceHttp.GetTextAsync(url, cancel, Client)), url, _catalogue.Id, Name);
            return results.Take(limit).ToList();
        }

        /// <summary>The search address with {searchTerms} in it: from an OpenSearch description, or the link itself.</summary>
        private async Task<string?> SearchTemplateAsync(XDocument feed, Uri root, CancellationToken cancel)
        {
            var link = feed.Root?.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") == "search");
            var href = (string?)link?.Attribute("href");
            if (href == null)
            {
                return null;
            }
            var resolved = new Uri(root, href).ToString();
            var type = (string?)link!.Attribute("type") ?? "";
            if (resolved.Contains("%7BsearchTerms%7D", StringComparison.OrdinalIgnoreCase) || resolved.Contains("{searchTerms}"))
            {
                return Uri.UnescapeDataString(resolved);
            }
            if (!type.Contains("opensearchdescription", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var descriptionUri = new Uri(resolved);
            var description = Parse(await SourceHttp.GetTextAsync(descriptionUri, cancel, Client));
            return SearchTemplate(description, descriptionUri);
        }

        /// <summary>The Atom search address in an OpenSearch description.</summary>
        public static string? SearchTemplate(XDocument description, Uri baseUri)
        {
            var urls = description.Root?.Elements(OpenSearch + "Url").ToList() ?? new List<XElement>();
            var url = urls.FirstOrDefault(u => ((string?)u.Attribute("type") ?? "").Contains("atom", StringComparison.OrdinalIgnoreCase))
                      ?? urls.FirstOrDefault();
            var template = (string?)url?.Attribute("template");
            return template == null ? null : new Uri(baseUri, template.Replace("{searchTerms}", "SEARCHTERMS"))
                .ToString().Replace("SEARCHTERMS", "{searchTerms}");
        }

        /// <summary>Puts the query in; optional parameters ({startPage?}) are dropped.</summary>
        public static string FillTemplate(string template, string query) =>
            Regex.Replace(template.Replace("{searchTerms}", Uri.EscapeDataString(query)), @"\{[^}]*\?\}", "");

        public static XDocument Parse(string xml)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader);
        }

        /// <summary>The books in a feed that can be downloaded as EPUB or KEPUB without buying or borrowing.</summary>
        public static List<EbookResult> ParseEntries(XDocument feed, Uri feedUri, string catalogueId, string catalogueName)
        {
            var results = new List<EbookResult>();
            foreach (var entry in feed.Root?.Elements(Atom + "entry") ?? Enumerable.Empty<XElement>())
            {
                var links = entry.Elements(Atom + "link").ToList();
                var files = links
                    .Where(l => ((string?)l.Attribute("rel") ?? "") is var rel
                                && (rel == "http://opds-spec.org/acquisition" || rel == "http://opds-spec.org/acquisition/open-access"))
                    .Select(l => (Href: (string?)l.Attribute("href"), Type: ((string?)l.Attribute("type") ?? "").ToLowerInvariant()))
                    .Where(l => l.Href != null && (l.Type.Contains("epub") || l.Type.Contains("kepub")))
                    .OrderBy(l => l.Type.Contains("kepub") ? 0 : 1)
                    .ToList();
                if (files.Count == 0)
                {
                    continue;
                }
                var file = files[0];
                var page = links.FirstOrDefault(l => (string?)l.Attribute("rel") == "alternate" && ((string?)l.Attribute("type") ?? "").Contains("html"));
                var key = (string?)entry.Element(Atom + "id") ?? file.Href!;
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalogueId + "|" + key)))[..8].ToLowerInvariant();
                var year = ((string?)entry.Element(Dc + "issued") ?? (string?)entry.Element(Atom + "published") ?? "") is { Length: >= 4 } date ? date[..4] : null;
                results.Add(new EbookResult
                {
                    Id = "op-" + hash,
                    Source = "opds",
                    Title = ((string?)entry.Element(Atom + "title") ?? "").Trim(),
                    Authors = string.Join(", ", entry.Elements(Atom + "author").Select(a => ((string?)a.Element(Atom + "name") ?? "").Trim()).Where(a => a.Length > 0)),
                    Year = year,
                    Language = (string?)entry.Element(Dc + "language") ?? (string?)entry.Element(DcElements + "language"),
                    Note = "From " + catalogueName,
                    DownloadUrl = new Uri(feedUri, file.Href!),
                    Format = file.Type.Contains("kepub") ? "kepub" : "epub",
                    PageUrl = page == null ? null : new Uri(feedUri, (string)page.Attribute("href")!),
                    CatalogueId = catalogueId,
                });
            }
            return results;
        }
    }
}
