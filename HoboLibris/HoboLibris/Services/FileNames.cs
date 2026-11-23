using System.IO;

namespace HoboLibris.Services
{
    /// <summary>How book files are named on this computer.</summary>
    public static class FileNames
    {
        private static readonly char[] AuthorSeparators = { ',', '&' };

        public static string FirstAuthor(string authors)
        {
            var i = authors.IndexOf(", ", StringComparison.Ordinal);
            return i < 0 ? authors : authors[..i];
        }

        /// <summary>A safe Windows file name: reserved characters become "_", at most 150 characters.</summary>
        public static string Sanitize(string name)
        {
            var chars = name.Select(c => c switch
            {
                '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' => '_',
                _ when char.IsControl(c) => '_',
                _ => c,
            }).ToArray();
            var trimmed = new string(chars).Trim().TrimEnd('.').Trim();
            var limited = trimmed.Length > 150 ? trimmed[..150] : trimmed;
            return limited.Length == 0 ? "Untitled" : limited;
        }

        /// <summary>
        /// The copy's file name for a book from the eReader: "Title - Author.ext" (the extension as
        /// on the eReader, so ".kepub.epub" stays), made unique among the names already given.
        /// </summary>
        public static string CopyName(string title, string authors, string lowerFileName, string format, HashSet<string> used)
        {
            string ext;
            if (lowerFileName.EndsWith(".kepub.epub", StringComparison.Ordinal))
            {
                ext = "kepub.epub";
            }
            else
            {
                var dot = lowerFileName.LastIndexOf('.');
                ext = dot < 0 ? "" : lowerFileName[(dot + 1)..];
            }
            var author = authors.Split(AuthorSeparators, 2)[0].Trim();
            var baseName = Sanitize(author.Length == 0 ? title : $"{title} - {author}");
            var name = $"{baseName}.{ext}";
            if (!used.Add(name.ToLowerInvariant()))
            {
                name = $"{baseName} ({format}).{ext}";
                var n = 2;
                while (!used.Add(name.ToLowerInvariant()))
                {
                    name = $"{baseName} ({n}).{ext}";
                    n++;
                }
            }
            return name;
        }

        /// <summary>"Some Title_B00ABCDEFG.azw3" -> "Some Title".</summary>
        public static string TitleFromName(string file)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var cleaned = stem;
            var us = stem.LastIndexOf('_');
            if (us >= 0)
            {
                var tail = stem[(us + 1)..];
                if (tail.Length == 10 && tail.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
                {
                    cleaned = stem[..us];
                }
            }
            return cleaned.Replace('_', ' ').Trim();
        }

        /// <summary>The file name's stem, treating ".kepub.epub" as one extension.</summary>
        public static string StemOf(string fileName)
        {
            if (fileName.EndsWith(".kepub.epub", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^".kepub.epub".Length];
            }
            var dot = fileName.LastIndexOf('.');
            return dot < 0 ? fileName : fileName[..dot];
        }

        /// <summary>The lower-case extension and the format label: "kepub.epub" is ("epub", "KEPUB").</summary>
        public static (string Ext, string Format) LowerExtension(string path)
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            if (name.EndsWith(".kepub.epub", StringComparison.Ordinal))
            {
                return ("epub", "KEPUB");
            }
            var ext = Path.GetExtension(name).TrimStart('.');
            return (ext, ext.ToUpperInvariant());
        }

        /// <summary>
        /// "Title - Author.ext" (how this app names copies) -> ("Title", "Author"); otherwise the
        /// file name and no author. File names can't hold ":", so copies have "_" there; it is
        /// shown as ": " again.
        /// </summary>
        public static (string Title, string Author) TitleAuthor(string path)
        {
            var stem = StemOf(Path.GetFileName(path));
            static string Tidy(string s) => s.Replace("_ ", ": ").Replace('_', ' ').Trim();
            var sep = stem.LastIndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0 && stem[..sep].Trim().Length > 0)
            {
                return (Tidy(stem[..sep]), Tidy(stem[(sep + 3)..]));
            }
            return (Tidy(stem), "");
        }
    }
}
