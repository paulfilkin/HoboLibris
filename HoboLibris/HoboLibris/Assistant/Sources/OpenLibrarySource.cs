using System.Text.Json.Nodes;

namespace HoboLibris.Assistant.Sources
{
    /// <summary>
    /// Open Library and the Internet Archive.  The search asks Open Library for books it marks as
    /// public (free to read, not lending), and a download checks each Internet Archive item first:
    /// anything restricted, private or in a lending collection is skipped.  The EPUBs are made from
    /// scans, so their quality varies.
    /// </summary>
    public static class OpenLibrarySource
    {
        /// <summary>Collections whose books are lent, not given away.</summary>
        private static readonly string[] LendingCollections = { "inlibrary", "printdisabled", "lendinglibrary", "internetarchivebooks_restricted" };

        private static readonly Dictionary<string, string> Marc = new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "eng", ["fr"] = "fre", ["de"] = "ger", ["es"] = "spa", ["it"] = "ita", ["pt"] = "por",
            ["nl"] = "dut", ["ru"] = "rus", ["uk"] = "ukr", ["ja"] = "jpn", ["zh"] = "chi", ["ko"] = "kor",
            ["ar"] = "ara", ["he"] = "heb", ["la"] = "lat", ["el"] = "gre", ["sv"] = "swe", ["da"] = "dan",
            ["fi"] = "fin", ["pl"] = "pol", ["no"] = "nor", ["cs"] = "cze", ["hu"] = "hun",
        };

        public static Uri SearchUrl(string? query, string? author, string? language, int limit)
        {
            var q = new List<string>();
            if (!string.IsNullOrWhiteSpace(query))
            {
                q.Add(query.Trim());
            }
            if (!string.IsNullOrWhiteSpace(author))
            {
                q.Add($"author:({author.Trim()})");
            }
            if (!string.IsNullOrWhiteSpace(language))
            {
                q.Add("language:" + (Marc.TryGetValue(language.Trim(), out var code) ? code : language.Trim()));
            }
            // In the query itself: as a separate parameter Open Library ignores it.
            q.Add("ebook_access:public");
            return new Uri("https://openlibrary.org/search.json?q=" + Uri.EscapeDataString(string.Join(" ", q))
                + "&fields=key,title,author_name,first_publish_year,ia,language,ebook_access&limit=" + limit);
        }

        public static List<EbookResult> ParseSearch(JsonNode json) =>
            (json["docs"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Where(d => d["ebook_access"]?.ToString() == "public" && d["ia"] is JsonArray { Count: > 0 })
                .Select(d =>
                {
                    var key = d["key"]?.ToString() ?? "";
                    var work = key.Replace("/works/", "");
                    return new EbookResult
                    {
                        Id = "ol-" + work,
                        Source = "open_library",
                        Title = d["title"]?.ToString() ?? "",
                        Authors = string.Join(", ", (d["author_name"] as JsonArray ?? new JsonArray()).Select(a => a?.ToString())),
                        Year = d["first_publish_year"]?.ToString(),
                        Language = string.Join(", ", (d["language"] as JsonArray ?? new JsonArray()).Take(3).Select(l => l?.ToString())),
                        Note = "Made from a scan; quality varies.",
                        PageUrl = new Uri("https://openlibrary.org" + key),
                        ArchiveItems = ((JsonArray)d["ia"]!).Select(i => i?.ToString() ?? "").Where(i => i.Length > 0).Take(8).ToList(),
                    };
                })
                .ToList();

        /// <summary>The work's Internet Archive items, for a book not found in this conversation's searches.</summary>
        public static Uri WorkUrl(string work) =>
            new("https://openlibrary.org/search.json?q=" + Uri.EscapeDataString($"key:/works/{work}") + "&fields=key,title,author_name,first_publish_year,ia,language,ebook_access&limit=1");

        public static Uri MetadataUrl(string item) => new($"https://archive.org/metadata/{Uri.EscapeDataString(item)}");

        /// <summary>
        /// The EPUB to download from an Internet Archive item, or null with the reason it can't be:
        /// restricted (lent, not given away), in a lending collection, or with no public EPUB.
        /// </summary>
        public static (Uri? File, string? Reason) ChooseFile(string item, JsonNode metadata)
        {
            var meta = metadata["metadata"] as JsonObject;
            if (meta == null)
            {
                return (null, "no such item");
            }
            if (Flag(meta["access-restricted-item"]))
            {
                return (null, "restricted: lent, not given away");
            }
            var collections = meta["collection"] switch
            {
                JsonArray array => array.Select(c => c?.ToString() ?? "").ToList(),
                JsonNode one => new List<string> { one.ToString() },
                _ => new List<string>(),
            };
            if (collections.Any(c => LendingCollections.Contains(c, StringComparer.OrdinalIgnoreCase)))
            {
                return (null, "in a lending collection");
            }
            // A belt to the Archive's braces: a book published within the US copyright term needs a licence.
            if (Year(meta["date"]) is int year && year >= CopyrightCutoff && string.IsNullOrWhiteSpace(meta["licenseurl"]?.ToString()))
            {
                return (null, $"published {year}, may still be in copyright");
            }
            var epub = (metadata["files"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Where(f => (f["name"]?.ToString() ?? "").EndsWith(".epub", StringComparison.OrdinalIgnoreCase)
                            && !Flag(f["private"])
                            && !(f["name"]?.ToString() ?? "").Contains("_encrypted", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f["source"]?.ToString() == "original" ? 0 : 1)
                .FirstOrDefault();
            if (epub == null)
            {
                return (null, "no public EPUB");
            }
            var name = epub["name"]!.ToString();
            return (new Uri($"https://archive.org/download/{Uri.EscapeDataString(item)}/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}"), null);
        }

        /// <summary>Books published before this year are in the public domain in the USA (95 years).</summary>
        public static int CopyrightCutoff => DateTime.UtcNow.Year - 95;

        private static int? Year(JsonNode? date) =>
            date?.ToString() is { Length: >= 4 } text && int.TryParse(text[..4], out var year) ? year : null;

        private static bool Flag(JsonNode? node) =>
            node != null && (node.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) || node.ToString() == "1");
    }
}
