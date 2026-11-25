using Microsoft.Data.Sqlite;
using HoboLibris.Services;
using HoboLibris.ViewModels;

namespace HoboLibris.Tests
{
    /// <summary>The Previews screen (#59): store previews on the eReader, and the wishlist in the Kobo account.</summary>
    public class PreviewsTests
    {
        /// <summary>
        /// A fake eReader shaped as found with firmware 4.46: two previews with their chapters and files, a
        /// preview the eReader no longer holds, an owned store book, and a Wishlist table with one synced
        /// add and one removal waiting for the sync.
        /// </summary>
        private static string Reader(string name)
        {
            var root = Path.Combine(TestFolder.Fresh(name), "reader") + Path.DirectorySeparatorChar;
            var kepub = Path.Combine(root, ".kobo", "kepub");
            Directory.CreateDirectory(kepub);
            var db = KoboDeviceService.DatabasePath(root);
            using (var c = new SqliteConnection($"Data Source={db}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"CREATE TABLE content (ContentID TEXT, ContentType TEXT, BookID TEXT, Title TEXT, Attribution TEXT,
                        ISBN TEXT, CrossRevisionId TEXT, ___SyncTime TEXT, PreviewFileSize INTEGER, IsDownloaded BOOL, Accessibility INTEGER);
                    CREATE TABLE volume_shortcovers (volumeId TEXT, shortcoverId TEXT, VolumeIndex INTEGER);
                    CREATE TABLE volume_tabs (volumeId TEXT NOT NULL, tabId TEXT NOT NULL, PRIMARY KEY (volumeId, tabId));
                    INSERT INTO content VALUES ('p-1', 6, NULL, 'Crusaders', 'Dan Jones', '9780000000001', 'cr-1', '2026-11-25T12:41:28Z', 5180881, 'true', 6);
                    INSERT INTO content VALUES ('p-1!ch1', 9, 'p-1', NULL, NULL, NULL, NULL, NULL, NULL, 'true', 6);
                    INSERT INTO content VALUES ('p-1!ch2', 9, 'p-1', NULL, NULL, NULL, NULL, NULL, NULL, 'true', 6);
                    INSERT INTO content VALUES ('p-2', 6, NULL, 'Dynasty', 'Tom Holland', '', 'cr-2', '2026-11-20T09:00:00Z', 2785260, 'true', 6);
                    INSERT INTO content VALUES ('p-2!ch1', 9, 'p-2', NULL, NULL, NULL, NULL, NULL, NULL, 'true', 6);
                    INSERT INTO content VALUES ('p-3', 6, NULL, 'Read and gone', 'Someone', '9780000000003', 'cr-3', '2026-11-24T20:29:44Z', 1135909, 'false', 6);
                    INSERT INTO content VALUES ('owned-1', 6, NULL, 'Owned', 'Author', '9780000000004', 'cr-4', '2026-01-01T00:00:00Z', NULL, 'true', 1);
                    INSERT INTO volume_shortcovers VALUES ('p-1', 'p-1!ch1', 0);
                    INSERT INTO volume_shortcovers VALUES ('p-1', 'p-1!ch2', 1);
                    INSERT INTO volume_shortcovers VALUES ('p-2', 'p-2!ch1', 0);
                    INSERT INTO volume_tabs VALUES ('p-1', 'tab-a');
                    INSERT INTO volume_tabs VALUES ('p-2', 'tab-a');
                    INSERT INTO volume_tabs VALUES ('other', 'p-1');
                    ALTER TABLE content ADD COLUMN WishlistedDate TEXT DEFAULT '0000-00-00T00:00:00.000';
                    CREATE TABLE Wishlist (CrossRevisionId TEXT, DateModified TEXT NOT NULL, IsAdded BOOL, IsSynced BOOL, PRIMARY KEY (CrossRevisionId));
                    INSERT INTO Wishlist VALUES ('cr-9', '2026-11-24T20:37:51Z', 'true', 'true');
                    INSERT INTO Wishlist VALUES ('cr-7', '2026-11-25T17:12:07Z', 'false', 'false');";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            foreach (var id in new[] { "p-1", "p-2", "owned-1" })
            {
                File.WriteAllText(Path.Combine(kepub, id), "x");
            }
            return root;
        }

        private static long Count(string root, string sql)
        {
            using var c = KoboDeviceService.OpenReadOnly(KoboDeviceService.DatabasePath(root));
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return (long)cmd.ExecuteScalar()!;
        }

        [Fact]
        public void PreviewsOnTheEreaderAreListedNewestFirst()
        {
            var root = Reader("previews-read");
            var found = Previews.Read(root);

            // The preview the eReader no longer holds and the owned book aren't listed.
            Assert.Equal(new[] { "Crusaders", "Dynasty" }, found.Select(p => p.Title));
            var first = found[0];
            Assert.Equal("Dan Jones", first.Author);
            Assert.Equal(5180881, first.Size);
            Assert.Equal(new DateTime(2026, 11, 25, 12, 41, 28, DateTimeKind.Utc), first.Arrived);
            Assert.Equal("https://www.kobo.com/search?query=9780000000001", first.StoreUrl);
            // With no ISBN, the store is searched for the title and author.
            Assert.Equal("https://www.kobo.com/search?query=Dynasty%20Tom%20Holland", found[1].StoreUrl);
        }

        [Fact]
        public async Task WithNoEreaderTheScreenSaysHowToConnect()
        {
            var screen = new PreviewsViewModel { FindReader = () => null };
            await screen.LoadAsync();
            Assert.True(screen.ShowNoReader);
            Assert.Empty(screen.Rows);
            Assert.False(screen.CanAddWish);
        }

        /// <summary>A wishlist reply shaped as the store sent one on 25 November 2026, with made-up books.</summary>
        private const string WishlistReply = @"{
            ""TotalCountByProductType"": { ""book"": 2 },
            ""Items"": [
                { ""DateAdded"": ""2026-11-24T20:37:51.0000000Z"", ""CrossRevisionId"": ""cr-9"", ""IsPurchaseable"": true,
                  ""ProductMetadata"": { ""Book"": {
                      ""Contributors"": ""A. Writer"", ""ISBN"": ""9780000000009"", ""CrossRevisionId"": ""cr-9"", ""Title"": ""A Future Book"",
                      ""ContributorRoles"": [ { ""Name"": ""A. Writer"", ""Role"": ""Author"" }, { ""Name"": ""B. Translator"", ""Role"": ""Translator"" } ],
                      ""Slug"": ""a-future-book-2"", ""HasPreview"": true, ""Price"": { ""Currency"": ""EUR"", ""Price"": 17.43 },
                      ""IsPreOrder"": true, ""Id"": ""book-9"" } } },
                { ""DateAdded"": ""2026-10-01T08:00:00.0000000Z"", ""CrossRevisionId"": ""cr-8"",
                  ""ProductMetadata"": { ""Book"": { ""Contributors"": ""C. Author"", ""Title"": ""Out Now"", ""HasPreview"": false,
                      ""IsPreOrder"": false, ""Id"": ""book-8"" } } }
            ],
            ""ItemCount"": 2, ""TotalPageCount"": 1, ""TotalItemCount"": 2, ""CurrentPageIndex"": 0, ""ItemsPerPage"": 100, ""VersionCode"": 2
        }";

        [Fact]
        public void TheWishlistReplyIsRead()
        {
            var (items, page, pages) = Wishlist.Parse(WishlistReply);
            Assert.Equal(0, page);
            Assert.Equal(1, pages);
            Assert.Equal(2, items.Count);

            var first = items[0];
            Assert.Equal("cr-9", first.CrossRevisionId);
            Assert.Equal("A Future Book", first.Title);
            Assert.Equal("A. Writer", first.Author);
            Assert.Equal(17.43m, first.Price);
            Assert.Equal("EUR", first.Currency);
            Assert.True(first.PreOrder);
            Assert.True(first.HasPreview);
            Assert.Equal(new DateTime(2026, 11, 24, 20, 37, 51, DateTimeKind.Utc), first.Added);
            Assert.Equal("https://www.kobo.com/ebook/a-future-book-2", first.StoreUrl);

            // No slug or ISBN: the store is searched for the title and author.  No price: none shown.
            Assert.Equal("C. Author", items[1].Author);
            Assert.Null(items[1].Price);
            Assert.Equal("", items[1].PriceText);
            Assert.Equal("https://www.kobo.com/search?query=Out%20Now%20C.%20Author", items[1].StoreUrl);
        }

        [Fact]
        public void AWishlistReplyThatIsNotJsonSaysSo()
        {
            var ex = Assert.Throws<HoboLibrisException>(() => Wishlist.Parse("<html>"));
            Assert.StartsWith(Resources.Strings.Wish_UnexpectedReply.Split('{')[0], ex.Message);
        }

        [Fact]
        public async Task TheScreenListsTheWishlistNewestFirstAndOpensStorePages()
        {
            var opened = new List<string>();
            var screen = new PreviewsViewModel
            {
                FindReader = () => null,
                FetchWishlist = _ => Task.FromResult(Wishlist.Parse(WishlistReply).Items.AsEnumerable().Reverse().ToList()),
                OpenUrl = opened.Add,
            };
            await screen.LoadAsync();
            Assert.False(screen.WishSignedOut);
            Assert.Null(screen.WishError);
            Assert.Equal(new[] { "A Future Book", "Out Now" }, screen.WishRows.Select(r => r.Title));
            Assert.Equal(string.Format(Resources.Strings.Wish_PreOrderPrice, screen.WishRows[0].Item.PriceText), screen.WishRows[0].PriceText);

            screen.OpenWishPageCommand.Execute(screen.WishRows[0]);
            Assert.Equal(new[] { "https://www.kobo.com/ebook/a-future-book-2" }, opened);
        }

        /// <summary>
        /// Wishlist changes are left on the eReader for its sync, as tested on 25 November 2026: a row per
        /// book marked IsAdded true or false and IsSynced false, after a backup.  An added book's content
        /// row gets a WishlistedDate, as the eReader's own add left it.  Only the waiting rows are read back.
        /// </summary>
        [Fact]
        public void WishlistChangesAreLeftForTheEreadersSync()
        {
            var root = Reader("wishlist-change");
            var db = KoboDeviceService.DatabasePath(root);
            Assert.Equal(new Dictionary<string, bool> { ["cr-7"] = false }, Wishlist.Waiting(db));

            var backup = Wishlist.Change(root, new[] { ("cr-1", true), ("cr-9", false) }, Path.Combine(root, "..", "backups"));
            Assert.True(File.Exists(backup));
            var waiting = Wishlist.Waiting(db);
            Assert.Equal(3, waiting.Count);
            Assert.True(waiting["cr-1"]);
            Assert.False(waiting["cr-9"]);
            Assert.Equal(3, Count(root, "SELECT COUNT(*) FROM Wishlist WHERE IsSynced = 'false'"));
            Assert.Equal(1, Count(root, "SELECT COUNT(*) FROM content WHERE CrossRevisionId = 'cr-1' AND WishlistedDate LIKE '20%'"));
            Assert.Equal(0, Count(root, "SELECT COUNT(*) FROM content WHERE CrossRevisionId <> 'cr-1' AND WishlistedDate LIKE '20%'"));
        }

        [Fact]
        public async Task TheScreenChangesTheWishlistThroughTheEreader()
        {
            var root = Reader("wishlist-screen");
            var items = Wishlist.Parse(WishlistReply).Items;
            // Dynasty (cr-2) is on the wishlist in the account too.
            items.Add(items[1] with { CrossRevisionId = "cr-2", Title = "Dynasty" });
            var screen = new PreviewsViewModel
            {
                FindReader = () => root,
                BackupFolder = () => Path.Combine(root, "..", "backups"),
                FetchWishlist = _ => Task.FromResult(items.ToList()),
            };
            await screen.LoadAsync();
            var crusaders = screen.Rows.Single(r => r.Title == "Crusaders");
            var dynasty = screen.Rows.Single(r => r.Title == "Dynasty");
            Assert.True(dynasty.OnWishlist);
            Assert.False(dynasty.CanWish);
            Assert.True(crusaders.CanWish);

            // Ticking both offers only the one not on the wishlist.
            crusaders.IsSelected = true;
            dynasty.IsSelected = true;
            Assert.Equal(1, screen.TickedForWish);
            await screen.AddTickedToWishlistCommand.ExecuteAsync(null);
            Assert.Null(screen.Error);
            Assert.True(crusaders.Adding);
            Assert.Equal(Resources.Strings.Prev_Adding, crusaders.WishText);
            Assert.Equal(string.Format(Resources.Strings.Prev_Added, 1), screen.Done);

            // Removing asks first; a no changes nothing.
            var future = screen.WishRows.Single(r => r.Title == "A Future Book");
            future.IsSelected = true;
            screen.ConfirmWishRemove = _ => false;
            await screen.RemoveTickedFromWishlistCommand.ExecuteAsync(null);
            Assert.False(Wishlist.Waiting(KoboDeviceService.DatabasePath(root)).ContainsKey("cr-9"));

            screen.ConfirmWishRemove = _ => true;
            await screen.RemoveTickedFromWishlistCommand.ExecuteAsync(null);
            Assert.Null(screen.WishError);
            future = screen.WishRows.Single(r => r.Title == "A Future Book");
            Assert.True(future.Removing);
            Assert.False(future.CanTick);
            Assert.Equal(string.Format(Resources.Strings.Wish_Removed, 1), screen.WishDone);
            Assert.False(Wishlist.Waiting(KoboDeviceService.DatabasePath(root))["cr-9"]);
        }

        [Fact]
        public async Task WithNoEreaderTheWishlistCannotBeChanged()
        {
            var screen = new PreviewsViewModel
            {
                FindReader = () => null,
                FetchWishlist = _ => Task.FromResult(Wishlist.Parse(WishlistReply).Items),
            };
            await screen.LoadAsync();
            Assert.True(screen.ShowWishNeedsReader);
            screen.WishRows[0].IsSelected = true;
            Assert.False(screen.CanRemoveWish);
            Assert.Empty(await screen.RemoveFromWishlistAsync(screen.WishRows.ToList()));
            Assert.Equal(Resources.Strings.Wish_NeedsReader, screen.WishError);
        }

        [Fact]
        public async Task AWishlistThatCannotBeReadSaysWhy()
        {
            var screen = new PreviewsViewModel
            {
                FindReader = () => null,
                FetchWishlist = _ => Task.FromException<List<WishlistItem>>(new HoboLibrisException("no network")),
            };
            await screen.LoadAsync();
            Assert.Empty(screen.WishRows);
            Assert.Equal(string.Format(Resources.Strings.Wish_CouldNotRead, "no network"), screen.WishError);
        }
    }
}
