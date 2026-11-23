using System.Text.Json.Nodes;

namespace HoboLibris.Assistant.Sources
{
    /// <summary>
    /// Google Books, with the person's own API key: free e-books only.  A download is kept only when
    /// Google sends an actual EPUB; otherwise the person gets the book's page to open.
    /// </summary>
    public static class GoogleBooksSource
    {
        public static Uri SearchUrl(string? query, string? author, string? language, int limit, string key)
        {
            var q = new List<string>();
            if (!string.IsNullOrWhiteSpace(query))
            {
                q.Add(query.Trim());
            }
            if (!string.IsNullOrWhiteSpace(author))
            {
                q.Add("inauthor:" + author.Trim());
            }
            var url = "https://www.googleapis.com/books/v1/volumes?q=" + Uri.EscapeDataString(string.Join(" ", q))
                + "&filter=free-ebooks&maxResults=" + Math.Clamp(limit, 1, 40);
            if (!string.IsNullOrWhiteSpace(language))
            {
                url += "&langRestrict=" + Uri.EscapeDataString(language.Trim());
            }
            return new Uri(url + "&key=" + Uri.EscapeDataString(key));
        }

        public static List<EbookResult> ParseSearch(JsonNode json) =>
            (json["items"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(item =>
                {
                    var info = item["volumeInfo"] as JsonObject ?? new JsonObject();
                    var epub = item["accessInfo"]?["epub"] as JsonObject;
                    var link = epub?["downloadLink"]?.ToString();
                    var page = info["infoLink"]?.ToString() ?? item["accessInfo"]?["webReaderLink"]?.ToString();
                    return new EbookResult
                    {
                        Id = "gb-" + item["id"],
                        Source = "google_books",
                        Title = info["title"]?.ToString() ?? "",
                        Authors = string.Join(", ", (info["authors"] as JsonArray ?? new JsonArray()).Select(a => a?.ToString())),
                        Year = info["publishedDate"]?.ToString() is { Length: >= 4 } date ? date[..4] : null,
                        Language = info["language"]?.ToString(),
                        DownloadUrl = Secure(link),
                        PageUrl = Uri.TryCreate(page, UriKind.Absolute, out var p) ? p : null,
                        Note = link == null ? "Google offers no EPUB download for this book; it can be read on its page." : null,
                    };
                })
                .ToList();

        /// <summary>Google's download links often start http://; Google's own hosts are fetched over https.</summary>
        private static Uri? Secure(string? link)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            {
                return null;
            }
            if (!uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return uri.Scheme == "https" ? uri : new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        }
    }
}
