using System.Text.Json.Serialization;

namespace HoboLibris.Assistant.Sources
{
    /// <summary>An OPDS catalogue the person added.  Its address, user name and password are encrypted.</summary>
    public sealed class OpdsCatalogue
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("address")]
        public string Address { get; set; } = "";

        [JsonPropertyName("user")]
        public string User { get; set; } = "";

        [JsonPropertyName("password")]
        public string Password { get; set; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        public string GetAddress() => SecretStore.Unprotect(Address);
        public string GetUserName() => SecretStore.Unprotect(User);
        public string GetPassword() => SecretStore.Unprotect(Password);

        public static OpdsCatalogue Create(string name, string address, string user, string password) => new()
        {
            Name = name.Trim(),
            Address = SecretStore.Protect(address.Trim()),
            User = SecretStore.Protect(user.Trim()),
            Password = SecretStore.Protect(password),
        };

        /// <summary>The server, without the path (which may hold a key), for the Settings list.</summary>
        [JsonIgnore]
        public string Server => Uri.TryCreate(GetAddress(), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "";
    }

    /// <summary>Settings › E-book sources, kept in settings.json under "assistant" › "sources".</summary>
    public sealed class SourceSettings
    {
        [JsonPropertyName("gutenberg")]
        public bool Gutenberg { get; set; } = true;

        [JsonPropertyName("open_library")]
        public bool OpenLibrary { get; set; } = true;

        [JsonPropertyName("google_books")]
        public bool GoogleBooks { get; set; } = true;

        /// <summary>The Google Books API key, encrypted.</summary>
        [JsonPropertyName("google_books_key")]
        public string GoogleBooksKey { get; set; } = "";

        [JsonPropertyName("opds")]
        public List<OpdsCatalogue> Catalogues { get; set; } = new();

        /// <summary>
        /// What a search of Google Books or Open Library lists when the person didn't say (#91): "ask" (a
        /// card asks them), "free" (free books only) or "paid" (books for sale too).
        /// </summary>
        [JsonPropertyName("paid_books")]
        public string PaidBooks { get; set; } = PaidBooksAsk;

        public const string PaidBooksAsk = "ask";
        public const string PaidBooksFree = "free";
        public const string PaidBooksPaid = "paid";

        public string GetGoogleBooksKey() => SecretStore.Unprotect(GoogleBooksKey);

        public void SetGoogleBooksKey(string? key) =>
            GoogleBooksKey = string.IsNullOrWhiteSpace(key) ? "" : SecretStore.Protect(key.Trim());

        /// <summary>Google Books is offered only with a key.</summary>
        [JsonIgnore]
        public bool GoogleBooksReady => GoogleBooks && GetGoogleBooksKey().Length > 0;
    }
}
