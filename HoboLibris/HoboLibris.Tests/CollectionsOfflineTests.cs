using Microsoft.Data.Sqlite;
using HoboLibris.Services;
using HoboLibris.ViewModels;

namespace HoboLibris.Tests
{
    /// <summary>The Collections screen with no eReader (#32): the snapshot, offline changes, and the merge when it is back.</summary>
    public class CollectionsOfflineTests
    {
        /// <summary>A fake eReader: a folder with ".kobo\KoboReader.sqlite" (and a serial number when given).</summary>
        private sealed class FakeReader
        {
            public string Root { get; }
            public string Db { get; }
            public string StoreDir { get; }
            public bool Connected { get; set; } = true;

            public FakeReader(string name, string? serial = null)
            {
                var dir = TestFolder.Fresh(name);
                Root = Path.Combine(dir, "reader") + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(Path.Combine(Root, ".kobo"));
                Db = KoboDeviceService.DatabasePath(Root);
                StoreDir = Path.Combine(dir, "store");
                Exec(@"CREATE TABLE content (ContentID TEXT, ContentType TEXT, Title TEXT, Attribution TEXT, IsDownloaded BOOL);
                    CREATE TABLE Shelf (CreationDate TEXT, Id TEXT, InternalName TEXT, LastModified TEXT, Name TEXT,
                        Type TEXT, _IsDeleted BOOL, _IsVisible BOOL, _IsSynced BOOL, _SyncTime TEXT, LastAccessed TEXT,
                        PRIMARY KEY (Id));
                    CREATE TABLE ShelfContent (ShelfName TEXT, ContentId TEXT, DateModified TEXT, _IsDeleted BOOL,
                        _IsSynced BOOL, PRIMARY KEY (ShelfName, ContentId));
                    INSERT INTO content VALUES ('file:///mnt/onboard/a.epub', 6, 'Book A', 'Author A', 'true');
                    INSERT INTO content VALUES ('file:///mnt/onboard/b.epub', 6, 'Book B', 'Author B', 'true');
                    INSERT INTO content VALUES ('store-1', 6, 'Store Book', 'Author S', 'true');
                    INSERT INTO Shelf VALUES ('2025-01-01T00:00:00Z', 'id-1', 'Novels', '2025-01-01T00:00:00Z', 'Novels',
                        'UserTag', 'false', 'true', 'true', NULL, NULL);
                    INSERT INTO ShelfContent VALUES ('Novels', 'store-1', '2025-01-01T00:00:00Z', 'false', 'true');");
                SetSerial(serial);
            }

            public void SetSerial(string? serial)
            {
                var version = Path.Combine(Root, ".kobo", "version");
                if (serial == null)
                {
                    File.Delete(version);
                }
                else
                {
                    File.WriteAllText(version, serial + ",4.38.21908,4.38.21908,4.38.21908,4.38.21908,00000000-0000-0000-0000-000000000382");
                }
            }

            /// <summary>A change made on the eReader itself.</summary>
            public void Exec(string sql)
            {
                using (var c = new SqliteConnection($"Data Source={Db}"))
                {
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
                SqliteConnection.ClearAllPools();
                File.SetLastWriteTimeUtc(Db, DateTime.UtcNow.AddSeconds(5));
            }

            /// <summary>The Collections screen, as when HoboLibris starts.</summary>
            public CollectionsViewModel Screen() => new()
            {
                Store = new CollectionsStore(StoreDir),
                FindReader = () => Connected ? Root : null,
                BackupFolder = () => Path.Combine(StoreDir, "..", "backups"),
            };
        }

        [Fact]
        public void TheSnapshotKeepsTheChangesAndTheRenames()
        {
            var reader = new FakeReader("coll-snapshot", "N418000001");
            var c = Collections.Load(reader.Root);
            Assert.Equal("N418000001", c.Serial);
            Assert.Equal("id-1", c.ShelfIds["Novels"]);
            c.Create("Later");
            c.Add("Later", new[] { "file:///mnt/onboard/a.epub" });
            c.Rename("Novels", "Fiction");

            var store = new CollectionsStore(reader.StoreDir);
            Assert.True(store.Save(c));
            var back = store.Load("N418000001")!;
            Assert.True(back.Offline);
            Assert.Equal(c.Books.Count, back.Books.Count);
            Assert.Equal(new[] { "Novels" }, back.Original.Keys);
            Assert.Equal("id-1", back.ShelfIds["Novels"]);
            Assert.Equal(new[] { "Fiction", "Later" }, back.Current.Keys);
            Assert.Equal("Novels", back.Renamed["Fiction"]);
            Assert.Equal(c.Changes().Count, back.Changes().Count);
            Assert.Equal(c.Taken, back.Taken);
            Assert.Throws<HoboLibrisException>(() => { back.Save(reader.StoreDir); });
        }

        [Fact]
        public async Task WithNoEreaderTheLastSnapshotOpensOfflineAndKeepsTheChanges()
        {
            var reader = new FakeReader("coll-offline");
            var screen = reader.Screen();
            await screen.LoadAsync();
            Assert.False(screen.IsOffline);

            // Unplugged: the screen goes on offline, and saving waits.
            reader.Connected = false;
            screen.Unload(null);
            Assert.True(screen.IsOffline);
            screen.Coll!.Create("Later");
            screen.Rebuild();
            Assert.Equal(1, screen.Pending);
            Assert.False(screen.CanSave);
            Assert.StartsWith("Changes waiting for the eReader", screen.PendingText);

            // HoboLibris closed and started again, still with no eReader.
            var again = reader.Screen();
            await again.LoadAsync();
            Assert.True(again.IsLoaded);
            Assert.True(again.IsOffline);
            Assert.Equal(1, again.Pending);
            Assert.Contains("Later", again.CollectionNames);
        }

        [Fact]
        public async Task AtTheNextSyncChangesFromBothSidesAreKeptAndSaved()
        {
            var reader = new FakeReader("coll-merge");
            var screen = reader.Screen();
            await screen.LoadAsync();
            reader.Connected = false;
            screen.Unload(null);
            screen.Coll!.Create("Later");
            screen.Coll.Add("Later", new[] { "file:///mnt/onboard/a.epub" });
            screen.Rebuild();

            // Meanwhile, on the eReader: a new collection.
            reader.Exec(@"INSERT INTO Shelf VALUES ('2025-02-01T00:00:00Z', 'id-2', 'Travel', '2025-02-01T00:00:00Z', 'Travel',
                'UserTag', 'false', 'true', 'true', NULL, NULL);
                INSERT INTO ShelfContent VALUES ('Travel', 'file:///mnt/onboard/b.epub', '2025-02-01T00:00:00Z', 'false', 'true');");
            reader.Connected = true;
            await screen.LoadAsync();

            Assert.False(screen.HasConflicts);
            Assert.False(screen.IsOffline);
            Assert.Equal(new[] { "Later", "Novels", "Travel" }, screen.Coll!.Current.Keys);
            Assert.Equal(2, screen.Pending); // Later created, and a book put in it
            Assert.Equal(Resources.Strings.Coll_Merged, screen.Done);

            await screen.SaveAsync();
            Assert.Null(screen.Error);
            var saved = Collections.LoadDb(reader.Db);
            Assert.Equal(new[] { "Later", "Novels", "Travel" }, saved.Current.Keys);
            Assert.Equal(new[] { "Later" }, saved.ShelvesOf("file:///mnt/onboard/a.epub"));
            Assert.Equal(0, reader.Screen().Store.Load(null)!.Changes().Count);
        }

        [Fact]
        public async Task AConflictIsDecidedBeforeAnythingChanges()
        {
            var reader = new FakeReader("coll-conflict");
            var screen = reader.Screen();
            await screen.LoadAsync();
            reader.Connected = false;
            screen.Unload(null);
            screen.Coll!.Delete("Novels");
            screen.Rebuild();

            // On the eReader, a book was put in Novels.
            reader.Exec("INSERT INTO ShelfContent VALUES ('Novels', 'file:///mnt/onboard/a.epub', '2025-02-01T00:00:00Z', 'false', 'true');");
            reader.Connected = true;
            await screen.LoadAsync();

            Assert.True(screen.HasConflicts);
            Assert.False(screen.IsLoaded);
            Assert.False(screen.AllChosen);
            var row = Assert.Single(screen.ConflictRows);
            row.Choices.Single(c => c.Value == CollectionsMerge.Keep).IsChosen = true;
            Assert.True(screen.AllChosen);
            // The combo box of collections sets this to null when the card empties its list.
            screen.AddTo = null!;
            screen.ApplyChoicesCommand.Execute(null);

            Assert.False(screen.HasConflicts);
            Assert.True(screen.IsLoaded);
            Assert.Equal(new[] { "file:///mnt/onboard/a.epub", "store-1" }, screen.Coll!.Current["Novels"]);
            Assert.Equal(0, screen.Pending);
        }

        [Fact]
        public async Task ChangesForAnotherEreaderAreNotAppliedWithoutAsking()
        {
            var reader = new FakeReader("coll-other", "N418000001");
            var screen = reader.Screen();
            await screen.LoadAsync();
            reader.Connected = false;
            screen.Unload(null);
            screen.Coll!.Create("Later");
            screen.Rebuild();

            // Another eReader is plugged in.
            reader.SetSerial("N418000002");
            reader.Connected = true;
            await screen.LoadAsync();
            Assert.True(screen.HasConflicts);
            Assert.False(screen.CanDiscardMine);
            var row = Assert.Single(screen.ConflictRows);
            Assert.Equal(new[] { CollectionsViewModel.ApplyHere, CollectionsViewModel.KeepForOther, CollectionsViewModel.DiscardThem },
                row.Choices.Select(c => c.Value));

            row.Choices.Single(c => c.Value == CollectionsViewModel.KeepForOther).IsChosen = true;
            screen.ApplyChoicesCommand.Execute(null);
            Assert.False(screen.HasConflicts);
            Assert.Equal(0, screen.Pending);
            Assert.Equal("N418000002", screen.Coll!.Serial);
            // The changes are still waiting for the first eReader.
            Assert.Equal(1, screen.Store.Load("N418000001")!.Changes().Count);
        }

        [Fact]
        public async Task SavingWhenTheBookListChangedMergesInsteadOfRefusing()
        {
            var reader = new FakeReader("coll-stale");
            var screen = reader.Screen();
            await screen.LoadAsync();
            screen.Coll!.Create("Later");
            screen.Rebuild();

            // Something other than the collections changed: the save goes through.
            reader.Exec("UPDATE content SET Title = 'Changed' WHERE ContentID = 'store-1'");
            await screen.SaveAsync();
            Assert.Null(screen.Error);
            Assert.Equal(0, screen.Pending);
            Assert.Contains("Later", Collections.LoadDb(reader.Db).Current.Keys);

            // A collection changed on the eReader: merged, and saved only when the person saves again.
            screen.Coll!.Create("Even later");
            screen.Rebuild();
            reader.Exec(@"INSERT INTO Shelf VALUES ('2025-02-01T00:00:00Z', 'id-3', 'Travel', '2025-02-01T00:00:00Z', 'Travel',
                'UserTag', 'false', 'true', 'true', NULL, NULL);");
            await screen.SaveAsync();
            Assert.Equal(Resources.Strings.Coll_MergedChanged, screen.Done);
            Assert.Equal(1, screen.Pending);
            Assert.DoesNotContain("Even later", Collections.LoadDb(reader.Db).Current.Keys);
            await screen.SaveAsync();
            Assert.Contains("Even later", Collections.LoadDb(reader.Db).Current.Keys);
        }
    }
}
