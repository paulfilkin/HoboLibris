using System.Security.Cryptography;
using System.Text;

namespace HoboLibris.Assistant
{
    /// <summary>
    /// Short ids for books in what is sent to the model ("r-1a2b3c4d"), so file paths, which hold the
    /// Windows user name, never leave the computer.  The same book always gets the same id, so ids in
    /// a conversation kept between sessions still work.
    /// </summary>
    public static class BookIds
    {
        public const string Store = "s";
        public const string Reader = "r";
        public const string Folder = "f";
        public const string Kindle = "k";
        public const string Collection = "c";
        public const string Send = "e";
        public const string Article = "a";
        public const string Preview = "p";
        public const string Wish = "w";

        public static string For(string kind, string id) =>
            kind + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8].ToLowerInvariant();

        /// <summary>The items whose short ids were asked for, and the ids that matched nothing.</summary>
        public static (List<T> Found, List<string> Unknown) Resolve<T>(IEnumerable<string> shortIds, IEnumerable<T> items, Func<T, string> shortIdOf)
        {
            var byId = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                byId.TryAdd(shortIdOf(item), item);
            }
            var found = new List<T>();
            var unknown = new List<string>();
            foreach (var id in shortIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (byId.TryGetValue(id.Trim(), out var item))
                {
                    found.Add(item);
                }
                else
                {
                    unknown.Add(id);
                }
            }
            return (found, unknown);
        }
    }
}
