namespace HoboLibris.Services
{
    /// <summary>Whether two titles name the same book.</summary>
    public static class TitleMatching
    {
        /// <summary>Letters and digits only, lower case, so small differences in punctuation don't matter.</summary>
        public static string Normalise(string title)
        {
            var chars = title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant);
            return new string(chars.ToArray());
        }

        private static string BeforeColon(string title)
        {
            var i = title.IndexOfAny(new[] { ':', '(' });
            return i < 0 ? title : title[..i];
        }

        /// <summary>Equal, or equal once a subtitle (after ":" or "(") is dropped.</summary>
        public static bool SameTitle(string a, string b)
        {
            var na = Normalise(a);
            var nb = Normalise(b);
            if (na.Length == 0 || nb.Length == 0)
            {
                return false;
            }
            if (na == nb)
            {
                return true;
            }
            var sa = Normalise(BeforeColon(a));
            var sb = Normalise(BeforeColon(b));
            return (sa.Length >= 6 && sa == nb) || (sb.Length >= 6 && na == sb);
        }
    }
}
