using System.IO;
using System.Net.Http;
using System.Text;
using HoboLibris.Services;

namespace HoboLibris.Assistant.Sources
{
    /// <summary>
    /// Saves a downloaded e-book into the Kobo folder as "Title - Author.epub" (".kepub.epub" for a
    /// KEPUB), where Send to eReader finds it.  Only a real EPUB is kept: a web page or another kind
    /// of file is thrown away.  An existing file is never overwritten.
    /// </summary>
    public static class EbookDownloader
    {
        public const long MaxBytes = 300L * 1024 * 1024;

        /// <summary>The file name the book gets in the folder.</summary>
        public static string FileName(string title, string authors, string format)
        {
            var author = FileNames.FirstAuthor(authors.Trim());
            var stem = FileNames.Sanitize(author.Length > 0 ? $"{title} - {author}" : title);
            return stem + (format == "kepub" ? ".kepub.epub" : ".epub");
        }

        /// <summary>Whether the start of a file is an EPUB: a ZIP whose first entry says application/epub+zip.</summary>
        public static bool LooksLikeEpub(byte[] head, int length)
        {
            if (length < 58 || head[0] != 'P' || head[1] != 'K' || head[2] != 3 || head[3] != 4)
            {
                return false;
            }
            var text = Encoding.ASCII.GetString(head, 0, Math.Min(length, 200));
            return text.Contains("mimetype", StringComparison.Ordinal) && text.Contains("application/epub+zip", StringComparison.Ordinal);
        }

        /// <summary>Downloads one book; returns its path and whether it was already there.</summary>
        public static async Task<(string Path, bool AlreadyThere)> DownloadAsync(Uri url, HttpClient client, string folder,
            string title, string authors, string format, CancellationToken cancel)
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, FileName(title, authors, format));
            if (File.Exists(target))
            {
                return (target, true);
            }
            var temp = Path.Combine(folder, $".hobolibris-{Guid.NewGuid():N}.part");
            try
            {
                using (var turn = await SourceHttp.TurnAsync(url, cancel))
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel))
                {
                    SourceHttp.Check(response, url);
                    if (response.Content.Headers.ContentLength > MaxBytes)
                    {
                        throw new InvalidOperationException("The file is larger than 300 MB, so it wasn't downloaded.");
                    }
                    await using var input = await response.Content.ReadAsStreamAsync(cancel);
                    await using var output = File.Create(temp);
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                    {
                        total += read;
                        if (total > MaxBytes)
                        {
                            throw new InvalidOperationException("The file is larger than 300 MB, so it wasn't downloaded.");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    }
                }
                var head = new byte[200];
                int got;
                await using (var check = File.OpenRead(temp))
                {
                    got = await check.ReadAsync(head, cancel);
                }
                if (!LooksLikeEpub(head, got))
                {
                    throw new InvalidOperationException($"{url.Host} didn't send an EPUB (perhaps a web page or a protected file), so nothing was saved.");
                }
                // Another download of the same book may have finished meanwhile.
                if (File.Exists(target))
                {
                    return (target, true);
                }
                File.Move(temp, target);
                return (target, false);
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
