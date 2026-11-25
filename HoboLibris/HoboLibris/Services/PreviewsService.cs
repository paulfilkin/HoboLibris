using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    /// <summary>A store preview (free sample) on the Kobo eReader (#59).</summary>
    public record Preview(string Id, string Title, string Author, string Isbn, string CrossRevisionId, DateTime? Arrived, long Size)
    {
        /// <summary>The book in the Kobo store, found by its ISBN, or by its title and author when it has none.</summary>
        public string StoreUrl => Previews.SearchUrl(Isbn.Length > 0 ? Isbn : (Title + " " + Author).Trim());
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
    /// followed by "_searchresult" and no EntitlementId; and some come with a sync with neither.  One of
    /// the last kind came back at the next sync after its rows and file were removed from the eReader.
    /// Removing previews for good is left for later.
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
                    + $"IFNULL({Col("___SyncTime", "NULL")}, ''), IFNULL({Col("PreviewFileSize", "0")}, 0) "
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
                        found.Add(new Preview(id, title.Length > 0 ? title : id, rows.GetString(2).Trim(), rows.GetString(3).Trim(),
                            rows.GetString(4).Trim(), ParseTime(rows.GetValue(5)?.ToString()), ToLong(rows.GetValue(6))));
                    }
                }
                return found.OrderByDescending(p => p.Arrived ?? DateTime.MinValue).ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (SqliteException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Coll_CouldNotRead, ex.Message), ex);
            }
        }

        private static long ToLong(object? value) =>
            value != null && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        private static DateTime? ParseTime(string? text) =>
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    }
}
