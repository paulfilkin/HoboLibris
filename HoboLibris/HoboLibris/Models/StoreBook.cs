using System.IO;
using System.Text.Json.Serialization;
using HoboLibris.Services;

namespace HoboLibris.Models
{
    /// <summary>One download the Kobo store offers for a book.</summary>
    public class DownloadLink
    {
        [JsonPropertyName("drm")]
        public string Drm { get; set; } = "";

        [JsonPropertyName("format")]
        public string Format { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonIgnore]
        public bool IsProtected
        {
            get
            {
                var d = Drm.ToLowerInvariant();
                return !(d.Length == 0 || d == "none" || d == "signednodrm");
            }
        }
    }

    /// <summary>A book in the person's Kobo account.</summary>
    public class StoreBook
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("authors")]
        public string Authors { get; set; } = "";

        [JsonPropertyName("links")]
        public List<DownloadLink> Links { get; set; } = new();

        /// <summary>The link the app downloads: unprotected first, then EPUB3, EPUB, KEPUB.</summary>
        public DownloadLink? BestLink()
        {
            DownloadLink? best = null;
            var bestScore = int.MinValue;
            foreach (var l in Links)
            {
                var f = l.Format.ToUpperInvariant();
                if (f.Contains("SAMPLE") || f == "MANIFEST" || string.IsNullOrEmpty(l.Url))
                {
                    continue;
                }
                var score = l.IsProtected ? 0 : 100;
                score += f switch
                {
                    "EPUB3" => 30,
                    "EPUB" => 25,
                    "KEPUB" => 20,
                    "EPUB3FL" or "FLEPUB" => 15,
                    "PDF" => 10,
                    _ => 5,
                };
                if (l.Platform.Equals("generic", StringComparison.OrdinalIgnoreCase))
                {
                    score += 3;
                }
                // max_by_key keeps the last of equal keys.
                if (score >= bestScore)
                {
                    best = l;
                    bestScore = score;
                }
            }
            return best;
        }

        public string FormatLabel => BestLink()?.Format ?? "-";

        /// <summary>True when the copy the store delivers is copy-protected.</summary>
        public bool IsProtected => BestLink()?.IsProtected ?? false;

        public bool CanDownload => BestLink() != null;

        /// <summary>File name (without folder or extension): "Title - First author".</summary>
        public string FileStem()
        {
            var baseName = string.IsNullOrEmpty(Authors)
                ? Title
                : $"{Title} - {FileNames.FirstAuthor(Authors)}";
            return FileNames.Sanitize(baseName);
        }

        /// <summary>The name earlier versions used ("Author - Title"), so those copies still count as saved.</summary>
        private string? OldFileStem() =>
            string.IsNullOrEmpty(Authors) ? null : FileNames.Sanitize($"{FileNames.FirstAuthor(Authors)} - {Title}");

        public string Extension
        {
            get
            {
                var f = BestLink()?.Format.ToUpperInvariant();
                return f switch
                {
                    "KEPUB" => "kepub.epub",
                    "PDF" => "pdf",
                    _ => "epub",
                };
            }
        }

        /// <summary>The saved copy, if one is already in the folder.</summary>
        public string? SavedIn(string dir)
        {
            var stems = new List<string> { FileStem() };
            var old = OldFileStem();
            if (old != null)
            {
                stems.Add(old);
            }
            foreach (var stem in stems)
            {
                foreach (var ext in new[] { "epub", "kepub.epub", "pdf", "acsm" })
                {
                    var p = Path.Combine(dir, $"{stem}.{ext}");
                    if (File.Exists(p))
                    {
                        return p;
                    }
                }
            }
            return null;
        }
    }
}
