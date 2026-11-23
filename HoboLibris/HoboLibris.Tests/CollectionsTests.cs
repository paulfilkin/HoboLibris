using Microsoft.Data.Sqlite;
using HoboLibris.Services;

namespace HoboLibris.Tests
{
    public class CollectionsTests
    {
        /// <summary>A book list shaped like a Kobo's, with one collection.</summary>
        private static string Fixture(string name)
        {
            var dir = TestFolder.Fresh(name);
            var db = Path.Combine(dir, "KoboReader.sqlite");
            using (var c = new SqliteConnection($"Data Source={db}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"CREATE TABLE content (ContentID TEXT, ContentType TEXT, Title TEXT, Attribution TEXT, IsDownloaded BOOL);
                    CREATE TABLE Shelf (CreationDate TEXT, Id TEXT, InternalName TEXT, LastModified TEXT, Name TEXT,
                        Type TEXT, _IsDeleted BOOL, _IsVisible BOOL, _IsSynced BOOL, _SyncTime TEXT, LastAccessed TEXT,
                        PRIMARY KEY (Id));
                    CREATE TABLE ShelfContent (ShelfName TEXT, ContentId TEXT, DateModified TEXT, _IsDeleted BOOL,
                        _IsSynced BOOL, PRIMARY KEY (ShelfName, ContentId));
                    INSERT INTO content VALUES ('file:///mnt/onboard/a.epub', 6, 'Book A', 'Author A', 'true');
                    INSERT INTO content VALUES ('file:///mnt/onboard/b.epub', 6, 'Book B', 'Author B', 'true');
                    INSERT INTO content VALUES ('store-1', 6, 'Store Book', 'Author S', 'true');
                    INSERT INTO content VALUES ('store-2', 6, 'Not Downloaded', 'Author N', 'false');
                    INSERT INTO Shelf VALUES ('2025-01-01T00:00:00Z', 'id-1', 'Novels', '2025-01-01T00:00:00Z', 'Novels',
                        'UserTag', 'false', 'true', 'true', NULL, NULL);
                    INSERT INTO ShelfContent VALUES ('Novels', 'store-1', '2025-01-01T00:00:00Z', 'false', 'true');";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            return db;
        }

        private static string Backups(string db) => Path.Combine(Path.GetDirectoryName(db)!, "backups");

        private static string Scalar(string db, string sql)
        {
            using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            var result = cmd.ExecuteScalar()?.ToString() ?? "";
            SqliteConnection.ClearAllPools();
            return result;
        }

        [Fact]
        public void ReadsCollectionsAndWritesChangesBack()
        {
            var db = Fixture("collections-test");
            var c = Collections.LoadDb(db);
            Assert.Equal(3, c.Books.Count); // the store book that isn't downloaded is left out
            Assert.Equal(new[] { "Novels" }, c.ShelvesOf("store-1"));
            Assert.False(c.IsShelved("file:///mnt/onboard/a.epub"));

            c.Create("Languages");
            c.Add("Languages", new[] { "file:///mnt/onboard/a.epub" });
            c.Remove("Novels", new[] { "store-1" });
            c.Add("Novels", new[] { "file:///mnt/onboard/b.epub" });
            Assert.Equal(4, c.Changes().Count);

            var backup = c.Save(Backups(db));
            Assert.True(File.Exists(backup));
            Assert.Equal(0, c.Changes().Count);

            // Read back from the file: the changes are there.
            var again = Collections.LoadDb(db);
            Assert.Equal(new[] { "Languages" }, again.ShelvesOf("file:///mnt/onboard/a.epub"));
            Assert.Equal(new[] { "Novels" }, again.ShelvesOf("file:///mnt/onboard/b.epub"));
            Assert.False(again.IsShelved("store-1"));

            // The new collection got the existing type and is marked for syncing.
            Assert.Equal("UserTag", Scalar(db, "SELECT Type FROM Shelf WHERE Name = 'Languages'"));
            Assert.Equal("false", Scalar(db, "SELECT _IsSynced FROM Shelf WHERE Name = 'Languages'"));
            Assert.Equal("true", Scalar(db, "SELECT _IsDeleted FROM ShelfContent WHERE ShelfName = 'Novels' AND ContentId = 'store-1'"));
        }

        [Fact]
        public void RenamesAndMovesAndSaves()
        {
            var db = Fixture("collections-rename-test");
            var c = Collections.LoadDb(db);
            c.Create("Languages");
            c.Add("Languages", new[] { "store-1" });
            Assert.Equal(2, c.ShelfCount("store-1"));

            // Move: only in the target afterwards.
            c.MoveTo("Languages", new[] { "store-1", "file:///mnt/onboard/a.epub" });
            Assert.Equal(new[] { "Languages" }, c.ShelvesOf("store-1"));
            Assert.Equal(new[] { "Languages" }, c.ShelvesOf("file:///mnt/onboard/a.epub"));

            // Rename keeps the books; a clash is refused.
            Assert.Throws<HoboLibrisException>(() => c.Rename("Novels", "languages"));
            c.Rename("Languages", "Spanish & German");
            Assert.Equal(new[] { "Spanish & German" }, c.ShelvesOf("store-1"));

            c.Save(Backups(db));
            var again = Collections.LoadDb(db);
            Assert.Equal(new[] { "Novels", "Spanish & German" }, again.Current.Keys.ToArray());
            Assert.Equal(new[] { "Spanish & German" }, again.ShelvesOf("store-1"));
            Assert.Equal(new[] { "Spanish & German" }, again.ShelvesOf("file:///mnt/onboard/a.epub"));
            Assert.Empty(again.Current["Novels"]);
        }

        [Fact]
        public void DeletingACollectionMarksItAndItsBooks()
        {
            var db = Fixture("collections-delete-test");
            var c = Collections.LoadDb(db);
            c.Delete("Novels");
            c.Save(Backups(db));
            var again = Collections.LoadDb(db);
            Assert.Empty(again.Current);
            Assert.False(again.IsShelved("store-1"));
        }

        [Fact]
        public void RefusesToSaveWhenTheEreaderChangedIt()
        {
            var db = Fixture("collections-stale-test");
            var c = Collections.LoadDb(db);
            c.Create("Later");
            Thread.Sleep(1100);
            using (var conn = new SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE content SET Title = 'Changed' WHERE ContentID = 'store-1'";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            File.SetLastWriteTimeUtc(db, DateTime.UtcNow);
            Assert.Throws<HoboLibrisException>(() => { c.Save(Backups(db)); });
        }

        [Fact]
        public void TimestampsAreKoboStyle()
        {
            var t = DateTime.UnixEpoch.AddSeconds(1_763_733_909);
            Assert.Equal("2025-11-21T14:05:09Z", Collections.KoboTimestamp(t));
        }
    }
}

