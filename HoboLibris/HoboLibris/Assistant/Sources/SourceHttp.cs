using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;

namespace HoboLibris.Assistant.Sources
{
    /// <summary>
    /// The web requests the e-book sources make: one client, a User-Agent naming HoboLibris and its
    /// project page (never the person's email address), and at most one request a second to each
    /// site, as Open Library and the Internet Archive ask.
    /// </summary>
    public static class SourceHttp
    {
        public static readonly string UserAgent =
            $"HoboLibris/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0"} (+https://github.com/paulfilkin/HoboLibris)";

        public static readonly HttpClient Client = Create();

        private static readonly Dictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> LastRequest = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

        private static HttpClient Create()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        /// <summary>Waits until a second has passed since the last request to the same site.</summary>
        public static async Task<IDisposable> TurnAsync(Uri uri, CancellationToken cancel)
        {
            SemaphoreSlim gate;
            lock (Gates)
            {
                if (!Gates.TryGetValue(uri.Host, out gate!))
                {
                    gate = new SemaphoreSlim(1, 1);
                    Gates[uri.Host] = gate;
                }
            }
            await gate.WaitAsync(cancel);
            DateTime last;
            lock (Gates)
            {
                LastRequest.TryGetValue(uri.Host, out last);
            }
            var wait = last + Interval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancel);
            }
            return new Turn(uri.Host, gate);
        }

        private sealed class Turn : IDisposable
        {
            private readonly string _host;
            private readonly SemaphoreSlim _gate;
            private bool _done;

            public Turn(string host, SemaphoreSlim gate)
            {
                _host = host;
                _gate = gate;
            }

            public void Dispose()
            {
                if (_done)
                {
                    return;
                }
                _done = true;
                lock (Gates)
                {
                    LastRequest[_host] = DateTime.UtcNow;
                }
                _gate.Release();
            }
        }

        /// <summary>GETs JSON, one request a second per site.</summary>
        public static async Task<JsonNode> GetJsonAsync(Uri uri, CancellationToken cancel, HttpClient? client = null)
        {
            using var turn = await TurnAsync(uri, cancel);
            using var response = await (client ?? Client).GetAsync(uri, cancel);
            Check(response, uri);
            var text = await response.Content.ReadAsStringAsync(cancel);
            return JsonNode.Parse(text) ?? throw new InvalidOperationException($"{uri.Host} sent an empty answer.");
        }

        /// <summary>GETs text (an OPDS feed), one request a second per site.</summary>
        public static async Task<string> GetTextAsync(Uri uri, CancellationToken cancel, HttpClient? client = null)
        {
            using var turn = await TurnAsync(uri, cancel);
            using var response = await (client ?? Client).GetAsync(uri, cancel);
            Check(response, uri);
            return await response.Content.ReadAsStringAsync(cancel);
        }

        /// <summary>A plain sentence for a refused request, naming the site but never the address (it may hold a key).</summary>
        public static void Check(HttpResponseMessage response, Uri uri)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }
            var code = (int)response.StatusCode;
            var why = code switch
            {
                401 or 403 => "refused the request (check the sign-in or key in Settings)",
                404 => "has no such page",
                429 => "says there have been too many requests; wait a while",
                >= 500 => "isn't answering properly at the moment",
                _ => "refused the request",
            };
            throw new InvalidOperationException($"{uri.Host} {why} ({code}).");
        }
    }
}
