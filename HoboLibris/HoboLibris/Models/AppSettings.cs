using System.Text.Json.Serialization;
using HoboLibris.Assistant;

namespace HoboLibris.Models
{
    /// <summary>
    /// Everything kept between runs, in %APPDATA%\HoboLibris\settings.json. The JSON names match
    /// the earlier HoboLibris build, so an existing sign-in and folder choice carry over.
    /// </summary>
    public class AppSettings
    {
        /// <summary>Where Kindle books are backed up.</summary>
        [JsonPropertyName("kindle_dir")]
        public string KindleDir { get; set; } = "";

        /// <summary>Extra folders the person added to look for Kindle books in.</summary>
        [JsonPropertyName("kindle_extra_dirs")]
        public List<string> KindleExtraDirs { get; set; } = new();

        /// <summary>Where books from a Kobo eReader are backed up (mirrors the Kobo folder).</summary>
        [JsonPropertyName("kobo_reader_dir")]
        public string KoboReaderDir { get; set; } = "";

        /// <summary>Send new books in the Kobo folder to the eReader as soon as it is plugged in.</summary>
        [JsonPropertyName("kobo_auto_send")]
        public bool KoboAutoSend { get; set; }

        /// <summary>
        /// Books to remove from the Kobo eReader the next time it is plugged in (paths on the
        /// eReader such as "Books/Title - Author.epub"), when removing them straight away failed.
        /// </summary>
        [JsonPropertyName("kobo_pending_removals")]
        public List<string> KoboPendingRemovals { get; set; } = new();

        [JsonPropertyName("device_id")]
        public string DeviceId { get; set; } = "";

        [JsonPropertyName("user_id")]
        public string UserId { get; set; } = "";

        [JsonPropertyName("user_key")]
        public string UserKey { get; set; } = "";

        [JsonPropertyName("email")]
        public string Email { get; set; } = "";

        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = "";

        /// <summary>The Kobo folder: store downloads and eReader copies both go here.</summary>
        [JsonPropertyName("download_dir")]
        public string DownloadDir { get; set; } = "";

        /// <summary>
        /// Where copies of the eReader's book list are kept before collections are saved. Empty means
        /// the default folder inside the data folder.
        /// </summary>
        [JsonPropertyName("book_list_backup_dir")]
        public string BookListBackupDir { get; set; } = "";

        /// <summary>"system" (follow Windows), "light" or "dark".</summary>
        [JsonPropertyName("theme")]
        public string Theme { get; set; } = "system";

        /// <summary>The Assistant: AI model, key, instructions, tool policies and the pane.</summary>
        [JsonPropertyName("assistant")]
        public AssistantSettings Assistant { get; set; } = new();

        [JsonPropertyName("library_sync_url")]
        public string LibrarySyncUrl { get; set; } = "";

        [JsonIgnore]
        public bool SignedIn => !string.IsNullOrEmpty(UserKey) && !string.IsNullOrEmpty(AccessToken);

        public AppSettings Clone()
        {
            var copy = (AppSettings)MemberwiseClone();
            copy.KindleExtraDirs = new List<string>(KindleExtraDirs);
            copy.KoboPendingRemovals = new List<string>(KoboPendingRemovals);
            return copy;
        }

        /// <summary>The settings after signing out: folders and device id kept, account forgotten.</summary>
        public AppSettings SignedOut() => new()
        {
            DeviceId = DeviceId,
            DownloadDir = DownloadDir,
            KindleDir = KindleDir,
            BookListBackupDir = BookListBackupDir,
            Theme = Theme,
            Assistant = Assistant,
            KindleExtraDirs = new List<string>(KindleExtraDirs),
            KoboReaderDir = KoboReaderDir,
            KoboAutoSend = KoboAutoSend,
            KoboPendingRemovals = new List<string>(KoboPendingRemovals),
        };
    }
}
