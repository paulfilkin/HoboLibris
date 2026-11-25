using System.IO;
using Microsoft.Data.Sqlite;
using HoboLibris.Models;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    /// <summary>
    /// Kobo eReader collections: read from and written back to the eReader's book list
    /// (".kobo\KoboReader.sqlite", tables Shelf and ShelfContent, as Kobo and calibre use them).
    ///
    /// Changes are kept in memory until Save. Saving first copies the book list to this computer,
    /// refuses if the eReader changed it since it was read, and writes everything in one
    /// transaction. Rows are marked deleted (_IsDeleted = 'true') rather than removed, and changed
    /// rows get _IsSynced = 'false', so a Kobo that syncs with the Kobo account sends the change
    /// on as if it had been made on the eReader.
    ///
    /// The collections can also be opened offline, from a snapshot kept on this computer at the last
    /// read or save (#32): they are changed as usual, but saving waits until the eReader is plugged
    /// in again, when the changes are merged with any made on the eReader (CollectionsMerge).
    /// </summary>
    public class Collections
    {
        public string DatabasePath { get; }
        private DateTime? _loadedModified;
        public List<CollectionBook> Books { get; }
        /// <summary>Collection name -> book ids, as on the eReader.</summary>
        private SortedDictionary<string, SortedSet<string>> _original;
        /// <summary>Collection name -> book ids, with the person's changes.</summary>
        public SortedDictionary<string, SortedSet<string>> Current { get; private set; }

        /// <summary>Collection name -> book ids, as on the eReader when it was read (the base of a merge).</summary>
        public IReadOnlyDictionary<string, SortedSet<string>> Original => _original;

        /// <summary>The eReader's own id for each collection as read (Shelf.Id), by name.</summary>
        public Dictionary<string, string> ShelfIds { get; private set; }

        /// <summary>Every book id in the eReader's book list as read, downloaded or not; empty offline.</summary>
        public HashSet<string> KnownIds { get; }

        /// <summary>Collections renamed here: current name -> its name as read.</summary>
        public Dictionary<string, string> Renamed { get; private set; } = new(NameComparer);

        /// <summary>The eReader's serial number, from ".kobo\version"; null when it couldn't be read.</summary>
        public string? Serial { get; }

        /// <summary>When the collections were read from the eReader (UTC).</summary>
        public DateTime Taken { get; private set; }

        /// <summary>Opened from the snapshot on this computer: the eReader isn't connected, and saving waits.</summary>
        public bool Offline { get; private set; }

        public static readonly StringComparer NameComparer = StringComparer.CurrentCultureIgnoreCase;

        private Collections(string db, DateTime? modified, List<CollectionBook> books, SortedDictionary<string, SortedSet<string>> current,
            Dictionary<string, string>? shelfIds = null, HashSet<string>? known = null, string? serial = null, DateTime? taken = null)
        {
            DatabasePath = db;
            _loadedModified = modified;
            Books = books;
            _original = Copy(current);
            Current = current;
            ShelfIds = shelfIds ?? new Dictionary<string, string>(NameComparer);
            KnownIds = known ?? new HashSet<string>(StringComparer.Ordinal);
            Serial = serial;
            Taken = taken ?? DateTime.UtcNow;
        }

        /// <summary>
        /// Collections from their parts: a snapshot read back, or a test.  The books in each
        /// collection are its state as read, with no changes yet.
        /// </summary>
        public static Collections FromParts(string db, string? serial, DateTime taken, List<CollectionBook> books,
            IEnumerable<(string Name, string? Id, IEnumerable<string> Books)> shelves, IEnumerable<string>? known = null, bool offline = false)
        {
            var current = new SortedDictionary<string, SortedSet<string>>(NameComparer);
            var ids = new Dictionary<string, string>(NameComparer);
            foreach (var (name, id, members) in shelves)
            {
                current[name] = new SortedSet<string>(members, StringComparer.Ordinal);
                if (id != null)
                {
                    ids[name] = id;
                }
            }
            return new Collections(db, null, books, current, ids, known?.ToHashSet(StringComparer.Ordinal), serial, taken) { Offline = offline };
        }

        /// <summary>
        /// Put the person's changes back on collections read from their parts: the collections as
        /// they are now, and which were renamed.
        /// </summary>
        public void Restore(IEnumerable<(string Name, IEnumerable<string> Books)> current, IReadOnlyDictionary<string, string>? renamed)
        {
            Current = new SortedDictionary<string, SortedSet<string>>(NameComparer);
            foreach (var (name, members) in current)
            {
                Current[name] = new SortedSet<string>(members, StringComparer.Ordinal);
            }
            Renamed = new Dictionary<string, string>(NameComparer);
            foreach (var (now, before) in renamed ?? new Dictionary<string, string>())
            {
                if (Current.ContainsKey(now))
                {
                    Renamed[now] = before;
                }
            }
        }

        /// <summary>Replace the collections with a merged result (CollectionsMerge), against the eReader as read.</summary>
        public void Apply(SortedDictionary<string, SortedSet<string>> merged, Dictionary<string, string> renamed)
        {
            Current = merged;
            Renamed = renamed;
        }

        /// <summary>The eReader is no longer connected: keep the collections and changes, but saving waits.</summary>
        public void GoOffline() => Offline = true;

        /// <summary>True when the eReader's book list was written since it was read.</summary>
        public bool ChangedOnDevice() => !Offline && Modified(DatabasePath) != _loadedModified;

        private static SortedDictionary<string, SortedSet<string>> Copy(SortedDictionary<string, SortedSet<string>> source)
        {
            var copy = new SortedDictionary<string, SortedSet<string>>(NameComparer);
            foreach (var (name, ids) in source)
            {
                copy[name] = new SortedSet<string>(ids, StringComparer.Ordinal);
            }
            return copy;
        }

        private static DateTime? Modified(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
            }
            catch (Exception ex)
            {
                LogManager.Debug($"No modified time for {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>UTC time as Kobo writes it: 2026-11-21T14:05:09Z.</summary>
        public static string KoboTimestamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        private static string NotDeleted(string col) => $"IFNULL({col}, 'false') NOT IN ('true', '1')";

        /// <summary>
        /// Articles saved with Instapaper (MimeType "application/x-kobo-html+instapaper"; Pocket's
        /// earlier articles are taken to be of the same family).  They are content rows like books,
        /// but the eReader doesn't show them in collections, even when ShelfContent puts them there (#28).
        /// </summary>
        private const string ArticleMimeTypes = "application/x-kobo-html%";

        /// <summary>The ids of the articles in the book list; none when it has no MimeType column.</summary>
        private static HashSet<string> ReadArticleIds(SqliteConnection conn)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT ContentID FROM content WHERE ContentType = 6 AND MimeType LIKE '{ArticleMimeTypes}'";
                using var rows = cmd.ExecuteReader();
                while (rows.Read())
                {
                    ids.Add(rows.GetString(0));
                }
            }
            catch (SqliteException ex)
            {
                LogManager.Debug($"No MimeType column, so no articles to leave out: {ex.Message}");
            }
            return ids;
        }

        /// <summary>Read the books and collections from a Kobo eReader whose drive root is root.</summary>
        public static Collections Load(string root)
        {
            var db = KoboDeviceService.DatabasePath(root);
            if (!File.Exists(db))
            {
                throw new HoboLibrisException(Strings.Coll_BookListNotFound);
            }
            return LoadDb(db, KoboDeviceService.Serial(root));
        }

        /// <summary>Collection name -> Shelf.Id, for the collections not deleted (none when there is no Id column).</summary>
        private static Dictionary<string, string> ReadShelfIds(SqliteConnection conn, SqliteTransaction? tx = null)
        {
            var ids = new Dictionary<string, string>(NameComparer);
            using (var cols = conn.CreateCommand())
            {
                cols.Transaction = tx;
                cols.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Shelf') WHERE name = 'Id'";
                if (Convert.ToInt64(cols.ExecuteScalar()) == 0)
                {
                    return ids;
                }
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT Name, Id FROM Shelf WHERE Name IS NOT NULL AND Id IS NOT NULL AND {NotDeleted("_IsDeleted")}";
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                ids.TryAdd(rows.GetString(0), rows.GetValue(1).ToString() ?? "");
            }
            return ids;
        }

        public static Collections LoadDb(string db, string? serial = null)
        {
            var loadedModified = Modified(db);
            SqliteConnection conn;
            try
            {
                conn = KoboDeviceService.OpenReadOnly(db);
            }
            catch (Exception ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotOpen, ex.Message), ex);
            }
            using (conn)
            {
                try
                {
                    var current = new SortedDictionary<string, SortedSet<string>>(NameComparer);
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT Name FROM Shelf WHERE Name IS NOT NULL AND {NotDeleted("_IsDeleted")}";
                        using var rows = cmd.ExecuteReader();
                        while (rows.Read())
                        {
                            var name = rows.GetString(0);
                            if (!current.ContainsKey(name))
                            {
                                current[name] = new SortedSet<string>(StringComparer.Ordinal);
                            }
                        }
                    }
                    // Articles are left out of the books and of the collections' books, so the counts
                    // and "Not in any collection" are about books; their rows on the eReader stay as they are.
                    var articles = ReadArticleIds(conn);
                    var shelved = new HashSet<string>(StringComparer.Ordinal);
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT ShelfName, ContentId FROM ShelfContent WHERE ShelfName IS NOT NULL AND {NotDeleted("_IsDeleted")}";
                        using var rows = cmd.ExecuteReader();
                        while (rows.Read())
                        {
                            if (rows.IsDBNull(1))
                            {
                                continue;
                            }
                            var shelf = rows.GetString(0);
                            var id = rows.GetString(1);
                            // Only collections that still exist.
                            if (current.TryGetValue(shelf, out var set) && !articles.Contains(id))
                            {
                                shelved.Add(id);
                                set.Add(id);
                            }
                        }
                    }

                    // Books: everything added by the person, store books that are downloaded, and
                    // any book already in a collection.
                    var books = new List<CollectionBook>();
                    var known = new HashSet<string>(StringComparer.Ordinal);
                    const string withDownload = "SELECT ContentID, IFNULL(Title, ''), IFNULL(Attribution, ''), IFNULL(IsDownloaded, 'true') FROM content WHERE ContentType = 6";
                    const string plain = "SELECT ContentID, IFNULL(Title, ''), IFNULL(Attribution, ''), 'true' FROM content WHERE ContentType = 6";
                    foreach (var sql in new[] { withDownload, plain })
                    {
                        try
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = sql;
                            using var rows = cmd.ExecuteReader();
                            while (rows.Read())
                            {
                                var id = rows.GetString(0);
                                known.Add(id);
                                if (articles.Contains(id))
                                {
                                    continue;
                                }
                                var title = rows.GetString(1);
                                var authors = rows.GetString(2);
                                var downloadedText = rows.GetValue(3)?.ToString() ?? "true";
                                var store = !id.StartsWith("file://", StringComparison.Ordinal);
                                var downloaded = downloadedText is "true" or "1";
                                if (store && !downloaded && !shelved.Contains(id))
                                {
                                    continue;
                                }
                                if (title.Trim().Length == 0)
                                {
                                    var slash = id.LastIndexOf('/');
                                    title = slash < 0 ? id : id[(slash + 1)..];
                                }
                                else
                                {
                                    title = title.Trim();
                                }
                                books.Add(new CollectionBook { Id = id, Title = title, Authors = authors.Trim(), Store = store });
                            }
                            break;
                        }
                        catch (SqliteException ex) when (sql == withDownload)
                        {
                            LogManager.Debug($"No IsDownloaded column, reading without it: {ex.Message}");
                        }
                    }
                    books.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
                    return new Collections(db, loadedModified, books, current, ReadShelfIds(conn), known, serial, DateTime.UtcNow);
                }
                catch (SqliteException ex)
                {
                    throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotRead, ex.Message), ex);
                }
            }
        }

        public IEnumerable<string> ShelvesOf(string id) =>
            Current.Where(kv => kv.Value.Contains(id)).Select(kv => kv.Key);

        public bool IsShelved(string id) => Current.Values.Any(ids => ids.Contains(id));

        /// <summary>How many collections a book is in.</summary>
        public int ShelfCount(string id) => Current.Values.Count(ids => ids.Contains(id));

        public void Create(string name)
        {
            name = name.Trim();
            if (name.Length == 0)
            {
                throw new HoboLibrisException(Strings.Coll_TypeNameFirst);
            }
            if (Current.ContainsKey(name))
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_AlreadyExists, name));
            }
            Current[name] = new SortedSet<string>(StringComparer.Ordinal);
        }

        public void Delete(string name)
        {
            Current.Remove(name);
            Renamed.Remove(name);
        }

        /// <summary>
        /// Rename a collection: its books go to a collection with the new name and the old one is
        /// deleted, which Kobo handles the same way as when done on the eReader.
        /// </summary>
        public void Rename(string oldName, string newName)
        {
            newName = newName.Trim();
            if (newName.Length == 0)
            {
                throw new HoboLibrisException(Strings.Coll_TypeNewNameFirst);
            }
            if (newName == oldName)
            {
                return;
            }
            if (Current.Keys.Any(n => n != oldName && NameComparer.Equals(n, newName)))
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_AlreadyExists, newName));
            }
            if (!Current.Remove(oldName, out var books))
            {
                throw new HoboLibrisException(Strings.Coll_NoLongerExists);
            }
            Current[newName] = books;

            // Remember the name it had on the eReader, so a merge knows it is the same collection.
            var before = Renamed.Remove(oldName, out var was) ? was : _original.ContainsKey(oldName) ? oldName : null;
            if (before != null)
            {
                Renamed[newName] = before;
            }
        }

        /// <summary>Put books in shelf and take them out of every other collection.</summary>
        public void MoveTo(string shelf, IReadOnlyCollection<string> ids)
        {
            if (!Current.ContainsKey(shelf))
            {
                return;
            }
            foreach (var (name, set) in Current)
            {
                if (NameComparer.Equals(name, shelf))
                {
                    set.UnionWith(ids);
                }
                else
                {
                    set.ExceptWith(ids);
                }
            }
        }

        public void Add(string shelf, IEnumerable<string> ids)
        {
            if (Current.TryGetValue(shelf, out var set))
            {
                set.UnionWith(ids);
            }
        }

        public void Remove(string shelf, IEnumerable<string> ids)
        {
            if (Current.TryGetValue(shelf, out var set))
            {
                set.ExceptWith(ids);
            }
        }

        public void Discard()
        {
            Current = Copy(_original);
            Renamed = new Dictionary<string, string>(NameComparer);
        }

        /// <summary>What Save would write.</summary>
        public CollectionChanges Changes()
        {
            var c = new CollectionChanges();
            foreach (var (name, ids) in Current)
            {
                if (!_original.TryGetValue(name, out var before))
                {
                    c.Created.Add(name);
                    c.Added.AddRange(ids.Select(id => (name, id)));
                }
                else
                {
                    c.Added.AddRange(ids.Except(before).Select(id => (name, id)));
                    c.Removed.AddRange(before.Except(ids).Select(id => (name, id)));
                }
            }
            foreach (var name in _original.Keys)
            {
                if (!Current.ContainsKey(name))
                {
                    c.Deleted.Add(name);
                }
            }
            return c;
        }

        /// <summary>Write the changes to the eReader. Returns where the book list was backed up first.</summary>
        public string Save(string backupDir)
        {
            var changes = Changes();
            if (changes.Count == 0)
            {
                throw new HoboLibrisException(Strings.Coll_NoChanges);
            }
            if (Offline || !File.Exists(DatabasePath))
            {
                throw new HoboLibrisException(Strings.Coll_ReaderGone);
            }
            if (Modified(DatabasePath) != _loadedModified)
            {
                throw new HoboLibrisException(Strings.Coll_ListChanged);
            }

            // Keep a copy of the book list (and its journal files, if any) on this computer first.
            try
            {
                Directory.CreateDirectory(backupDir);
            }
            catch (Exception ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotCreateBackupFolder, ex.Message), ex);
            }
            var stamp = KoboTimestamp(DateTime.UtcNow).Replace(":", "").Replace("-", "");
            var backup = Path.Combine(backupDir, $"KoboReader-{stamp}.sqlite");
            try
            {
                File.Copy(DatabasePath, backup, overwrite: true);
            }
            catch (Exception ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotBackUp, ex.Message), ex);
            }
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var side = DatabasePath + suffix;
                if (File.Exists(side))
                {
                    try
                    {
                        File.Copy(side, backup + suffix, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warn($"Could not back up {side}: {ex.Message}");
                    }
                }
            }

            try
            {
                // Not pooled, so the file is closed even when writing fails (the eReader can then be ejected).
                using var conn = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWrite;Pooling=False");
                conn.Open();
                WriteChanges(conn, changes, KoboTimestamp(DateTime.UtcNow));
                // New collections got their own ids: read them for the next merge.
                ShelfIds = ReadShelfIds(conn);
            }
            catch (SqliteException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotSave, ex.Message), ex);
            }
            catch (IOException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotOpenForWriting, ex.Message), ex);
            }
            SqliteConnection.ClearAllPools();

            _original = Copy(Current);
            Renamed = new Dictionary<string, string>(NameComparer);
            _loadedModified = Modified(DatabasePath);
            Taken = DateTime.UtcNow;
            return backup;
        }

        private static List<string> ShelfColumns(SqliteConnection conn)
        {
            var cols = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(Shelf)";
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                cols.Add(rows.GetString(1));
            }
            return cols;
        }

        private static int Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, string Value)[] args)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
            {
                cmd.Parameters.AddWithValue(name, value);
            }
            return cmd.ExecuteNonQuery();
        }

        private static void WriteChanges(SqliteConnection conn, CollectionChanges c, string now)
        {
            var cols = ShelfColumns(conn);
            string? commonType = null;
            if (cols.Contains("Type"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Type FROM Shelf WHERE Type IS NOT NULL GROUP BY Type ORDER BY COUNT(*) DESC LIMIT 1";
                commonType = cmd.ExecuteScalar() as string;
            }
            using var tx = conn.BeginTransaction();

            foreach (var name in c.Created)
            {
                // A collection deleted earlier under the same name comes back; otherwise add a new one.
                var revived = Exec(conn, tx,
                    "UPDATE Shelf SET _IsDeleted = 'false', _IsVisible = 'true', _IsSynced = 'false', LastModified = $now WHERE Name = $name",
                    ("$now", now), ("$name", name));
                if (revived == 0)
                {
                    var names = new List<string>();
                    var values = new List<(string, string)>();
                    void Put(string col, string v)
                    {
                        if (cols.Contains(col))
                        {
                            names.Add(col);
                            values.Add(($"$p{values.Count}", v));
                        }
                    }
                    Put("CreationDate", now);
                    Put("Id", Guid.NewGuid().ToString());
                    Put("InternalName", name);
                    Put("LastModified", now);
                    Put("LastAccessed", now);
                    Put("Name", name);
                    if (commonType != null)
                    {
                        Put("Type", commonType);
                    }
                    Put("_IsDeleted", "false");
                    Put("_IsVisible", "true");
                    Put("_IsSynced", "false");
                    var sql = $"INSERT INTO Shelf ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", values.Select(v => v.Item1))})";
                    Exec(conn, tx, sql, values.ToArray());
                }
            }

            foreach (var name in c.Deleted)
            {
                Exec(conn, tx, "UPDATE Shelf SET _IsDeleted = 'true', _IsSynced = 'false', LastModified = $now WHERE Name = $name",
                    ("$now", now), ("$name", name));
                Exec(conn, tx, "UPDATE ShelfContent SET _IsDeleted = 'true', _IsSynced = 'false', DateModified = $now WHERE ShelfName = $name",
                    ("$now", now), ("$name", name));
            }

            foreach (var (shelf, id) in c.Added)
            {
                var updated = Exec(conn, tx,
                    "UPDATE ShelfContent SET _IsDeleted = 'false', _IsSynced = 'false', DateModified = $now WHERE ShelfName = $shelf AND ContentId = $id",
                    ("$now", now), ("$shelf", shelf), ("$id", id));
                if (updated == 0)
                {
                    Exec(conn, tx,
                        "INSERT INTO ShelfContent (ShelfName, ContentId, DateModified, _IsDeleted, _IsSynced) VALUES ($shelf, $id, $now, 'false', 'false')",
                        ("$shelf", shelf), ("$id", id), ("$now", now));
                }
            }

            foreach (var (shelf, id) in c.Removed)
            {
                Exec(conn, tx,
                    "UPDATE ShelfContent SET _IsDeleted = 'true', _IsSynced = 'false', DateModified = $now WHERE ShelfName = $shelf AND ContentId = $id",
                    ("$now", now), ("$shelf", shelf), ("$id", id));
            }

            // Collections whose books changed are marked changed too.
            var touched = c.Added.Select(a => a.Shelf).Concat(c.Removed.Select(r => r.Shelf)).Distinct(StringComparer.Ordinal);
            foreach (var shelf in touched)
            {
                Exec(conn, tx, "UPDATE Shelf SET LastModified = $now, _IsSynced = 'false' WHERE Name = $name",
                    ("$now", now), ("$name", shelf));
            }
            tx.Commit();
        }
    }
}
