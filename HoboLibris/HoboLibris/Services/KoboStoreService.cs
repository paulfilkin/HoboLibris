using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using HoboLibris.Models;
using HoboLibris.Resources;

namespace HoboLibris.Services
{
    /// <summary>
    /// Talking to the Kobo store: device sign-in, the library list and book downloads.
    /// The rules come from Docs\HoboLibris-Mechanics.md (read from the Kobo desktop program).
    /// </summary>
    public class KoboStoreService
    {
        public const string StoreHost = "https://storeapi.kobo.com";
        public const string AuthDevicePath = "/v1/auth/device";
        public const string AuthRefreshPath = "/v1/auth/refresh";
        public const string InitPath = "/v1/initialization";
        public const string LibrarySyncPath = "/v1/library/sync"; // estimated, see the mechanics notes
        public const string AppVersion = "4.38.23483";
        public const string Affiliate = "Kobo"; // estimated
        public const string PlatformId = "00000000-0000-0000-0000-000000004000"; // estimated
        public const string ReturnUrl = "kobo://UserAuthenticated";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) HoboLibris/1.0";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(20),
                AutomaticDecompression = DecompressionMethods.All,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        private static string ClientKey() => Convert.ToBase64String(Encoding.UTF8.GetBytes(PlatformId));

        private static void StoreTokens(AppSettings s, JsonNode? v)
        {
            if (v?["AccessToken"]?.GetValue<string>() is { Length: > 0 } access)
            {
                s.AccessToken = access;
            }
            if (v?["RefreshToken"]?.GetValue<string>() is { Length: > 0 } refresh)
            {
                s.RefreshToken = refresh;
            }
            // An anonymous device sign-in also returns a UserKey of its own; it is not the
            // account's, so it is not stored.
        }

        private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    if (body.Length > 300)
                    {
                        body = body[..300];
                    }
                    var status = (int)response.StatusCode;
                    response.Dispose();
                    throw new StoreStatusException(status, string.Format(Strings.Store_ServerAnswered, status, body));
                }
                return response;
            }
            catch (HttpRequestException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Store_CouldNotReach, ex.Message), ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new HoboLibrisException(string.Format(Strings.Store_CouldNotReach, ex.Message), ex);
            }
        }

        private static async Task<JsonNode?> PostJsonAsync(string url, JsonObject body, string? bearer, string unexpectedFormat, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(bearer))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            using var response = await SendAsync(request, ct);
            try
            {
                return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (JsonException ex)
            {
                throw new HoboLibrisException(string.Format(unexpectedFormat, ex.Message), ex);
            }
        }

        /// <summary>POST /v1/auth/device. With withUser, sends the account's UserKey.</summary>
        public async Task AuthDeviceAsync(AppSettings s, bool withUser, CancellationToken ct = default)
        {
            var body = new JsonObject
            {
                ["AffiliateName"] = Affiliate,
                ["AppVersion"] = AppVersion,
                ["ClientKey"] = ClientKey(),
                ["DeviceId"] = s.DeviceId,
                ["PlatformId"] = PlatformId,
            };
            if (withUser && !string.IsNullOrEmpty(s.UserKey))
            {
                body["UserKey"] = s.UserKey;
            }
            JsonNode? v;
            try
            {
                v = await PostJsonAsync(StoreHost + AuthDevicePath, body, null, Strings.Store_UnexpectedSignInReply, ct);
            }
            catch (StoreStatusException ex)
            {
                throw new HoboLibrisException(ex.Message, ex);
            }
            StoreTokens(s, v);
            if (string.IsNullOrEmpty(s.AccessToken))
            {
                throw new HoboLibrisException(Strings.Store_NoAccessToken);
            }
        }

        /// <summary>POST /v1/auth/refresh; falls back to a fresh device sign-in with the UserKey.</summary>
        public async Task RefreshAsync(AppSettings s, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(s.RefreshToken))
            {
                var body = new JsonObject
                {
                    ["AppVersion"] = AppVersion,
                    ["ClientKey"] = ClientKey(),
                    ["PlatformId"] = PlatformId,
                    ["RefreshToken"] = s.RefreshToken,
                };
                try
                {
                    var v = await PostJsonAsync(StoreHost + AuthRefreshPath, body, s.AccessToken, Strings.Store_UnexpectedSignInReply, ct);
                    StoreTokens(s, v);
                    SettingsService.Save(s);
                    return;
                }
                catch (Exception ex) when (ex is HoboLibrisException or StoreStatusException)
                {
                    LogManager.Info($"Token refresh failed, signing the device in again: {ex.Message}");
                }
            }
            await AuthDeviceAsync(s, true, ct);
            SettingsService.Save(s);
        }

        /// <summary>GET with the bearer token, refreshing once on 401.</summary>
        private async Task<HttpResponseMessage> GetAuthedAsync(AppSettings s, string url, IEnumerable<KeyValuePair<string, string>>? extra, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AccessToken);
                if (extra != null)
                {
                    foreach (var (k, v) in extra)
                    {
                        request.Headers.TryAddWithoutValidation(k, v);
                    }
                }
                try
                {
                    return await SendAsync(request, ct);
                }
                catch (StoreStatusException ex) when (ex.Status == 401 && attempt == 0)
                {
                    await RefreshAsync(s, ct);
                }
                catch (StoreStatusException ex)
                {
                    throw new HoboLibrisException(ex.Message, ex);
                }
            }
            throw new HoboLibrisException(Strings.Store_SignInExpired);
        }

        /// <summary>GET /v1/initialization and return its Resources object.</summary>
        public async Task<JsonNode> InitializationAsync(AppSettings s, CancellationToken ct = default)
        {
            using var response = await GetAuthedAsync(s, StoreHost + InitPath, null, ct);
            JsonNode? v;
            try
            {
                v = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (JsonException ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Store_UnexpectedStartupReply, ex.Message), ex);
            }
            if (v == null)
            {
                throw new HoboLibrisException(string.Format(Strings.Store_UnexpectedStartupReply, "empty"));
            }
            return v["Resources"] ?? v;
        }

        /// <summary>The sign-in page address with the device parameters the original sends.</summary>
        public static string SignInUrl(AppSettings s, JsonNode resources)
        {
            var page = resources["sign_in_page"]?.GetValue<string>();
            if (string.IsNullOrEmpty(page) || !Uri.TryCreate(page, UriKind.Absolute, out var uri))
            {
                throw new HoboLibrisException(Strings.Store_NoSignInPage);
            }
            // The page address already carries a returnUrl of its own; replace it with ours.
            var existing = HttpUtility.ParseQueryString(uri.Query);
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var key in existing.AllKeys)
            {
                if (key == null || key.Equals("returnUrl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (var value in existing.GetValues(key) ?? Array.Empty<string>())
                {
                    pairs.Add(new(key, value));
                }
            }
            pairs.Add(new("wsa", Affiliate));
            pairs.Add(new("pwsav", AppVersion));
            pairs.Add(new("pwspid", PlatformId));
            pairs.Add(new("pwsdid", s.DeviceId));
            pairs.Add(new("pwsdm", "Desktop"));
            pairs.Add(new("pwspos", "Windows"));
            pairs.Add(new("pwspov", "10"));
            pairs.Add(new("pwspt", "Desktop"));
            pairs.Add(new("wscfv", "1.5"));
            pairs.Add(new("returnUrl", ReturnUrl));
            var query = string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
            var builder = new UriBuilder(uri) { Query = query };
            return builder.Uri.ToString();
        }

        /// <summary>Read userId, userKey and email from the kobo://UserAuthenticated address.</summary>
        public static (string UserId, string UserKey, string Email) ParseReturnUrl(string text)
        {
            text = text.Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                throw new HoboLibrisException(Strings.Store_NotASignInLink);
            }
            var q = HttpUtility.ParseQueryString(uri.Query);
            string userId = "", userKey = "", email = "";
            foreach (var key in q.AllKeys)
            {
                if (key == null)
                {
                    continue;
                }
                var value = q[key] ?? "";
                switch (key.ToLowerInvariant())
                {
                    case "userid": userId = value; break;
                    case "userkey": userKey = value; break;
                    case "email": email = value; break;
                }
            }
            if (string.IsNullOrEmpty(userKey))
            {
                throw new HoboLibrisException(Strings.Store_LinkHasNoUserKey);
            }
            return (userId, userKey, email);
        }

        /// <summary>Finish sign-in once the user key is known.</summary>
        public async Task CompleteSignInAsync(AppSettings s, string userId, string userKey, string email, CancellationToken ct = default)
        {
            s.UserId = userId;
            s.UserKey = userKey;
            s.Email = email;
            s.AccessToken = "";
            s.RefreshToken = "";
            await AuthDeviceAsync(s, true, ct);
            try
            {
                var res = await InitializationAsync(s, ct);
                if (res["library_sync"]?.GetValue<string>() is { Length: > 0 } u)
                {
                    s.LibrarySyncUrl = u;
                }
            }
            catch (HoboLibrisException ex)
            {
                LogManager.Warn($"Initialisation after sign-in failed; using the default library address: {ex.Message}");
            }
            SettingsService.Save(s);
        }

        private static string TextOf(JsonNode? v)
        {
            if (v is JsonValue value && value.TryGetValue<string>(out var s))
            {
                return s;
            }
            return "";
        }

        private static string AuthorsOf(JsonNode meta)
        {
            var names = new List<string>();
            if (meta["ContributorRoles"] is JsonArray roles)
            {
                foreach (var c in roles)
                {
                    var role = TextOf(c?["Role"]);
                    if (role.Length == 0 || role.Equals("author", StringComparison.OrdinalIgnoreCase))
                    {
                        var n = TextOf(c?["Name"]);
                        if (n.Length > 0)
                        {
                            names.Add(n);
                        }
                    }
                }
            }
            if (names.Count == 0 && meta["Contributors"] is { } list)
            {
                if (list is JsonArray a)
                {
                    foreach (var c in a)
                    {
                        if (c is JsonValue cv && cv.TryGetValue<string>(out var n))
                        {
                            names.Add(n);
                        }
                        else
                        {
                            var name = TextOf(c?["Name"]);
                            if (name.Length > 0)
                            {
                                names.Add(name);
                            }
                        }
                    }
                }
                else if (list is JsonValue lv && lv.TryGetValue<string>(out var single))
                {
                    names.Add(single);
                }
            }
            // dedup removes consecutive duplicates only, as the earlier build did.
            var deduped = new List<string>();
            foreach (var n in names)
            {
                if (deduped.Count == 0 || deduped[^1] != n)
                {
                    deduped.Add(n);
                }
            }
            return string.Join(", ", deduped);
        }

        private static long LongOf(JsonNode? v)
        {
            if (v is JsonValue value)
            {
                if (value.TryGetValue<long>(out var l))
                {
                    return l;
                }
                if (value.TryGetValue<double>(out var d) && d >= 0)
                {
                    return (long)d;
                }
            }
            return 0;
        }

        /// <summary>One library item: the book's id, and the book (null when it was removed).</summary>
        private static (string Id, StoreBook? Book)? BookFrom(JsonNode? item)
        {
            var ent = item?["NewEntitlement"] ?? item?["ChangedEntitlement"];
            var be = ent?["BookEntitlement"];
            if (be == null)
            {
                return null;
            }
            var id = TextOf(be["Id"]);
            if (id.Length == 0)
            {
                id = TextOf(be["CrossRevisionId"]);
            }
            var removed = be["IsRemoved"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r;
            var meta = ent?["BookMetadata"];
            if (meta == null || removed)
            {
                return (id, null);
            }
            var links = new List<DownloadLink>();
            if (meta["DownloadUrls"] is JsonArray urls)
            {
                foreach (var d in urls)
                {
                    if (d == null)
                    {
                        continue;
                    }
                    links.Add(new DownloadLink
                    {
                        Drm = TextOf(d["DrmType"] ?? d["DRMType"]),
                        Format = TextOf(d["Format"]),
                        Url = TextOf(d["Url"]),
                        Platform = TextOf(d["Platform"]),
                        Size = LongOf(d["Size"] ?? d["ByteSize"]),
                    });
                }
            }
            var title = TextOf(meta["Title"]);
            return (id, new StoreBook
            {
                Id = id,
                Title = title.Length == 0 ? Strings.Store_Untitled : title,
                Authors = AuthorsOf(meta),
                Links = links,
            });
        }

        /// <summary>Fetch the whole library, page by page, following x-kobo-sync: continue.</summary>
        public async Task<List<StoreBook>> FetchLibraryAsync(AppSettings s, IProgress<int>? progress, CancellationToken ct = default)
        {
            var url = string.IsNullOrEmpty(s.LibrarySyncUrl) ? StoreHost + LibrarySyncPath : s.LibrarySyncUrl;
            var token = "";
            var books = new List<StoreBook>();
            var index = new Dictionary<string, int>();
            for (var page = 0; page < 500; page++)
            {
                var headers = token.Length == 0
                    ? Array.Empty<KeyValuePair<string, string>>()
                    : new[] { new KeyValuePair<string, string>("x-kobo-synctoken", token) };
                using var response = await GetAuthedAsync(s, url, headers, ct);
                var more = response.Headers.TryGetValues("x-kobo-sync", out var syncValues)
                           && syncValues.Any(h => h.Equals("continue", StringComparison.OrdinalIgnoreCase));
                var next = response.Headers.TryGetValues("x-kobo-synctoken", out var tokenValues)
                    ? tokenValues.FirstOrDefault() ?? ""
                    : "";
                JsonNode? v;
                try
                {
                    v = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                }
                catch (JsonException ex)
                {
                    throw new HoboLibrisException(string.Format(Strings.Store_UnexpectedLibraryReply, ex.Message), ex);
                }
                if (v is JsonArray items)
                {
                    foreach (var item in items)
                    {
                        var parsed = BookFrom(item);
                        if (parsed == null)
                        {
                            continue;
                        }
                        var (id, book) = parsed.Value;
                        var known = index.TryGetValue(id, out var i);
                        if (book != null && known)
                        {
                            books[i] = book;
                        }
                        else if (book != null)
                        {
                            index[id] = books.Count;
                            books.Add(book);
                        }
                        else if (known)
                        {
                            books[i].Links.Clear();
                        }
                    }
                }
                progress?.Report(books.Count);
                if (!more || next.Length == 0 || next == token)
                {
                    break;
                }
                token = next;
            }
            books.RemoveAll(b => b.Links.Count == 0 && b.Title.Length == 0);
            books.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            return books;
        }

        /// <summary>Download a book's file into dir, exactly as the store delivers it.</summary>
        public async Task<string> DownloadAsync(AppSettings s, StoreBook book, string dir, IProgress<double>? progress, CancellationToken ct = default)
        {
            var link = book.BestLink() ?? throw new HoboLibrisException(Strings.Store_NoDownload);
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Store_CouldNotCreateFolder, ex.Message), ex);
            }
            using var response = await GetAuthedAsync(s, link.Url, null, ct);
            var total = response.Content.Headers.ContentLength ?? link.Size;
            var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            var ext = contentType.Contains("adept") ? "acsm" : book.Extension;
            var stem = book.FileStem();
            var finalPath = Path.Combine(dir, $"{stem}.{ext}");
            var part = Path.Combine(dir, $"{stem}.part");
            long done = 0;
            try
            {
                await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await using (var reader = await response.Content.ReadAsStreamAsync(ct))
                {
                    var buffer = new byte[64 * 1024];
                    int n;
                    while ((n = await ReadAsync(reader, buffer, ct)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, n), ct);
                        done += n;
                        if (total > 0)
                        {
                            progress?.Report(Math.Min(1.0, (double)done / total));
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                TryDelete(part);
                throw new HoboLibrisException(string.Format(Strings.Store_CouldNotWriteFile, ex.Message), ex);
            }
            catch (HttpRequestException ex)
            {
                TryDelete(part);
                throw new HoboLibrisException(string.Format(Strings.Store_DownloadInterrupted, ex.Message), ex);
            }
            if (done == 0)
            {
                TryDelete(part);
                throw new HoboLibrisException(Strings.Store_DownloadEmpty);
            }
            try
            {
                File.Move(part, finalPath, overwrite: true);
            }
            catch (Exception ex)
            {
                throw new HoboLibrisException(string.Format(Strings.Store_CouldNotSaveFile, ex.Message), ex);
            }
            return finalPath;
        }

        private static async Task<int> ReadAsync(Stream reader, byte[] buffer, CancellationToken ct)
        {
            try
            {
                return await reader.ReadAsync(buffer, ct);
            }
            catch (IOException ex)
            {
                throw new HttpRequestException(ex.Message, ex);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                LogManager.Debug($"Could not delete {path}: {ex.Message}");
            }
        }

        /// <summary>A reply from the Kobo server with an error status.</summary>
        private class StoreStatusException : Exception
        {
            public int Status { get; }

            public StoreStatusException(int status, string message) : base(message)
            {
                Status = status;
            }
        }
    }
}
