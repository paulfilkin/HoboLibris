using Microsoft.Data.Sqlite;
using HoboLibris.Services;
using HoboLibris.ViewModels;

namespace HoboLibris.Tests
{
    /// <summary>The Articles screen (#53): reading the articles on the eReader and keeping copies of them.</summary>
    public class ArticlesTests
    {
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

        /// <summary>A fake eReader shaped as found with firmware 4.46: two articles and a book in the book list, one article's files.</summary>
        private static string Reader(string name)
        {
            var root = Path.Combine(TestFolder.Fresh(name), "reader") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(Path.Combine(root, ".kobo"));
            var db = KoboDeviceService.DatabasePath(root);
            using (var c = new SqliteConnection($"Data Source={db}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"CREATE TABLE content (ContentID TEXT, ContentType TEXT, MimeType TEXT, BookID TEXT, Title TEXT, Attribution TEXT,
                        ContentURL TEXT, DateAdded TEXT, ___PercentRead INTEGER, ReadStatus INTEGER, IsDownloaded BOOL, PocketStatus INTEGER);
                    CREATE TABLE volume_shortcovers (volumeId TEXT, shortcoverId TEXT, VolumeIndex INTEGER);
                    INSERT INTO content VALUES ('2059301894', 6, 'application/x-kobo-html+instapaper', NULL, 'Who Owns the Code?', 'A. Writer',
                        'https://www.example.com/blog/code', '2026-11-22T17:30:23Z', 37, 1, 'true', 0);
                    INSERT INTO content VALUES ('2059301528', 6, 'application/x-kobo-html+instapaper', NULL, 'Rebuilt in Rust', '',
                        'https://example.org/rust', '2026-11-22T17:27:43Z', 100, 2, 'true', 0);
                    INSERT INTO content VALUES ('2059300000', 6, 'application/x-kobo-html+instapaper', NULL, 'Archived on the eReader', '',
                        'https://example.org/old', '2026-11-21T10:00:00Z', 2, 1, 'false', 2);
                    INSERT INTO content VALUES ('{ca73909e}', 9, 'application/x-kobo-html+instapaper', '2059301894', NULL, NULL, NULL, NULL, 0, 0, 'true', 0);
                    INSERT INTO content VALUES ('{6d7588bb}', 9, 'application/x-kobo-html+instapaper', '2059301528', NULL, NULL, NULL, NULL, 0, 0, 'true', 0);
                    INSERT INTO volume_shortcovers VALUES ('2059301894', '{ca73909e}', 0);
                    INSERT INTO volume_shortcovers VALUES ('2059301528', '{6d7588bb}', 0);
                    INSERT INTO content VALUES ('file:///mnt/onboard/a.epub', 6, 'application/x-kobo-epub+zip', NULL, 'A Book', 'Someone', NULL, NULL, 10, 1, 'true', 0);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var folder = Articles.FolderOf(root, "2059301894");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "index.html"),
                "<div lang=\"en\"><p>In short: it depends.</p><script>alert(1)</script>"
                + "<div class=\"RIL_IMG\"><img src=\"file:///mnt/onboard/.kobo/articles/2059301894/a47d76b183d5e0bf6a0a9be13071150d\" /></div>"
                + "<img src=\"https://example.com/remote.png\"></div>");
            File.WriteAllBytes(Path.Combine(folder, "a47d76b183d5e0bf6a0a9be13071150d"), Jpeg);
            return root;
        }

        /// <summary>The articles on the eReader: not its books, and not the ones archived on it (PocketStatus 2).</summary>
        [Fact]
        public void ReadsTheArticlesAndNotTheBooks()
        {
            var list = Articles.Read(Reader("articles-read"));
            Assert.Equal(new[] { "Who Owns the Code?", "Rebuilt in Rust" }, list.Select(a => a.Title));
            var first = list[0];
            Assert.Equal("A. Writer", first.Author);
            Assert.Equal("example.com", first.Site);
            Assert.Equal(37, first.Percent);
            Assert.False(first.Finished);
            Assert.Equal(new DateTime(2026, 11, 22, 17, 30, 23, DateTimeKind.Utc), first.Added);
            Assert.True(list[1].Finished);
        }

        [Fact]
        public void AKeptCopyIsOnePageWithItsPicturesAndNoScripts()
        {
            var root = Reader("articles-keep");
            var article = Articles.Read(root)[0];
            var dir = Path.Combine(Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!, "kept");
            var path = Articles.Keep(root, article, dir);

            Assert.Equal("Who Owns the Code - A. Writer.html", Path.GetFileName(path));
            var html = File.ReadAllText(path);
            Assert.Contains("<title>Who Owns the Code?</title>", html);
            Assert.Contains("data:image/jpeg;base64," + Convert.ToBase64String(Jpeg), html);
            Assert.DoesNotContain("file:///mnt/onboard", html);
            Assert.Contains("https://example.com/remote.png", html);
            Assert.DoesNotContain("<script", html);
            Assert.Contains("<a href=\"https://www.example.com/blog/code\">example.com</a>", html);
            Assert.Contains("2026-11-22", html);

            // The folder gives it back, with its id, so the screen knows it is kept.
            var kept = Assert.Single(Articles.Kept(dir));
            Assert.Equal("2059301894", kept.Id);
            Assert.Equal("Who Owns the Code?", kept.Title);
            Assert.Equal("A. Writer", kept.Author);
            Assert.Equal(article.Added, kept.Added);

            // Keeping it again writes the same file; another article of the same name gets its own.
            Assert.Equal(path, Articles.Keep(root, article, dir));
            var twin = article with { Id = "999" };
            Assert.NotEqual(path, Articles.PathFor(dir, twin));
        }

        [Fact]
        public void AnArticleWithoutItsTextOnTheEreaderIsNotKept()
        {
            var root = Reader("articles-missing");
            var article = Articles.Read(root)[1];
            Assert.Throws<HoboLibrisException>(() => Articles.Keep(root, article, Path.Combine(root, "..", "kept")));
        }

        private static long Count(string root, string sql)
        {
            using var c = new SqliteConnection($"Data Source={KoboDeviceService.DatabasePath(root)};Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            var n = (long)cmd.ExecuteScalar()!;
            SqliteConnection.ClearAllPools();
            return n;
        }

        /// <summary>
        /// Removing takes away the article's rows and folder, as the eReader's own Delete left things,
        /// after copying the book list; the other articles and the books stay.
        /// </summary>
        [Fact]
        public void RemovingTakesTheRowsAndTheFolderAfterABackup()
        {
            var root = Reader("articles-remove");
            var backups = Path.Combine(root, "..", "backups");
            var backup = Articles.Remove(root, new[] { "2059301894" }, backups);

            Assert.True(File.Exists(backup));
            Assert.Equal(0, Count(root, "SELECT COUNT(*) FROM content WHERE ContentID = '2059301894' OR BookID = '2059301894'"));
            Assert.Equal(0, Count(root, "SELECT COUNT(*) FROM volume_shortcovers WHERE volumeId = '2059301894'"));
            Assert.False(Directory.Exists(Articles.FolderOf(root, "2059301894")));
            Assert.Equal(1, Count(root, "SELECT COUNT(*) FROM volume_shortcovers"));
            Assert.Equal(4, Count(root, "SELECT COUNT(*) FROM content"));
            Assert.Equal(new[] { "Rebuilt in Rust" }, Articles.Read(root).Select(a => a.Title));
        }

        [Fact]
        public async Task TheScreenRemovesTickedArticlesAndSaysWhichHadNoCopy()
        {
            var root = Reader("articles-screen-remove");
            var dir = Path.Combine(root, "..", "kept");
            var screen = new ArticlesViewModel { FindReader = () => root, Folder = () => dir, BackupFolder = () => Path.Combine(root, "..", "backups") };
            await screen.LoadAsync();
            var rows = screen.Rows.ToList();
            await screen.KeepAsync(new[] { rows[0] });

            var prompt = ArticlesViewModel.RemovePrompt(rows);
            Assert.Contains("(2)", prompt);
            Assert.Contains("Rebuilt in Rust", prompt);
            Assert.Contains(string.Format(Resources.Strings.Art_RemoveNotKept, 1), prompt);

            // Removing asks first; a no leaves everything.
            screen.ConfirmRemove = _ => false;
            rows[1].IsSelected = true;
            await screen.RemoveTickedCommand.ExecuteAsync(null);
            Assert.Equal(2, Articles.Read(root).Count);

            screen.ConfirmRemove = _ => true;
            await screen.RemoveTickedCommand.ExecuteAsync(null);
            Assert.Null(screen.Error);
            Assert.Equal(new[] { "Who Owns the Code?" }, Articles.Read(root).Select(a => a.Title));
            Assert.Equal(string.Format(Resources.Strings.Art_Removed, 1), screen.Done);
        }

        [Fact]
        public void PicturesAreRecognisedByTheirFirstBytes()
        {
            Assert.Equal("image/jpeg", Articles.ImageType(Jpeg));
            Assert.Equal("image/png", Articles.ImageType(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }));
            Assert.Equal("image/gif", Articles.ImageType("GIF89a"u8.ToArray()));
            Assert.Equal("image/webp", Articles.ImageType("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
            Assert.Equal("image/svg+xml", Articles.ImageType("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()));
        }

        [Fact]
        public async Task TheScreenKeepsTickedArticlesAndListsCopiesWhenTheEreaderIsGone()
        {
            var root = Reader("articles-screen");
            var dir = Path.Combine(Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!, "kept");
            var connected = true;
            var screen = new ArticlesViewModel { FindReader = () => connected ? root : null, Folder = () => dir };

            await screen.LoadAsync();
            Assert.True(screen.ReaderFound);
            Assert.Equal(2, screen.Rows.Count);
            Assert.False(screen.CanKeep);
            screen.Rows[0].IsSelected = true;
            Assert.Equal(1, screen.Ticked);
            Assert.True(screen.CanKeep);

            await screen.KeepTickedCommand.ExecuteAsync(null);
            Assert.True(screen.Rows[0].IsKept);
            Assert.Equal(0, screen.Ticked);
            Assert.Null(screen.Error);

            // Unplugged: the kept copy is still listed, the other article isn't.
            connected = false;
            await screen.LoadAsync();
            Assert.False(screen.ReaderFound);
            Assert.True(screen.ShowNoReader);
            var row = Assert.Single(screen.Rows);
            Assert.False(row.IsOnReader);
            Assert.True(row.IsKept);
            Assert.Equal(Resources.Strings.Art_OnlyHere, row.KeptText);

            screen.Search = "nothing like it";
            Assert.Empty(screen.VisibleRows);
        }
    }
}
