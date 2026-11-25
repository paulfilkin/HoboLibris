using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    /// <summary>A book on the wishlist in the person's Kobo account (#59).</summary>
    public record WishlistItem(string CrossRevisionId, string Id, string Title, string Author, string Isbn, string Slug,
        decimal? Price, string Currency, bool PreOrder, bool HasPreview, DateTime? Added)
    {
        /// <summary>The book's page in the Kobo store; kobo.com sends it on to the person's own country.</summary>
        public string StoreUrl => Slug.Length > 0
            ? "https://www.kobo.com/ebook/" + Uri.EscapeDataString(Slug)
            : Previews.SearchUrl(Isbn.Length > 0 ? Isbn : (Title + " " + Author).Trim());

        public string PriceText => Price is { } p
            ? (Currency.Length > 0 ? $"{p.ToString("0.00", CultureInfo.CurrentCulture)} {Currency}" : p.ToString("0.00", CultureInfo.CurrentCulture))
            : "";
    }

    /// <summary>
    /// The wishlist in the person's Kobo account (#59).  As found on 25 November 2026: the store's
    /// initialisation lists user_wishlist ("https://storeapi.kobo.com/v1/user/wishlist"), and a GET on it
    /// returns Items, each with DateAdded, CrossRevisionId and ProductMetadata.Book (Title, Contributors,
    /// ISBN, Slug, Price, IsPreOrder, HasPreview), in pages of ItemsPerPage (100) with CurrentPageIndex and
    /// TotalPageCount.  The store lists no address for changing it.
    ///
    /// The eReader changes it instead.  Its Wishlist table (CrossRevisionId, DateModified, IsAdded,
    /// IsSynced) holds its changes: a book added on the eReader while online reached the account at once,
    /// with a row marked IsAdded 'true' and IsSynced 'true', and its content row's WishlistedDate set.  A
    /// row changed to IsAdded 'false' and IsSynced 'false' while the eReader was plugged in took the book
    /// off the wishlist in the account at the eReader's next sync, which then deleted the row.  A preview
    /// added the same way (IsAdded 'true', IsSynced 'false') reached the account at the next sync, with
    /// the row's DateModified as its DateAdded, and the row was deleted too (both tested on 25 November
    /// 2026 with firmware 4.46).
    /// </summary>
    public static class Wishlist
    {
        public const string Path = "/v1/user/wishlist";

        /// <summary>One page of the reply: its items, the page it is, and how many pages there are.</summary>
        public static (List<WishlistItem> Items, int Page, int Pages) Parse(string json)
        {
            JsonNode? v;
            try
            {
                v = JsonNode.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Wish_UnexpectedReply, ex.Message), ex);
            }
            var items = new List<WishlistItem>();
            if (v?["Items"] is JsonArray list)
            {
                foreach (var item in list)
                {
                    var book = item?["ProductMetadata"]?["Book"];
                    if (book == null)
                    {
                        continue;
                    }
                    var cross = Text(item?["CrossRevisionId"]);
                    if (cross.Length == 0)
                    {
                        cross = Text(book["CrossRevisionId"]);
                    }
                    var title = Text(book["Title"]);
                    decimal? price = book["Price"]?["Price"] is JsonValue pv && pv.TryGetValue<decimal>(out var p) ? p : null;
                    items.Add(new WishlistItem(cross, Text(book["Id"]), title.Length > 0 ? title : Strings.Store_Untitled,
                        Authors(book), Text(book["ISBN"]), Text(book["Slug"]), price, Text(book["Price"]?["Currency"]),
                        Flag(book["IsPreOrder"]), Flag(book["HasPreview"]), Time(Text(item?["DateAdded"]))));
                }
            }
            return (items, Int(v?["CurrentPageIndex"]), Math.Max(1, Int(v?["TotalPageCount"])));
        }

        /// <summary>
        /// The changes waiting on the eReader for its next sync, by CrossRevisionId: true for an add, false
        /// for a removal.  Empty when the book list has no Wishlist table.
        /// </summary>
        public static Dictionary<string, bool> Waiting(string db)
        {
            var waiting = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = KoboDeviceService.OpenReadOnly(db);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT CrossRevisionId, IsAdded FROM Wishlist WHERE IFNULL(IsSynced, 'false') IN ('false', '0') AND CrossRevisionId IS NOT NULL";
                using var rows = cmd.ExecuteReader();
                while (rows.Read())
                {
                    waiting[rows.GetString(0)] = IsTrue(rows.GetValue(1));
                }
            }
            catch (SqliteException ex)
            {
                LogManager.Debug($"No wishlist changes read from the eReader: {ex.Message}");
            }
            return waiting;
        }

        private static bool IsTrue(object? value) => value?.ToString()?.Trim().ToLowerInvariant() is "true" or "1";

        /// <summary>
        /// Leave wishlist changes on the eReader whose drive root is root, for it to send to the account at
        /// its next sync: a Wishlist row per book, by CrossRevisionId, marked IsAdded 'true' for an add or
        /// 'false' for a removal, and IsSynced 'false'.  An added book's content row gets a WishlistedDate,
        /// as the eReader's own add left it.  The book list is copied to backupDir first, and returned.
        /// </summary>
        public static string Change(string root, IReadOnlyCollection<(string CrossRevisionId, bool Add)> changes, string backupDir)
        {
            var db = KoboDeviceService.DatabasePath(root);
            if (!System.IO.File.Exists(db))
            {
                throw new HoboLibrisException(Strings.Coll_ReaderGone);
            }
            var backup = BookListBackups.Take(db, backupDir);
            var now = DateTime.UtcNow;
            try
            {
                // Not pooled, so the file is closed even when writing fails (the eReader can then be ejected).
                using var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False");
                conn.Open();
                using var tx = conn.BeginTransaction();
                foreach (var (cross, add) in changes.Where(c => c.CrossRevisionId.Length > 0))
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT OR REPLACE INTO Wishlist (CrossRevisionId, DateModified, IsAdded, IsSynced) VALUES ($id, $time, $added, 'false')";
                        cmd.Parameters.AddWithValue("$id", cross);
                        cmd.Parameters.AddWithValue("$time", now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                        cmd.Parameters.AddWithValue("$added", add ? "true" : "false");
                        cmd.ExecuteNonQuery();
                    }
                    if (add)
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = "UPDATE content SET WishlistedDate = $time WHERE CrossRevisionId = $id AND ContentType = 6";
                        cmd.Parameters.AddWithValue("$id", cross);
                        cmd.Parameters.AddWithValue("$time", now.ToString("yyyy-MM-dd'T'HH:mm:ss'.0000000Z'", CultureInfo.InvariantCulture));
                        try
                        {
                            cmd.ExecuteNonQuery();
                        }
                        catch (SqliteException ex) when (ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
                        {
                            // An older book list without WishlistedDate; the Wishlist row is what is sent.
                        }
                    }
                }
                tx.Commit();
            }
            catch (SqliteException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Wish_CouldNotChange, ex.Message), ex);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
            return backup;
        }

        private static string Text(JsonNode? v) => v is JsonValue value && value.TryGetValue<string>(out var s) ? s.Trim() : "";

        private static bool Flag(JsonNode? v) => v is JsonValue value && value.TryGetValue<bool>(out var b) && b;

        private static int Int(JsonNode? v) => v is JsonValue value && value.TryGetValue<int>(out var n) ? n : 0;

        private static DateTime? Time(string text) =>
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

        /// <summary>The authors from ContributorRoles, or Contributors when it has none.</summary>
        private static string Authors(JsonNode book)
        {
            var names = new List<string>();
            if (book["ContributorRoles"] is JsonArray roles)
            {
                foreach (var c in roles)
                {
                    var role = Text(c?["Role"]);
                    var name = Text(c?["Name"]);
                    if (name.Length > 0 && (role.Length == 0 || role.Equals("author", StringComparison.OrdinalIgnoreCase)) && !names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }
            return names.Count > 0 ? string.Join(", ", names) : Text(book["Contributors"]);
        }
    }
}
