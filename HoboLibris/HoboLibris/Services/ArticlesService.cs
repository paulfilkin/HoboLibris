using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    /// <summary>An article saved to the Kobo eReader with Instapaper (#53).</summary>
    public record Article(string Id, string Title, string Author, string Url, DateTime? Added, int Percent, bool Finished)
    {
        /// <summary>The site it came from ("example.com"), from its address.</summary>
        public string Site => Articles.SiteOf(Url);
    }

    /// <summary>A copy of an article kept on this computer.</summary>
    public record KeptArticle(string Id, string Title, string Author, string Url, DateTime? Added, string Path);

    /// <summary>
    /// Articles on the Kobo eReader, and the copies kept on this computer (#53).  As found with
    /// firmware 4.46 on 25 November 2026: each article is a row in the book list's content table
    /// (ContentType 6, MimeType "application/x-kobo-html+instapaper", a numeric ContentID, the page it
    /// came from in ContentURL), and its text is ".kobo\articles\&lt;id&gt;\index.html", an HTML
    /// fragment whose pictures are files beside it, named by hash with no extension and referred to as
    /// "file:///mnt/onboard/.kobo/articles/&lt;id&gt;/&lt;name&gt;".  A kept copy is one web page with
    /// the pictures inside it, so it can be opened, moved and kept as a single file.
    ///
    /// Archiving an article on the eReader (tested on 25 November 2026) removes its folder, its
    /// ContentType 9 row and its volume_shortcovers row, and keeps its content row with PocketStatus 2
    /// and IsDownloaded 'false'; such an article is no longer on the eReader.
    /// </summary>
    public static class Articles
    {
        /// <summary>Instapaper's articles; Pocket's earlier ones are taken to be of the same family.</summary>
        public const string MimeTypes = "application/x-kobo-html%";

        /// <summary>What marks a kept copy, with the article's id: how the Articles screen finds them again.</summary>
        private const string IdMeta = "hobolibris-article-id";
        private const string UrlMeta = "hobolibris-article-url";
        private const string AddedMeta = "hobolibris-article-added";

        public static string FolderOf(string root, string id) => Path.Combine(root, ".kobo", "articles", id);

        public static string SiteOf(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                return "";
            }
            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        }

        /// <summary>The articles on the Kobo eReader whose drive root is root, newest first.</summary>
        public static List<Article> Read(string root)
        {
            var db = KoboDeviceService.DatabasePath(root);
            if (!File.Exists(db))
            {
                throw new HoboLibrisException(Strings.Coll_BookListNotFound);
            }
            return ReadDb(db);
        }

        public static List<Article> ReadDb(string db)
        {
            try
            {
                using var conn = KoboDeviceService.OpenReadOnly(db);
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var info = conn.CreateCommand())
                {
                    info.CommandText = "PRAGMA table_info(content)";
                    using var rows = info.ExecuteReader();
                    while (rows.Read())
                    {
                        cols.Add(rows.GetString(1));
                    }
                }
                if (!cols.Contains("MimeType"))
                {
                    return new List<Article>();
                }
                // Columns an older book list may not have are read as empty.
                string Col(string name, string empty) => cols.Contains(name) ? $"\"{name}\"" : empty;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ContentID, IFNULL(Title, ''), IFNULL(Attribution, ''), "
                    + $"IFNULL({Col("ContentURL", "NULL")}, ''), IFNULL({Col("DateAdded", "NULL")}, ''), "
                    + $"IFNULL({Col("___PercentRead", "0")}, 0), IFNULL({Col("ReadStatus", "0")}, 0) "
                    + $"FROM content WHERE ContentType = 6 AND MimeType LIKE '{MimeTypes}'"
                    // Archived on the eReader: the row stays, the text has gone.
                    + (cols.Contains("PocketStatus") ? " AND IFNULL(PocketStatus, 0) <> 2" : "")
                    + (cols.Contains("IsDownloaded") ? " AND IFNULL(IsDownloaded, 'true') NOT IN ('false', '0')" : "");
                var found = new List<Article>();
                using (var rows = cmd.ExecuteReader())
                {
                    while (rows.Read())
                    {
                        var id = rows.GetString(0);
                        var title = rows.GetString(1).Trim();
                        found.Add(new Article(id, title.Length > 0 ? title : id, rows.GetString(2).Trim(), rows.GetString(3).Trim(),
                            ParseTime(rows.GetValue(4)?.ToString()), ToInt(rows.GetValue(5)), ToInt(rows.GetValue(6)) == 2));
                    }
                }
                return found.OrderByDescending(a => a.Added ?? DateTime.MinValue).ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (SqliteException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotRead, ex.Message), ex);
            }
        }

        private static int ToInt(object? value) =>
            value != null && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        private static DateTime? ParseTime(string? text) =>
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

        /// <summary>Keep a copy of an article in dir.  Returns the copy's path.</summary>
        public static string Keep(string root, Article article, string dir)
        {
            var folder = FolderOf(root, article.Id);
            var index = Path.Combine(folder, "index.html");
            if (!File.Exists(index))
            {
                throw new HoboLibrisException(string.Format(Strings.Art_TextNotFound, article.Title));
            }
            var html = Document(article, File.ReadAllText(index, Encoding.UTF8), name =>
            {
                var path = Path.Combine(folder, name);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            });
            Directory.CreateDirectory(dir);
            var target = PathFor(dir, article);
            File.WriteAllText(target, html, new UTF8Encoding(false));
            return target;
        }

        private static readonly Regex Scripts = new(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex ImageSources = new(@"(<img\b[^>]*?\bsrc\s*=\s*)([""'])([^""']*)\2", RegexOptions.IgnoreCase);
        private static readonly Regex SafeName = new(@"^[A-Za-z0-9._-]+$");

        /// <summary>
        /// The kept copy of an article: a whole web page with its title, author, source and date at the
        /// top, the article's pictures inside it, and no scripts.  image gives a picture's bytes by its
        /// file name in the article's folder, or null.
        /// </summary>
        public static string Document(Article article, string fragment, Func<string, byte[]?> image)
        {
            fragment = Scripts.Replace(fragment, "");
            fragment = ImageSources.Replace(fragment, m =>
            {
                var src = m.Groups[3].Value;
                if (src.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    return m.Value;
                }
                // Only a plain file name in the article's own folder.
                var name = src.Split('/', '\\').Last();
                var bytes = SafeName.IsMatch(name) && name != "." && name != ".." ? image(name) : null;
                return bytes == null
                    ? m.Value
                    : m.Groups[1].Value + m.Groups[2].Value + $"data:{ImageType(bytes)};base64,{Convert.ToBase64String(bytes)}" + m.Groups[2].Value;
            });

            static string E(string s) => WebUtility.HtmlEncode(s);
            var link = SiteOf(article.Url).Length > 0 ? $"<a href=\"{E(article.Url)}\">{E(SiteOf(article.Url))}</a>" : "";
            var byline = string.Join(" · ", new[]
            {
                E(article.Author),
                link,
                article.Added?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            }.Where(s => s.Length > 0));
            var b = new StringBuilder();
            b.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
            b.Append($"<title>{E(article.Title)}</title>\n");
            if (article.Author.Length > 0)
            {
                b.Append($"<meta name=\"author\" content=\"{E(article.Author)}\">\n");
            }
            b.Append($"<meta name=\"{IdMeta}\" content=\"{E(article.Id)}\">\n");
            b.Append($"<meta name=\"{UrlMeta}\" content=\"{E(article.Url)}\">\n");
            if (article.Added is { } added)
            {
                b.Append($"<meta name=\"{AddedMeta}\" content=\"{added.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\">\n");
            }
            b.Append("<style>\n");
            b.Append("body { max-width: 42em; margin: 2em auto; padding: 0 1em; font-family: Georgia, serif; font-size: 1.1em; line-height: 1.6; }\n");
            b.Append("header { border-bottom: 1px solid #ccc; margin-bottom: 1.5em; }\n");
            b.Append("header p { font-family: sans-serif; font-size: 0.9em; color: #555; }\n");
            b.Append("img { max-width: 100%; height: auto; }\n");
            b.Append("</style>\n</head>\n<body>\n<header>\n");
            b.Append($"<h1>{E(article.Title)}</h1>\n");
            if (byline.Length > 0)
            {
                b.Append($"<p>{byline}</p>\n");
            }
            b.Append("</header>\n<main>\n").Append(fragment).Append("\n</main>\n</body>\n</html>\n");
            return b.ToString();
        }

        /// <summary>A picture's type from its first bytes (the files have no extension).</summary>
        public static string ImageType(byte[] bytes)
        {
            bool Starts(params byte[] head) => bytes.Length >= head.Length && head.Select((x, i) => bytes[i] == x).All(same => same);
            if (Starts(0xFF, 0xD8, 0xFF))
            {
                return "image/jpeg";
            }
            if (Starts(0x89, 0x50, 0x4E, 0x47))
            {
                return "image/png";
            }
            if (Starts(0x47, 0x49, 0x46, 0x38))
            {
                return "image/gif";
            }
            if (bytes.Length >= 12 && Starts(0x52, 0x49, 0x46, 0x46) && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                return "image/webp";
            }
            var start = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 256)).TrimStart();
            return start.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                ? "image/svg+xml"
                : "image/jpeg";
        }

        /// <summary>"Title - Author.html" in dir; the same file again for the same article, another name for another one.</summary>
        public static string PathFor(string dir, Article article)
        {
            var name = Clean(article.Title);
            if (article.Author.Length > 0)
            {
                name += " - " + Clean(article.Author);
            }
            if (name.Length > 120)
            {
                name = name[..120].TrimEnd(' ', '.');
            }
            if (name.Length == 0)
            {
                name = "Article " + article.Id;
            }
            var path = Path.Combine(dir, name + ".html");
            if (File.Exists(path) && ReadKept(path)?.Id is { } id && id != article.Id)
            {
                path = Path.Combine(dir, $"{name} ({Clean(article.Id)}).html");
            }
            return path;
        }

        private static string Clean(string text)
        {
            var bad = Path.GetInvalidFileNameChars();
            var cleaned = new string(text.Select(c => bad.Contains(c) ? ' ' : c).ToArray());
            return Regex.Replace(cleaned, @"\s+", " ").Trim().Trim('.').Trim();
        }

        /// <summary>
        /// Remove articles from the eReader whose drive root is root: their content rows (the article and
        /// its text), their volume_shortcovers rows and their folders, as the eReader's own Delete left
        /// things (seen with articles deleted on 21 November 2026).  The book list is copied to backupDir
        /// first, and returned.  Tested on 25 November 2026: an article removed so stayed gone after the
        /// eReader synced, but Instapaper isn't told, so it is still in the person's account.
        /// </summary>
        public static string Remove(string root, IReadOnlyCollection<string> ids, string backupDir)
        {
            var db = KoboDeviceService.DatabasePath(root);
            if (!File.Exists(db))
            {
                throw new HoboLibrisException(Strings.Coll_ReaderGone);
            }
            var backup = BookListBackups.Take(db, backupDir);
            try
            {
                // Not pooled, so the file is closed even when writing fails (the eReader can then be ejected).
                using var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False");
                conn.Open();
                using var tx = conn.BeginTransaction();
                foreach (var id in ids)
                {
                    foreach (var sql in new[]
                    {
                        "DELETE FROM volume_shortcovers WHERE volumeId = $id",
                        "DELETE FROM content WHERE BookID = $id AND ContentType = 9",
                        $"DELETE FROM content WHERE ContentID = $id AND ContentType = 6 AND MimeType LIKE '{MimeTypes}'",
                    })
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = sql;
                        cmd.Parameters.AddWithValue("$id", id);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            catch (SqliteException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Art_CouldNotRemove, ex.Message), ex);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
            foreach (var id in ids)
            {
                var folder = FolderOf(root, id);
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    // The rows are gone, so the eReader no longer lists it; the folder is only space.
                    LogManager.Warn($"Could not remove {folder}: {ex.Message}");
                }
            }
            return backup;
        }

        /// <summary>The copies kept in dir.</summary>
        public static List<KeptArticle> Kept(string dir)
        {
            var kept = new List<KeptArticle>();
            try
            {
                if (!Directory.Exists(dir))
                {
                    return kept;
                }
                foreach (var path in Directory.GetFiles(dir, "*.html"))
                {
                    if (ReadKept(path) is { } k)
                    {
                        kept.Add(k);
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warn($"Could not read the kept articles in {dir}: {ex.Message}");
            }
            return kept;
        }

        private static string? Meta(string head, string name)
        {
            var m = Regex.Match(head, $"<meta name=\"{Regex.Escape(name)}\" content=\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
        }

        /// <summary>A kept copy's details, from the top of the file; null when the file isn't one.</summary>
        private static KeptArticle? ReadKept(string path)
        {
            try
            {
                var buffer = new char[8192];
                int read;
                using (var reader = new StreamReader(path, Encoding.UTF8))
                {
                    read = reader.ReadBlock(buffer, 0, buffer.Length);
                }
                var head = new string(buffer, 0, read);
                var id = Meta(head, IdMeta);
                if (id == null)
                {
                    return null;
                }
                var title = Regex.Match(head, "<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                return new KeptArticle(id, title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value) : Path.GetFileNameWithoutExtension(path),
                    Meta(head, "author") ?? "", Meta(head, UrlMeta) ?? "", ParseTime(Meta(head, AddedMeta)), path);
            }
            catch (Exception ex)
            {
                LogManager.Debug($"Could not read {path}: {ex.Message}");
                return null;
            }
        }
    }
}
