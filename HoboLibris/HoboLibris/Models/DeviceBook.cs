using System.IO;

namespace HoboLibris.Models
{
    /// <summary>Where a book sits on a media-device Kindle (no drive letter), as names from the device down.</summary>
    public class DeviceRef
    {
        public string Device { get; set; } = "";
        public List<string> Path { get; set; } = new();
        /// <summary>The book's "name.sdr" folder, if it has one.</summary>
        public List<string>? Sidecar { get; set; }
    }

    /// <summary>A book found on a Kindle, in Kindle for PC, in a folder, or on a Kobo eReader.</summary>
    public class DeviceBook
    {
        /// <summary>The file or folder that is copied (also the book's id).</summary>
        public string Source { get; set; } = "";
        public string Title { get; set; } = "";
        public string Authors { get; set; } = "";
        public string Format { get; set; } = "";
        /// <summary>Where it came from, for the list: "Kindle (E:)", "Kindle for PC", "Kobo eReader (F:)".</summary>
        public string Origin { get; set; } = "";
        /// <summary>The drive ("E:") when it came from a device with a drive letter.</summary>
        public string? Drive { get; set; }
        public long Size { get; set; }
        /// <summary>Set for a book on a Kindle that connects as a media device (no drive letter).</summary>
        public DeviceRef? Device { get; set; }
        /// <summary>
        /// Where the copy goes, relative to the backup folder, when it isn't the book's own file
        /// name (Kobo eReader books are named "Title - Author.ext").
        /// </summary>
        public string? TargetRel { get; set; }
        /// <summary>
        /// Why this book needn't be copied, e.g. it was already saved from the Kobo store. Such
        /// books are left out of "Back up all".
        /// </summary>
        public string? Duplicate { get; set; }

        public string Id => Source;

        public string FileName => Device != null
            ? (Device.Path.Count > 0 ? Device.Path[^1] : "")
            : System.IO.Path.GetFileName(Source);

        /// <summary>The copy's path: straight in the backup folder, under the book's own name.</summary>
        public string TargetIn(string dir) =>
            System.IO.Path.Combine(dir, TargetRel ?? FileName);

        /// <summary>
        /// The saved copy: under the name the app gives it, or under the book's own file name (a
        /// book sent from the Kobo folder to the eReader keeps its name there).
        /// </summary>
        public string? SavedIn(string dir)
        {
            var t = TargetIn(dir);
            if (File.Exists(t) || Directory.Exists(t))
            {
                return t;
            }
            var name = FileName;
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            var sameName = System.IO.Path.Combine(dir, name);
            return File.Exists(sameName) ? sameName : null;
        }
    }
}
