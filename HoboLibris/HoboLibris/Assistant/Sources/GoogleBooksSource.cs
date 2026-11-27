using System.Text.Json.Nodes;

namespace HoboLibris.Assistant.Sources
{
    /// <summary>
    /// Google Books, with the person's own API key: free e-books, and with paid (#91) e-books for sale
    /// too, listed with their price for the person to buy elsewhere.  A download is kept only when
    /// Google sends an actual EPUB of a free book; otherwise the person gets the book's page to open.
    /// </summary>
    public static class GoogleBooksSource
    {
        public static Uri SearchUrl(string? query, string? author, string? language, int limit, string key, bool paid = false)
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
                + (paid ? "&filter=ebooks" : "&filter=free-ebooks") + "&maxResults=" + Math.Clamp(limit, 1, 40);
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
                    var sale = item["saleInfo"] as JsonObject;
                    // FREE, FOR_SALE, FOR_PRESALE, NOT_FOR_SALE...; a free-ebooks search may leave it out.
                    var saleability = sale?["saleability"]?.ToString();
                    bool free = saleability is null or "FREE";
                    var epub = item["accessInfo"]?["epub"] as JsonObject;
                    var link = free ? epub?["downloadLink"]?.ToString() : null;
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
                        Free = free,
                        Price = free ? null : Price(sale?["retailPrice"] ?? sale?["listPrice"]),
                        Note = !free ? (saleability == "NOT_FOR_SALE" ? "Not for sale on Google Books." : null)
                            : link == null ? "Google offers no EPUB download for this book; it can be read on its page." : null,
                    };
                })
                .ToList();

        /// <summary>"9.99 GBP" from Google's {"amount":9.99,"currencyCode":"GBP"}; null when there is no amount.</summary>
        private static string? Price(JsonNode? price)
        {
            if (price?["amount"] is not JsonValue amount || !amount.TryGetValue(out double value))
            {
                return null;
            }
            var currency = price["currencyCode"]?.ToString();
            var text = value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(currency) ? text : $"{text} {currency}";
        }

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
