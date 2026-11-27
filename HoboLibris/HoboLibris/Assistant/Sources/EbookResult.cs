using System.Text.Json.Nodes;

namespace HoboLibris.Assistant.Sources
{
    /// <summary>
    /// A book found in a source.  Id is what the model uses ("pg-145", "ol-OL20151W"); the download
    /// address stays here and is never shown to the model, since an OPDS address may hold a key.
    /// </summary>
    public sealed record EbookResult
    {
        public required string Id { get; init; }
        public required string Source { get; init; }
        public required string Title { get; init; }
        public string Authors { get; init; } = "";
        public string? Year { get; init; }
        public string? Language { get; init; }
        public string? Note { get; init; }

        /// <summary>A file to download, when the search already knows it (Google Books, OPDS).</summary>
        public Uri? DownloadUrl { get; init; }

        /// <summary>"epub" or "kepub".</summary>
        public string Format { get; init; } = "epub";

        /// <summary>The book's page on the source's website, for the person to open.</summary>
        public Uri? PageUrl { get; init; }

        /// <summary>For an OPDS catalogue: which one, so its sign-in is used for the download.</summary>
        public string? CatalogueId { get; init; }

        /// <summary>For Open Library: the Internet Archive items that may hold the file.</summary>
        public IReadOnlyList<string> ArchiveItems { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Free to download here.  A book that isn't (#91) is listed for the person to find and buy
        /// elsewhere, and is never downloaded.
        /// </summary>
        public bool Free { get; init; } = true;

        /// <summary>The price the source gives for a book for sale, such as "9.99 GBP"; null when it gives none.</summary>
        public string? Price { get; init; }

        public JsonObject ToJson()
        {
            var json = new JsonObject
            {
                ["id"] = Id,
                ["title"] = Title,
                ["authors"] = Authors,
            };
            if (Year != null)
            {
                json["year"] = Year;
            }
            if (Language != null)
            {
                json["language"] = Language;
            }
            if (Format == "kepub")
            {
                json["format"] = "kepub";
            }
            if (!Free)
            {
                json["free"] = false;
                if (Price != null)
                {
                    json["price"] = Price;
                }
            }
            if (Note != null)
            {
                json["note"] = Note;
            }
            return json;
        }
    }
}
