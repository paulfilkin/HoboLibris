using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    /// <summary>Where a preview on the eReader came from, as its ___UserID and EntitlementId show (#61).</summary>
    public enum PreviewKind
    {
        /// <summary>Opened on the eReader but not saved: no user id and no entitlement.  It came back at the next sync after being removed.</summary>
        NotSaved,

        /// <summary>Saved with Save Preview: the person's user id and an EntitlementId.  It is in My Books and in the account.</summary>
        Saved,

        /// <summary>Opened from the store's search results: the user id followed by "_searchresult", and no EntitlementId.</summary>
        SearchResult,
    }

    /// <summary>A store preview (free sample) on the Kobo eReader (#59).</summary>
    public record Preview(string Id, string Title, string Author, string Isbn, string CrossRevisionId, DateTime? Arrived, long Size,
        PreviewKind Kind = PreviewKind.NotSaved, bool Removing = false)
    {
        /// <summary>The book in the Kobo store, found by its ISBN, or by its title and author when it has none.</summary>
        public string StoreUrl => Previews.SearchUrl(Isbn.Length > 0 ? Isbn : (Title + " " + Author).Trim());

        /// <summary>Can be removed for good: a saved preview not already being removed, or one from a search (#61).</summary>
        public bool CanRemove => Kind switch
        {
            PreviewKind.Saved => !Removing,
            PreviewKind.SearchResult => true,
            _ => false,
        };
    }

    /// <summary>
    /// Store previews on the Kobo eReader (#59).  As found with firmware 4.46 on 25 November 2026: each
    /// is a content row with ContentType 6 and Accessibility 6, with its ISBN and CrossRevisionId, and its
    /// file in ".kobo\kepub" named by its ContentID, of PreviewFileSize bytes.  Its chapters are content
    /// rows with its id as BookID, with volume_shortcovers and volume_tabs rows.  A preview the eReader no
    /// longer holds keeps its content row with IsDownloaded 'false'.
    ///
    /// There are three kinds, by ___UserID: a preview saved with Save Preview is in the person's library
    /// (their user id and an EntitlementId); one read from the store's search results has the user id
    /// followed by "_searchresult" and no EntitlementId; and others opened on the eReader have neither.  One of
    /// the last kind came back at the next sync after its rows and file were removed from the eReader.
    ///
    /// Removing (#61): a saved preview is marked as the eReader's Remove from My Books marks it
    /// (___ExpirationStatus 3), and the account archives it at the next sync.  A preview from a search
    /// is removed as the eReader's Remove Download removes it.  One that wasn't saved isn't removed,
    /// since it comes back.
    /// </summary>
    public static class Previews
    {
        /// <summary>The Kobo store's search; kobo.com sends it on to the person's own country.</summary>
        public static string SearchUrl(string query) => "https://www.kobo.com/search?query=" + Uri.EscapeDataString(query);

        /// <summary>The previews on the Kobo eReader whose drive root is root, newest first.</summary>
        public static List<Preview> Read(string root)
        {
            var db = KoboDeviceService.DatabasePath(root);
            if (!File.Exists(db))
            {
                throw new HoboLibrisException(Strings.Coll_BookListNotFound);
            }
            return ReadDb(db);
        }

        public static List<Preview> ReadDb(string db)
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
                if (!cols.Contains("Accessibility"))
                {
                    return new List<Preview>();
                }
                // Columns an older book list may not have are read as empty.
                string Col(string name, string empty) => cols.Contains(name) ? $"\"{name}\"" : empty;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ContentID, IFNULL(Title, ''), IFNULL(Attribution, ''), "
                    + $"IFNULL({Col("ISBN", "NULL")}, ''), IFNULL({Col("CrossRevisionId", "NULL")}, ''), "
                    + $"IFNULL({Col("___SyncTime", "NULL")}, ''), IFNULL({Col("PreviewFileSize", "0")}, 0), "
                    + $"IFNULL({Col("___UserID", "NULL")}, ''), IFNULL({Col("EntitlementId", "NULL")}, ''), IFNULL({Col("___ExpirationStatus", "0")}, 0) "
                    + "FROM content WHERE ContentType = 6 AND Accessibility = 6"
                    // No longer held by the eReader: the row stays, the file has gone.
                    + (cols.Contains("IsDownloaded") ? " AND IFNULL(IsDownloaded, 'true') NOT IN ('false', '0')" : "");
                var found = new List<Preview>();
                using (var rows = cmd.ExecuteReader())
                {
                    while (rows.Read())
                    {
                        var id = rows.GetString(0);
                        var title = rows.GetString(1).Trim();
                        var user = rows.GetValue(7)?.ToString()?.Trim() ?? "";
                        var kind = KindOf(user, rows.GetValue(8)?.ToString()?.Trim() ?? "");
                        // Marked by Remove from My Books, or already archived by a sync.
                        var removing = kind == PreviewKind.Saved && (ToLong(rows.GetValue(9)) == ExpiredStatus || user == "removed");
                        found.Add(new Preview(id, title.Length > 0 ? title : id, rows.GetString(2).Trim(), rows.GetString(3).Trim(),
                            rows.GetString(4).Trim(), ParseTime(rows.GetValue(5)?.ToString()), ToLong(rows.GetValue(6)), kind, removing));
                    }
                }
                return found.OrderByDescending(p => p.Arrived ?? DateTime.MinValue).ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (SqliteException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotRead, ex.Message), ex);
            }
        }

        /// <summary>The ___ExpirationStatus the eReader's Remove from My Books sets.</summary>
        public const long ExpiredStatus = 3;

        /// <summary>The kind of preview, from its ___UserID and EntitlementId.  "removed" is what a sync leaves after archiving a saved one.</summary>
        public static PreviewKind KindOf(string userId, string entitlementId)
        {
            if (userId.EndsWith("_searchresult", StringComparison.OrdinalIgnoreCase))
            {
                return PreviewKind.SearchResult;
            }
            if (userId == "removed" || (userId.Length > 0 && entitlementId.Length > 0))
            {
                return PreviewKind.Saved;
            }
            return PreviewKind.NotSaved;
        }

        /// <summary>
        /// Remove these previews from the eReader whose drive root is root, by kind (#61).  A saved preview
        /// gets ___ExpirationStatus 3, as Remove from My Books on the eReader leaves it, and the account
        /// archives it at the next sync.  A preview from a search is removed as Remove Download removes it:
        /// IsDownloaded 'false', no AnnotationsSyncToken, the rows with its id as BookID (chapters and table
        /// of contents entries), its volume_shortcovers rows and its file deleted, and its own row kept.
        /// Others are left alone.  The book list is copied to backupDir
        /// first, and returned.
        /// </summary>
        public static string Remove(string root, IReadOnlyCollection<Preview> previews, string backupDir)
        {
            var db = KoboDeviceService.DatabasePath(root);
            if (!File.Exists(db))
            {
                throw new HoboLibrisException(Strings.Coll_ReaderGone);
            }
            var targets = previews.Where(p => p.CanRemove).ToList();
            var backup = BookListBackups.Take(db, backupDir);
            var downloads = new List<string>();
            try
            {
                // Not pooled, so the file is closed even when writing fails (the eReader can then be ejected).
                using var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False");
                conn.Open();
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
                using var tx = conn.BeginTransaction();
                void Run(string sql, string id)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                const string Row = "WHERE ContentID = $id AND ContentType = 6 AND Accessibility = 6";
                foreach (var p in targets)
                {
                    if (p.Kind == PreviewKind.Saved)
                    {
                        if (!cols.Contains("___ExpirationStatus"))
                        {
                            throw new HoboLibrisException(string.Format(Strings.Prev_CouldNotRemove, "___ExpirationStatus"));
                        }
                        Run($"UPDATE content SET ___ExpirationStatus = {ExpiredStatus} {Row}", p.Id);
                        continue;
                    }
                    Run("UPDATE content SET IsDownloaded = 'false'"
                        + (cols.Contains("AnnotationsSyncToken") ? ", AnnotationsSyncToken = NULL " : " ") + Row, p.Id);
                    // Its chapters (ContentType 9) and table of contents entries (899): the eReader leaves neither.
                    Run("DELETE FROM content WHERE BookID = $id", p.Id);
                    Run("DELETE FROM volume_shortcovers WHERE volumeId = $id", p.Id);
                    downloads.Add(p.Id);
                }
                tx.Commit();
            }
            catch (SqliteException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Prev_CouldNotRemove, ex.Message), ex);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
            foreach (var id in downloads.Where(d => d.IndexOfAny(Path.GetInvalidFileNameChars()) < 0))
            {
                var file = Path.Combine(root, ".kobo", "kepub", id);
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex)
                {
                    // The rows say it isn't downloaded, so the eReader no longer lists it; the file is only space.
                    LogManager.Warn($"Could not remove {file}: {ex.Message}");
                }
            }
            return backup;
        }

        private static long ToLong(object? value) =>
            value != null && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        private static DateTime? ParseTime(string? text) =>
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    }
}
