using HoboLibris.Resources;

namespace HoboLibris.Models
{
    public enum VerdictKind
    {
        /// <summary>Not on the eReader and fine to open there.</summary>
        Ready,
        /// <summary>Already on the eReader.</summary>
        OnReader,
        /// <summary>A Kobo store book: the eReader gets it from the Kobo account.</summary>
        StoreBook,
        /// <summary>Copy-protected: may not open on this eReader.</summary>
        Protected,
        /// <summary>Not something a Kobo opens, or a damaged file.</summary>
        NotKobo,
        /// <summary>Fine to open on a Kobo, but the eReader isn't plugged in, so whether it's on it is unknown.</summary>
        Unchecked,
    }

    /// <summary>What sending a file from the Kobo folder would do, and why.</summary>
    public record Verdict(VerdictKind Kind, string? Reason = null)
    {
        public static readonly Verdict Ready = new(VerdictKind.Ready);
        public static readonly Verdict OnReader = new(VerdictKind.OnReader);
        public static readonly Verdict StoreBook = new(VerdictKind.StoreBook);
        public static readonly Verdict Unchecked = new(VerdictKind.Unchecked);
        public static Verdict Protected(string why) => new(VerdictKind.Protected, why);
        public static Verdict NotKobo(string why) => new(VerdictKind.NotKobo, why);

        public string Label => Kind switch
        {
            VerdictKind.Ready => Strings.Send_Verdict_Ready,
            VerdictKind.OnReader => Strings.Send_Verdict_OnReader,
            VerdictKind.StoreBook => Strings.Send_Verdict_StoreBook,
            VerdictKind.Protected => Strings.Send_Verdict_Protected,
            VerdictKind.NotKobo => Strings.Send_Verdict_NotKobo,
            _ => Strings.Send_Verdict_Unchecked,
        };

        public string Why => Kind switch
        {
            VerdictKind.Ready => Strings.Send_Why_Ready,
            VerdictKind.OnReader => Strings.Send_Why_OnReader,
            VerdictKind.StoreBook => Strings.Send_Why_StoreBook,
            VerdictKind.Unchecked => Strings.Send_Why_Unchecked,
            _ => Reason ?? "",
        };

        /// <summary>Ready and Protected books can be sent (the latter with "Send anyway").</summary>
        public bool CanSend => Kind is VerdictKind.Ready or VerdictKind.Protected;
    }

    /// <summary>A book file in the Kobo folder, as the Send to eReader screen sees it.</summary>
    public class SendItem
    {
        public string Path { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>From the file name ("Title - Author.ext"); empty when it doesn't say.</summary>
        public string Author { get; set; } = "";
        public string Format { get; set; } = "";
        public long Size { get; set; }
        public Verdict Verdict { get; set; } = Verdict.Unchecked;

        public string Id => Path;
        public string FileName => System.IO.Path.GetFileName(Path);
    }
}
